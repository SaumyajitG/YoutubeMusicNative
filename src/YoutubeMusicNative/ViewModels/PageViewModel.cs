using System.Collections.ObjectModel;
using System.Net.Http;
using System.Windows.Input;
using YoutubeMusicNative.Infrastructure;
using YoutubeMusicNative.Models;
using YoutubeMusicNative.Services;

namespace YoutubeMusicNative.ViewModels;

public sealed class PageViewModel : ObservableObject
{
    private readonly InnerTubeClient _api;
    private readonly Func<CancellationToken, Task<BrowseResult>> _loader;
    private readonly List<ShelfViewModel> _shelves = [];
    private Continuation? _continuation;
    private bool _loaded, _loadingMore;

    private PageViewModel(InnerTubeClient api, string nav, string? title, bool isCollection,
        Func<CancellationToken, Task<BrowseResult>> loader)
    {
        _api = api;
        Nav = nav;
        Title = title;
        IsCollection = isCollection;
        _loader = loader;
        PlayAllCommand = new RelayCommand(() => PlayAll(false));
        ShuffleCommand = new RelayCommand(() => PlayAll(true));
    }

    // ---------- factories ----------

    public static PageViewModel Home(InnerTubeClient api) =>
        new(api, "home", null, false, ct => api.GetHomeAsync(ct));

    public static PageViewModel Explore(InnerTubeClient api) =>
        new(api, "explore", "Explore", false, ct => api.BrowseAsync("FEmusic_explore", null, ct));

    public static PageViewModel Library(InnerTubeClient api) =>
        new(api, "library", "Library", false, ct => api.BrowseAsync("FEmusic_liked_playlists", null, ct));

    public static PageViewModel Search(InnerTubeClient api, string query) =>
        new(api, "search", $"Results for “{query}”", false, ct => api.SearchAsync(query, ct));

    public static PageViewModel Browse(InnerTubeClient api, MediaItem item, string nav) =>
        new(api, nav, item.Title, item.Kind is ItemKind.Album or ItemKind.Playlist,
            ct => api.BrowseAsync(item.BrowseId!, item.BrowseParams, ct));

    public static PageViewModel Message(InnerTubeClient api, string nav, string title, MessageRow message)
    {
        var page = new PageViewModel(api, nav, title, false, _ => Task.FromResult(new BrowseResult())) { _loaded = true };
        page.Rows.Add(new TitleRow(title, null));
        page.Rows.Add(message);
        return page;
    }

    // ---------- state ----------

    public string Nav { get; }
    public string? Title { get; }
    public bool IsCollection { get; }
    public DateTime LoadedAt { get; private set; }
    public ObservableCollection<object> Rows { get; } = [];
    public double ScrollOffset { get; set; }
    public ICommand PlayAllCommand { get; }
    public ICommand ShuffleCommand { get; }

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => Set(ref _isLoading, value); }

    // ---------- loading ----------

    public async Task LoadAsync()
    {
        if (_loaded || IsLoading) return;
        IsLoading = true;
        try
        {
            Build(await _loader(CancellationToken.None));
            _loaded = true;
            LoadedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Rows.Clear();
            Rows.Add(new MessageRow(FriendlyError(ex), "Retry", new RelayCommand(() => _ = LoadAsync())));
        }
        finally
        {
            IsLoading = false;
        }
    }

    public async Task LoadMoreAsync()
    {
        if (_continuation is not { } cont || _loadingMore) return;
        _loadingMore = true;
        _continuation = null;
        Rows.Add(LoadingRow.Instance);
        try
        {
            var more = await _api.ContinueAsync(cont);
            Rows.Remove(LoadingRow.Instance);
            foreach (var shelf in more.Shelves) AddShelf(shelf);
            if (more.Items.Count > 0 && _shelves.LastOrDefault(s => s.Style == ShelfStyle.List) is { } list)
            {
                foreach (var item in more.Items)
                {
                    list.Items.Add(item);
                    Rows.Add(new TrackRow(item, list));
                }
            }
            _continuation = more.Next;
        }
        catch
        {
            // Stop paging on error; the content already shown stays usable.
            Rows.Remove(LoadingRow.Instance);
        }
        finally
        {
            _loadingMore = false;
        }
    }

    private void Build(BrowseResult result)
    {
        Rows.Clear();
        _shelves.Clear();

        if (result.Header is { } h && !string.IsNullOrEmpty(h.Title))
        {
            if (h.IsArtist) Rows.Add(new ArtistHeaderRow(h, this));
            else if (h.Thumbnail is not null) Rows.Add(new DetailHeaderRow(h, this));
            else Rows.Add(new TitleRow(h.Title, h.Subtitle));
        }
        else if (Title is not null)
        {
            Rows.Add(new TitleRow(Title, null));
        }

        foreach (var shelf in result.Shelves) AddShelf(shelf);
        if (result.Shelves.Count == 0) Rows.Add(new MessageRow("Nothing to show here yet."));
        _continuation = result.Continuation;
    }

    private void AddShelf(Shelf shelf)
    {
        var vm = new ShelfViewModel(shelf, IsCollection && shelf.Style == ShelfStyle.List);
        _shelves.Add(vm);
        switch (shelf.Style)
        {
            case ShelfStyle.Carousel or ShelfStyle.QuickPicks:
                Rows.Add(new CarouselRow(vm));
                break;
            case ShelfStyle.Grid:
                Rows.Add(new GridRow(vm));
                break;
            default:
                if (!string.IsNullOrEmpty(vm.Title)) Rows.Add(new ShelfHeaderRow(vm));
                foreach (var row in vm.TrackRows) Rows.Add(row);
                break;
        }
    }

    private void PlayAll(bool shuffle)
    {
        var list = _shelves.FirstOrDefault(s => s.Style == ShelfStyle.List && s.Items.Any(i => i.VideoId is not null));
        if (list is not null) App.Vm.Player.PlayList(list.Items, 0, shuffle);
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        HttpRequestException => "Can't reach YouTube Music. Check your connection.",
        InnerTubeException { Status: 401 or 403 } => "YouTube Music rejected the request. Try signing in again.",
        _ => ex.Message,
    };
}
