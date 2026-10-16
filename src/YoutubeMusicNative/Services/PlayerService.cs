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
/// Playback queue on top of the OS media player (Media Foundation). Two players take turns so songs
/// can crossfade; the Windows media controls (media keys, lock screen) are driven by hand.
/// </summary>
public sealed class PlayerService : ObservableObject, IDisposable
{
    private readonly InnerTubeClient _api;
    private readonly StreamResolver _resolver;
    private readonly HttpClient _http;
    private readonly Settings _settings;
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _fadeTimer;
    private readonly SystemMediaTransportControls _smtc;

    /// <summary>Plays the current song.</summary>
    private WinMediaPlayer _mp;
    /// <summary>Idle, or playing the previous song while it fades out.</summary>
    private WinMediaPlayer _spare;
    private bool _fading;
    private DateTime _fadeStartedAt;
    private double _fadeSeconds;
    private double? _loudness, _spareLoudness;

    private sealed record PreparedAudio(ArraySegment<byte> Data, string Mime, double? LoudnessDb);

    private int _playToken;
    private int _queueGeneration;
    private DateTime _startedAt;
    private (string VideoId, Task<PreparedAudio> Audio)? _next;
    private bool _prepareQueued;
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
        _mp = CreatePlayer();
        _spare = CreatePlayer();
        ApplyVolume();

        _smtc = _mp.SystemMediaTransportControls;
        _smtc.IsEnabled = _smtc.IsPlayEnabled = _smtc.IsPauseEnabled = _smtc.IsNextEnabled = _smtc.IsPreviousEnabled = true;
        _smtc.ButtonPressed += (_, e) => _ui.InvokeAsync(() =>
        {
            switch (e.Button)
            {
                case SystemMediaTransportControlsButton.Play when !IsPlaying:
                case SystemMediaTransportControlsButton.Pause when IsPlaying:
                    TogglePlayPause();
                    break;
                case SystemMediaTransportControlsButton.Next: Next(); break;
                case SystemMediaTransportControlsButton.Previous: Previous(); break;
            }
        });
        _smtc.PlaybackPositionChangeRequested += (_, e) => _ui.InvokeAsync(() => Seek(e.RequestedPlaybackPosition.TotalSeconds));

        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Tick(), _ui);
        _fadeTimer = new DispatcherTimer(DispatcherPriority.Normal, _ui) { Interval = TimeSpan.FromMilliseconds(50) };
        _fadeTimer.Tick += (_, _) => { if (FadeProgress() >= 1) StopFade(); else ApplyVolume(); };
        Queue.CollectionChanged += (_, _) => QueuePrepareNext();
    }

    private WinMediaPlayer CreatePlayer()
    {
        var mp = new WinMediaPlayer { AudioCategory = MediaPlayerAudioCategory.Media, AutoPlay = true };
        // Both players feed one set of Windows media controls, updated by hand below.
        mp.CommandManager.IsEnabled = false;
        // Only the player for the current song drives the UI; a fading-out song ends silently.
        mp.MediaEnded += (_, _) => _ui.InvokeAsync(() => { if (mp == _mp) OnEnded(); });
        mp.MediaFailed += (_, e) => _ui.InvokeAsync(() => { if (mp == _mp) OnFailed(e.ErrorMessage); });
        mp.PlaybackSession.PlaybackStateChanged += (_, _) => _ui.InvokeAsync(() => { if (mp == _mp) UpdateState(); });
        return mp;
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
            ApplyVolume();
            _settings.Volume = value;
            if (value > 0 && IsMuted) IsMuted = false;
        }
    }

    private bool _isMuted;
    public bool IsMuted
    {
        get => _isMuted;
        set { if (Set(ref _isMuted, value)) _mp.IsMuted = _spare.IsMuted = value; }
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
        // Look up the stream now so the song is ready by the time it comes up.
        // (If it's the very next song, the queue watcher also downloads it.)
        _ = _resolver.ResolveAsync(item.VideoId).ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>
    /// Reordering rules: the playing song stays put, and other songs can't cross it. Upcoming songs
    /// are reordered among themselves, already-played songs among themselves.
    /// </summary>
    public bool CanMove(MediaItem item, int newIndex)
    {
        var from = Queue.IndexOf(item);
        if (from < 0 || newIndex < 0 || newIndex >= Queue.Count || from == newIndex) return false;
        if (CurrentIndex < 0) return true;
        if (from == CurrentIndex) return false;
        return from < CurrentIndex ? newIndex < CurrentIndex : newIndex > CurrentIndex;
    }

    public void Move(MediaItem item, int newIndex)
    {
        if (CanMove(item, newIndex)) Queue.Move(Queue.IndexOf(item), newIndex);
    }

    /// <summary>Removes a song from "Up next". Removing the playing song moves on to the next one.</summary>
    public void Remove(MediaItem item)
    {
        var index = Queue.IndexOf(item);
        if (index < 0) return;

        if (index != CurrentIndex)
        {
            if (index < CurrentIndex) CurrentIndex--;
            Queue.RemoveAt(index);
            return;
        }

        Queue.RemoveAt(index);
        if (index < Queue.Count)
        {
            PlayAt(index);
            return;
        }

        // It was the last song: stop.
        ++_playToken;
        StopFade();
        _mp.Pause();
        _mp.Source = null;
        _timer.Stop();
        CurrentIndex = -1;
        Current = null;
        IsPlaying = false;
        IsBuffering = false;
        Position = Duration = 0;
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
        StopFade();
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
        StopFade();
        _mp.PlaybackSession.Position = TimeSpan.FromSeconds(Math.Max(0, seconds));
        Position = seconds;
        UpdateTimeline();
    }

    public void ToggleShuffle()
    {
        Shuffle = !Shuffle;
        if (!Shuffle || CurrentIndex < 0) return;
        var tail = Queue.Skip(CurrentIndex + 1).ToList();
        ShuffleRange(tail, 0);
        for (var i = 0; i < tail.Count; i++) Queue[CurrentIndex + 1 + i] = tail[i];
    }

    public void CycleRepeat()
    {
        Repeat = Repeat switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off,
        };
        QueuePrepareNext();
    }

    // ---------- internals ----------

    /// <param name="crossfade">Keep the previous song playing on the spare player while it fades out.</param>
    private async Task PlayCurrentAsync(bool crossfade = false)
    {
        var item = Current;
        if (item?.VideoId is null) return;

        var token = ++_playToken;
        if (!crossfade) StopFade();
        Error = null;
        IsBuffering = true;
        Position = 0;
        Duration = ParseDuration(item.Duration);
        _mp.Source = null;

        // Use the copy prepared while the previous song played, if there is one.
        var prepared = _next is { } n && n.VideoId == item.VideoId && !n.Audio.IsFaulted ? n.Audio : null;
        if (_next?.VideoId == item.VideoId) _next = null;

        try
        {
            var audio = await (prepared ?? LoadAudioAsync(item.VideoId));
            if (token != _playToken) return;
            var stream = new MemoryStream(audio.Data.Array!, audio.Data.Offset, audio.Data.Count, writable: false).AsRandomAccessStream();
            _mp.Source = MediaSource.CreateFromStream(stream, audio.Mime);
            _loudness = audio.LoudnessDb;
            ApplyVolume();
            UpdateDisplay(item);
            _mp.Play();
            _startedAt = DateTime.UtcNow;
            PrepareNext();
            _ = ExtendQueueIfNeededAsync();
            ReleaseOldBuffers();
        }
        catch (Exception ex)
        {
            if (token != _playToken) return;
            _resolver.Invalidate(item.VideoId);
            IsBuffering = false;
            Error = "Playback failed: " + ex.Message;
        }
    }

    /// <summary>Resolves, downloads and remuxes a song so it can start instantly.</summary>
    private async Task<PreparedAudio> LoadAudioAsync(string videoId)
    {
        var info = await _resolver.ResolveAsync(videoId).ConfigureAwait(false);
        // googlevideo rejects Media Foundation's own range requests, so fetch the (~3-5 MB)
        // audio ourselves in &range= chunks and play it from memory.
        var data = await DownloadAsync(info).ConfigureAwait(false);
        if (info.MimeType == "audio/mp4" && AdtsRemuxer.Remux(data) is { } adts) return new PreparedAudio(adts, "audio/aac", info.LoudnessDb);
        return new PreparedAudio(data, info.MimeType, info.LoudnessDb);
    }

    private async Task<byte[]> DownloadAsync(StreamInfo info)
    {
        const int chunk = 1024 * 1024;
        if (info.ContentLength is not { } length)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, info.Url);
            req.Headers.TryAddWithoutValidation("User-Agent", info.UserAgent);
            using var resp = await _http.SendAsync(req).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        }

        // One exact-size buffer instead of a growing MemoryStream plus a copy.
        var data = GC.AllocateUninitializedArray<byte>((int)length);
        for (var start = 0; start < data.Length; start += chunk)
        {
            var count = Math.Min(chunk, data.Length - start);
            using var req = new HttpRequestMessage(HttpMethod.Get, $"{info.Url}&range={start}-{start + count - 1}");
            req.Headers.TryAddWithoutValidation("User-Agent", info.UserAgent);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            await using var body = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            await body.ReadExactlyAsync(data.AsMemory(start, count)).ConfigureAwait(false);
        }
        return data;
    }

    private int UpcomingIndex()
    {
        if (Repeat == RepeatMode.One || Queue.Count == 0) return -1;
        if (CurrentIndex + 1 < Queue.Count) return CurrentIndex + 1;
        return Repeat == RepeatMode.All ? 0 : -1;
    }

    private MediaItem? UpcomingItem() => UpcomingIndex() is >= 0 and var i ? Queue[i] : null;

    // ---------- volume & crossfade ----------

    /// <summary>Combines the volume slider, per-song levelling and any crossfade in progress.</summary>
    public void ApplyVolume()
    {
        // Squared ramps change loudness at a steady rate to the ear: the old song fades out gradually
        // over the whole fade while the new one fades in gradually.
        var fade = FadeProgress();
        _mp.Volume = _volume / 100 * Gain(_loudness) * fade * fade;
        if (_fading) _spare.Volume = _volume / 100 * Gain(_spareLoudness) * (1 - fade) * (1 - fade);
    }

    /// <summary>Like YouTube, only turns songs louder than the reference level down; never boosts.</summary>
    private double Gain(double? loudnessDb) =>
        _settings.NormalizeVolume && loudnessDb > 0 ? Math.Pow(10, -loudnessDb.Value / 20) : 1;

    private double FadeProgress() =>
        _fading ? Math.Clamp((DateTime.UtcNow - _fadeStartedAt).TotalSeconds / _fadeSeconds, 0, 1) : 1;

    /// <summary>
    /// Near the end of a song, starts the next one on the spare player and fades between them.
    /// Only when the next song is already downloaded; otherwise it plays normally.
    /// </summary>
    private void TryStartCrossfade()
    {
        if (_fading || IsSeeking || !_settings.Crossfade) return;
        var session = _mp.PlaybackSession;
        var duration = session.NaturalDuration.TotalSeconds;
        var seconds = Math.Min(_settings.CrossfadeSeconds, duration / 3);
        if (duration <= 0 || duration - session.Position.TotalSeconds > seconds) return;

        var index = UpcomingIndex();
        if (index < 0 || _next is not { } n || n.VideoId != Queue[index].VideoId || !n.Audio.IsCompletedSuccessfully) return;

        (_mp, _spare) = (_spare, _mp);
        _spareLoudness = _loudness;
        _fading = true;
        _fadeSeconds = seconds;
        _fadeStartedAt = DateTime.UtcNow;
        _fadeTimer.Start();
        CurrentIndex = index;
        Current = Queue[index];
        _ = PlayCurrentAsync(crossfade: true);
    }

    /// <summary>Ends a crossfade immediately: the previous song stops and the current one is at full volume.</summary>
    private void StopFade()
    {
        if (!_fading) return;
        _fading = false;
        _fadeTimer.Stop();
        _spare.Pause();
        _spare.Source = null;
        ApplyVolume();
    }

    /// <summary>Queue edits arrive in bursts (e.g. a whole radio); re-check the upcoming song once.</summary>
    private void QueuePrepareNext()
    {
        if (_prepareQueued) return;
        _prepareQueued = true;
        _ui.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _prepareQueued = false;
            PrepareNext();
        });
    }

    /// <summary>Keeps exactly one upcoming song downloaded and ready; drops it if the queue changed.</summary>
    private void PrepareNext()
    {
        if (Current is null) return;
        var id = UpcomingItem()?.VideoId;
        if (id is null || id == Current.VideoId) { _next = null; return; }
        if (_next is { } n && n.VideoId == id && !n.Audio.IsFaulted) return;

        var task = LoadAudioAsync(id);
        _ = task.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
        _next = (id, task);
    }

    /// <summary>
    /// Track buffers are a few MB each and land on the large object heap, which .NET
    /// doesn't compact by default. Compact once per track so memory doesn't creep up.
    /// </summary>
    private void ReleaseOldBuffers() =>
        _ui.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect();
        });

    // ---------- Windows media controls ----------

    private void UpdateDisplay(MediaItem item)
    {
        var display = _smtc.DisplayUpdater;
        display.Type = MediaPlaybackType.Music;
        display.MusicProperties.Title = item.Title;
        display.MusicProperties.Artist = item.ArtistName ?? item.Subtitle ?? "";
        display.MusicProperties.AlbumTitle = item.AlbumName ?? "";
        display.Thumbnail = item.Thumbnail is null ? null
            : RandomAccessStreamReference.CreateFromUri(new Uri(Thumbs.Resize(item.Thumbnail, 300)));
        display.Update();
    }

    private void UpdateTimeline() => _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
    {
        StartTime = TimeSpan.Zero,
        MinSeekTime = TimeSpan.Zero,
        EndTime = TimeSpan.FromSeconds(Duration),
        MaxSeekTime = TimeSpan.FromSeconds(Duration),
        Position = TimeSpan.FromSeconds(Position),
    });

    private void OnFailed(string message)
    {
        IsBuffering = false;
        Error = "Playback failed: " + message;
    }

    private void OnEnded()
    {
        // YouTube sometimes serves a valid-looking file with no playable audio; the player then
        // "ends" instantly. Report it instead of skipping through the whole queue.
        if (DateTime.UtcNow - _startedAt < TimeSpan.FromSeconds(5))
        {
            if (Current?.VideoId is { } id) _resolver.Invalidate(id);
            IsPlaying = false;
            _timer.Stop();
            Error = "YouTube refused to stream this song. Try again later, or sign in.";
            return;
        }
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
        _smtc.PlaybackStatus = state switch
        {
            MediaPlaybackState.Playing => MediaPlaybackStatus.Playing,
            MediaPlaybackState.Paused => MediaPlaybackStatus.Paused,
            MediaPlaybackState.Opening or MediaPlaybackState.Buffering => MediaPlaybackStatus.Changing,
            _ => MediaPlaybackStatus.Stopped,
        };
        UpdateTimeline();
    }

    private void Tick()
    {
        var session = _mp.PlaybackSession;
        if (session.NaturalDuration > TimeSpan.Zero) Duration = session.NaturalDuration.TotalSeconds;
        if (!IsSeeking) Position = session.Position.TotalSeconds;
        TryStartCrossfade();
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
        _fadeTimer.Stop();
        _mp.Dispose();
        _spare.Dispose();
    }
}
