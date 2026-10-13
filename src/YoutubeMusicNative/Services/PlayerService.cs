using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Streams;
using YoutubeMusicNative.Infrastructure;
using YoutubeMusicNative.Models;
using WinMediaPlayer = Windows.Media.Playback.MediaPlayer;

namespace YoutubeMusicNative.Services;

public enum RepeatMode { Off, All, One }

/// <summary>
/// Playback queue on top of the OS media player (Media Foundation). Using the WinRT player gives
/// hardware media keys, the volume flyout and lock-screen controls (SMTC) for free.
/// </summary>
public sealed class PlayerService : ObservableObject, IDisposable
{
    private readonly WinMediaPlayer _mp = new() { AudioCategory = MediaPlayerAudioCategory.Media, AutoPlay = true };
    private readonly InnerTubeClient _api;
    private readonly StreamResolver _resolver;
    private readonly HttpClient _http;
    private readonly Settings _settings;
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly DispatcherTimer _timer;

    private int _playToken;
    private int _queueGeneration;
    private Continuation? _queueContinuation;
    private string? _queuePlaylistId;
    private bool _extendingQueue;

    public PlayerService(InnerTubeClient api, StreamResolver resolver, HttpClient http, Settings settings)
    {
        _api = api;
        _resolver = resolver;
        _http = http;
        _settings = settings;
        _volume = Math.Clamp(settings.Volume, 0, 100);
        _mp.Volume = _volume / 100.0;

        var cm = _mp.CommandManager;
        cm.IsEnabled = true;
        cm.NextBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        cm.PreviousBehavior.EnablingRule = MediaCommandEnablingRule.Always;
        cm.NextReceived += (_, e) => { e.Handled = true; _ui.InvokeAsync(Next); };
        cm.PreviousReceived += (_, e) => { e.Handled = true; _ui.InvokeAsync(Previous); };

        _mp.MediaEnded += (_, _) => _ui.InvokeAsync(OnEnded);
        _mp.MediaFailed += (_, e) => _ui.InvokeAsync(() => OnFailed(e.ErrorMessage));
        _mp.PlaybackSession.PlaybackStateChanged += (_, _) => _ui.InvokeAsync(UpdateState);

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick(), _ui);
    }

    // ---------- state ----------

    public ObservableCollection<MediaItem> Queue { get; } = [];

    private MediaItem? _current;
    public MediaItem? Current
    {
        get => _current;
        private set
        {
            if (_current is not null) _current.IsCurrent = false;
            if (Set(ref _current, value)) OnPropertyChanged(nameof(HasTrack));
            if (value is not null) value.IsCurrent = true;
        }
    }

    public bool HasTrack => Current is not null;
    public int CurrentIndex { get; private set; } = -1;

    private bool _isPlaying;
    public bool IsPlaying { get => _isPlaying; private set => Set(ref _isPlaying, value); }

    private bool _isBuffering;
    public bool IsBuffering { get => _isBuffering; private set => Set(ref _isBuffering, value); }

    private double _position;
    public double Position
    {
        get => _position;
        private set { if (Set(ref _position, value)) OnPropertyChanged(nameof(TimeText)); }
    }

    private double _duration;
    public double Duration
    {
        get => _duration;
        private set { if (Set(ref _duration, value)) OnPropertyChanged(nameof(TimeText)); }
    }

    public string TimeText => $"{Format(Position)} / {Format(Duration)}";

    /// <summary>Set by the seek bar while the user drags so the timer does not fight the thumb.</summary>
    public bool IsSeeking { get; set; }

    private double _volume;
    public double Volume
    {
        get => _volume;
        set
        {
            value = Math.Clamp(value, 0, 100);
            if (!Set(ref _volume, value)) return;
            _mp.Volume = value / 100.0;
            _settings.Volume = value;
            if (value > 0 && IsMuted) IsMuted = false;
        }
    }

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set { if (Set(ref _isMuted, value)) _mp.IsMuted = value; }
    }

    private bool _shuffle;
    public bool Shuffle { get => _shuffle; private set => Set(ref _shuffle, value); }

    private RepeatMode _repeat;
    public RepeatMode Repeat { get => _repeat; private set => Set(ref _repeat, value); }

    private string? _error;
    public string? Error { get => _error; private set => Set(ref _error, value); }

    // ---------- starting playback ----------

    /// <summary>Plays a song and fills "Up next" with its radio (like clicking a song on YouTube Music).</summary>
    public async Task PlayRadioAsync(MediaItem seed)
    {
        if (seed.VideoId is null) return;
        var generation = ReplaceQueue([seed.Clone()], 0, null, null);
        try
        {
            var q = await _api.GetQueueAsync(seed.VideoId, seed.PlaylistId ?? "RDAMVM" + seed.VideoId);
            if (generation != _queueGeneration) return;
            foreach (var item in q.Items.Where(i => i.VideoId != seed.VideoId)) Queue.Add(item);
            _queueContinuation = q.Next;
            _queuePlaylistId = q.PlaylistId;
            Prefetch();
        }
        catch
        {
            // Radio is a bonus; the seed song keeps playing.
        }
    }

    /// <summary>Plays a playlist/album by id (e.g. the play button on a card).</summary>
    public async Task PlayPlaylistAsync(string playlistId)
    {
        var generation = ++_queueGeneration;
        IsBuffering = true;
        try
        {
            var q = await _api.GetQueueAsync(null, playlistId);
            if (generation != _queueGeneration) return;
            if (q.Items.Count == 0) throw new InvalidOperationException("This playlist is empty.");
            ReplaceQueue(q.Items, 0, q.Next, q.PlaylistId);
        }
        catch (Exception ex)
        {
            if (generation != _queueGeneration) return;
            IsBuffering = false;
            Error = ex.Message;
        }
    }

    /// <summary>Plays a fixed list (album / playlist page) starting at <paramref name="index"/>.</summary>
    public void PlayList(IReadOnlyList<MediaItem> items, int index, bool shuffle = false)
    {
        var playable = items.Where(i => i.VideoId is not null).Select(i => i.Clone()).ToList();
        if (playable.Count == 0) return;
        index = Math.Clamp(index, 0, playable.Count - 1);
        if (shuffle)
        {
            index = Random.Shared.Next(playable.Count);
            (playable[0], playable[index]) = (playable[index], playable[0]);
            ShuffleRange(playable, 1);
            index = 0;
        }
        ReplaceQueue(playable, index, null, null);
        Shuffle = shuffle;
    }

    private int ReplaceQueue(IReadOnlyList<MediaItem> items, int index, Continuation? next, string? playlistId)
    {
        var generation = ++_queueGeneration;
        Queue.Clear();
        foreach (var item in items) Queue.Add(item);
        _queueContinuation = next;
        _queuePlaylistId = playlistId;
        Shuffle = false;
        PlayAt(index);
        return generation;
    }

    public void PlayNext(MediaItem item)
    {
        if (item.VideoId is null) return;
        if (Current is null) { PlayList([item], 0); return; }
        Queue.Insert(CurrentIndex + 1, item.Clone());
    }

    public void AddToQueue(MediaItem item)
    {
        if (item.VideoId is null) return;
        if (Current is null) { PlayList([item], 0); return; }
        Queue.Add(item.Clone());
    }

    // ---------- transport ----------

    public void PlayAt(int index)
    {
        if (index < 0 || index >= Queue.Count) return;
        CurrentIndex = index;
        Current = Queue[index];
        _ = PlayCurrentAsync();
    }

    public void PlayQueueItem(MediaItem item)
    {
        var index = Queue.IndexOf(item);
        if (index >= 0) PlayAt(index);
    }

    public void TogglePlayPause()
    {
        if (Current is null) return;
        if (_mp.Source is null) { _ = PlayCurrentAsync(); return; }
        if (_mp.PlaybackSession.PlaybackState == MediaPlaybackState.Playing) _mp.Pause();
        else _mp.Play();
    }

    public void Next()
    {
        if (Queue.Count == 0) return;
        if (CurrentIndex + 1 < Queue.Count) PlayAt(CurrentIndex + 1);
        else if (Repeat == RepeatMode.All) PlayAt(0);
    }

    public void Previous()
    {
        if (Position > 3 || CurrentIndex <= 0) Seek(0);
        else PlayAt(CurrentIndex - 1);
    }

    public void Seek(double seconds)
    {
        if (_mp.Source is null) return;
        _mp.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, seconds));
        Position = seconds;
    }

    public void ToggleShuffle()
    {
        Shuffle = !Shuffle;
        if (!Shuffle || CurrentIndex < 0) return;
        var tail = Queue.Skip(CurrentIndex + 1).ToList();
        ShuffleRange(tail, 0);
        for (var i = 0; i < tail.Count; i++) Queue[CurrentIndex + 1 + i] = tail[i];
    }

    public void CycleRepeat() => Repeat = Repeat switch
    {
        RepeatMode.Off => RepeatMode.All,
        RepeatMode.All => RepeatMode.One,
        _ => RepeatMode.Off,
    };

    // ---------- internals ----------

    private async Task PlayCurrentAsync()
    {
        var item = Current;
        if (item?.VideoId is null) return;

        var token = ++_playToken;
        Error = null;
        IsBuffering = true;
        Position = 0;
        Duration = ParseDuration(item.Duration);
        _mp.Source = null;

        try
        {
            var info = await _resolver.ResolveAsync(item.VideoId);
            if (token != _playToken) return;
            // googlevideo rejects Media Foundation's own range requests, so fetch the (~3-5 MB)
            // audio ourselves in &range= chunks and play it from memory.
            var data = await DownloadAsync(info);
            if (token != _playToken) return;
            var stream = new MemoryStream(data).AsRandomAccessStream();
            _mp.Source = BuildPlaybackItem(MediaSource.CreateFromStream(stream, info.MimeType), item);
            _mp.Play();
            Prefetch();
            _ = ExtendQueueIfNeededAsync();
        }
        catch (Exception ex)
        {
            if (token != _playToken) return;
            _resolver.Invalidate(item.VideoId);
            IsBuffering = false;
            Error = "Playback failed: " + ex.Message;
        }
    }

    private async Task<byte[]> DownloadAsync(StreamInfo info)
    {
        const long chunk = 1024 * 1024;
        using var buffer = new MemoryStream();
        if (info.ContentLength is not { } length)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, info.Url);
            req.Headers.TryAddWithoutValidation("User-Agent", info.UserAgent);
            using var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            await resp.Content.CopyToAsync(buffer);
            return buffer.ToArray();
        }

        for (long start = 0; start < length; start += chunk)
        {
            var end = Math.Min(start + chunk, length) - 1;
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{info.Url}&range={start}-{end}");
            req.Headers.TryAddWithoutValidation("User-Agent", info.UserAgent);
            using var resp = await _http.SendAsync(req);
            resp.EnsureSuccessStatusCode();
            await resp.Content.CopyToAsync(buffer);
        }
        return buffer.ToArray();
    }

    private static MediaPlaybackItem BuildPlaybackItem(MediaSource source, MediaItem item)
    {
        var playback = new MediaPlaybackItem(source);
        var props = playback.GetDisplayProperties();
        props.Type = MediaPlaybackType.Music;
        props.MusicProperties.Title = item.Title;
        props.MusicProperties.Artist = item.ArtistName ?? item.Subtitle ?? "";
        props.MusicProperties.AlbumTitle = item.AlbumName ?? "";
        if (item.Thumbnail is not null)
            props.Thumbnail = RandomAccessStreamReference.CreateFromUri(new Uri(Thumbs.Resize(item.Thumbnail, 300)));
        playback.ApplyDisplayProperties(props);
        return playback;
    }

    private void OnFailed(string message)
    {
        IsBuffering = false;
        Error = "Playback failed: " + message;
    }

    private void OnEnded()
    {
        if (Repeat == RepeatMode.One) { Seek(0); _mp.Play(); return; }
        if (CurrentIndex + 1 < Queue.Count || Repeat == RepeatMode.All) Next();
        else { IsPlaying = false; _timer.Stop(); }
    }

    private void UpdateState()
    {
        var state = _mp.PlaybackSession.PlaybackState;
        IsPlaying = state == MediaPlaybackState.Playing;
        IsBuffering = state is MediaPlaybackState.Opening or MediaPlaybackState.Buffering;
        if (IsPlaying) _timer.Start(); else _timer.Stop();
        Tick();
    }

    private void Tick()
    {
        var session = _mp.PlaybackSession;
        if (session.NaturalDuration > TimeSpan.Zero) Duration = session.NaturalDuration.TotalSeconds;
        if (!IsSeeking) Position = session.Position.TotalSeconds;
    }

    private void Prefetch()
    {
        if (CurrentIndex + 1 < Queue.Count && Queue[CurrentIndex + 1].VideoId is { } next)
            _ = _resolver.ResolveAsync(next).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>Keeps radio/autoplay going by loading more of the queue as it runs out.</summary>
    private async Task ExtendQueueIfNeededAsync()
    {
        if (_extendingQueue || _queueContinuation is null || Queue.Count - CurrentIndex > 3) return;
        _extendingQueue = true;
        var generation = _queueGeneration;
        try
        {
            var more = await _api.ContinueQueueAsync(_queueContinuation, _queuePlaylistId);
            if (generation != _queueGeneration) return;
            var known = Queue.Select(q => q.VideoId).ToHashSet();
            foreach (var item in more.Items.Where(i => known.Add(i.VideoId))) Queue.Add(item);
            _queueContinuation = more.Next;
        }
        catch
        {
            _queueContinuation = null;
        }
        finally
        {
            _extendingQueue = false;
        }
    }

    private static void ShuffleRange(IList<MediaItem> list, int start)
    {
        for (var i = list.Count - 1; i > start; i--)
        {
            var j = Random.Shared.Next(start, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private static double ParseDuration(string? text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        double total = 0;
        foreach (var part in text.Split(':'))
        {
            if (!int.TryParse(part, out var n)) return 0;
            total = total * 60 + n;
        }
        return total;
    }

    private static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");
    }

    public void Dispose()
    {
        _timer.Stop();
        _mp.Dispose();
    }
}
