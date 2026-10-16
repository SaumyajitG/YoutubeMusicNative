using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using YoutubeMusicNative.Infrastructure;
using YoutubeMusicNative.Models;
using YoutubeMusicNative.Services;

namespace YoutubeMusicNative.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private const int MaxHistory = 30;
    private const int MaxCachedPages = 20;
    private readonly List<PageViewModel> _history = [];
    /// <summary>Opened pages stay loaded until the app closes or the refresh button; least recently opened go first.</summary>
    private readonly OrderedDictionary<string, PageViewModel> _pages = [];
    private readonly StreamResolver _resolver;
    private CancellationTokenSource? _suggestCts;
    private readonly DispatcherTimer _toastTimer;

    public MainViewModel(Settings settings)
    {
        Settings = settings;
        Auth = new AuthService(settings);
        Api = new InnerTubeClient(App.Http, Auth);
        _resolver = new StreamResolver(App.Http, Api, Auth, settings);
        Player = new PlayerService(Api, _resolver, App.Http, settings);
        OpenSettingsCommand = new RelayCommand(OpenSettings);
        RefreshCommand = new RelayCommand(_ => _ = CurrentPage?.RefreshAsync(), _ => CurrentPage?.CanRefresh == true);

        HomeCommand = new RelayCommand(() => NavigateRoot("home"));
        ExploreCommand = new RelayCommand(() => NavigateRoot("explore"));
        LibraryCommand = new RelayCommand(() => NavigateRoot("library"));
        BackCommand = new RelayCommand(_ => GoBack(), _ => CanGoBack);
        SearchCommand = new RelayCommand(p => Search(p as string ?? SearchText));
        OpenCommand = new RelayCommand(p => { if (p is MediaItem i) Open(i); });
        PlayCommand = new RelayCommand(p => { if (p is MediaItem i) Play(i); });
        PlayNextCommand = new RelayCommand(p => { if (p is MediaItem i) Player.PlayNext(i); }, p => p is MediaItem { VideoId: not null });
        AddToQueueCommand = new RelayCommand(p => { if (p is MediaItem i) Player.AddToQueue(i); }, p => p is MediaItem { VideoId: not null });
        GoToArtistCommand = new RelayCommand(p => GoTo(p as MediaItem, artist: true), p => p is MediaItem { ArtistBrowseId: not null });
        GoToAlbumCommand = new RelayCommand(p => GoTo(p as MediaItem, artist: false), p => p is MediaItem { AlbumBrowseId: not null });
        SignInCommand = new RelayCommand(SignIn);
        SignOutCommand = new RelayCommand(SignOut);
        ToggleQueueCommand = new RelayCommand(() => IsQueueOpen = !IsQueueOpen);
        ToggleSidebarCommand = new RelayCommand(() => IsSidebarExpanded = !IsSidebarExpanded);
        TogglePlayCommand = new RelayCommand(Player.TogglePlayPause);
        NextCommand = new RelayCommand(Player.Next);
        PreviousCommand = new RelayCommand(Player.Previous);
        ShuffleCommand = new RelayCommand(Player.ToggleShuffle);
        RepeatCommand = new RelayCommand(Player.CycleRepeat);
        MuteCommand = new RelayCommand(() => Player.IsMuted = !Player.IsMuted);
        PlayQueueItemCommand = new RelayCommand(p => { if (p is MediaItem i) Player.PlayQueueItem(i); });
        RemoveFromQueueCommand = new RelayCommand(p => { if (p is MediaItem i) Player.Remove(i); });

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); Toast = null; };
        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PlayerService.Error) && Player.Error is { } err) ShowToast(err);
        };
    }

    public Settings Settings { get; }
    public AuthService Auth { get; }
    public InnerTubeClient Api { get; }
    public PlayerService Player { get; }

    // ---------- commands ----------

    public ICommand HomeCommand { get; }
    public ICommand ExploreCommand { get; }
    public ICommand LibraryCommand { get; }
    public ICommand BackCommand { get; }
    public ICommand SearchCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand PlayCommand { get; }
    public ICommand PlayNextCommand { get; }
    public ICommand AddToQueueCommand { get; }
    public ICommand GoToArtistCommand { get; }
    public ICommand GoToAlbumCommand { get; }
    public ICommand SignInCommand { get; }
    public ICommand SignOutCommand { get; }
    public ICommand ToggleQueueCommand { get; }
    public ICommand ToggleSidebarCommand { get; }
    public ICommand TogglePlayCommand { get; }
    public ICommand NextCommand { get; }
    public ICommand PreviousCommand { get; }
    public ICommand ShuffleCommand { get; }
    public ICommand RepeatCommand { get; }
    public ICommand MuteCommand { get; }
    public ICommand PlayQueueItemCommand { get; }
    public ICommand RemoveFromQueueCommand { get; }
    public ICommand OpenSettingsCommand { get; }
    public ICommand RefreshCommand { get; }

    // ---------- settings ----------

    public bool IsDarkMode
    {
        get => Settings.DarkMode;
        set
        {
            if (Settings.DarkMode == value) return;
            Settings.DarkMode = value;
            Settings.Save();
            WindowTheming.SetTheme(value);
            OnPropertyChanged();
        }
    }

    public bool UseAccountForPlayback
    {
        get => Settings.UseAccountForPlayback;
        set
        {
            if (Settings.UseAccountForPlayback == value) return;
            Settings.UseAccountForPlayback = value;
            Settings.Save();
            _resolver.Clear(); // stream links depend on how they were fetched
            OnPropertyChanged();
        }
    }

    public bool NormalizeVolume
    {
        get => Settings.NormalizeVolume;
        set
        {
            if (Settings.NormalizeVolume == value) return;
            Settings.NormalizeVolume = value;
            Settings.Save();
            Player.ApplyVolume();
            OnPropertyChanged();
        }
    }

    public bool Crossfade
    {
        get => Settings.Crossfade;
        set
        {
            if (Settings.Crossfade == value) return;
            Settings.Crossfade = value;
            Settings.Save();
            OnPropertyChanged();
        }
    }

    public double CrossfadeSeconds
    {
        get => Settings.CrossfadeSeconds;
        set
        {
            value = Math.Clamp(Math.Round(value), 1, 12);
            if (Settings.CrossfadeSeconds == value) return;
            Settings.CrossfadeSeconds = value;
            Settings.Save();
            OnPropertyChanged();
        }
    }

    private void OpenSettings() =>
        new Views.SettingsWindow { Owner = Application.Current.MainWindow, DataContext = this }.ShowDialog();

    // ---------- navigation ----------

    private PageViewModel? _currentPage;
    public PageViewModel? CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (!Set(ref _currentPage, value)) return;
            if (value is not null) ActiveNav = value.Nav;
            OnPropertyChanged(nameof(CanGoBack));
        }
    }

    public bool CanGoBack => _history.Count > 0;

    private string _activeNav = "home";
    public string ActiveNav { get => _activeNav; private set => Set(ref _activeNav, value); }

    public void Start()
    {
        NavigateRoot("home");
        if (Auth.IsSignedIn) _ = LoadAccountAsync();
    }

    private void Navigate(PageViewModel page)
    {
        if (page == CurrentPage) return;
        if (CurrentPage is not null)
        {
            _history.Add(CurrentPage);
            if (_history.Count > MaxHistory) _history.RemoveAt(0);
        }
        CurrentPage = page;
        _ = page.LoadAsync();
    }

    private void NavigateRoot(string nav)
    {
        var page = nav switch
        {
            "explore" => Cached("explore", () => PageViewModel.Explore(Api)),
            "library" when !Auth.IsSignedIn => PageViewModel.Message(Api, "library", "Library",
                new MessageRow("Sign in to see your playlists and liked music.", "Sign in", SignInCommand)),
            "library" => Cached("library", () => PageViewModel.Library(Api)),
            _ => Cached("home", () => PageViewModel.Home(Api)),
        };
        Navigate(page);
    }

    private PageViewModel Cached(string key, Func<PageViewModel> create)
    {
        if (_pages.Remove(key, out var page))
        {
            _pages.Add(key, page); // now the most recently opened
            return page;
        }
        page = create();
        _pages.Add(key, page);
        if (_pages.Count > MaxCachedPages) _pages.RemoveAt(0);
        return page;
    }

    public void GoBack()
    {
        if (_history.Count == 0) return;
        var page = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        CurrentPage = page;
    }

    public void Open(MediaItem item)
    {
        if (item.BrowseId is not null && item.VideoId is null)
        {
            var nav = item.Kind == ItemKind.Category ? "explore" : ActiveNav;
            Navigate(Cached($"browse:{item.BrowseId}|{item.BrowseParams}", () => PageViewModel.Browse(Api, item, nav)));
        }
        else
        {
            Play(item);
        }
    }

    public void Play(MediaItem item)
    {
        if (item.VideoId is not null) _ = Player.PlayRadioAsync(item);
        else if (item.PlaylistId is not null) _ = Player.PlayPlaylistAsync(item.PlaylistId);
        else if (item.BrowseId is not null) Open(item);
    }

    private void GoTo(MediaItem? item, bool artist)
    {
        if (item is null) return;
        var id = artist ? item.ArtistBrowseId : item.AlbumBrowseId;
        if (id is null) return;
        Navigate(Cached($"browse:{id}|", () => PageViewModel.Browse(Api, new MediaItem
        {
            Title = (artist ? item.ArtistName : item.AlbumName) ?? "",
            BrowseId = id,
            Kind = artist ? ItemKind.Artist : ItemKind.Album,
        }, ActiveNav)));
    }

    // ---------- search ----------

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (Set(ref _searchText, value)) _ = UpdateSuggestionsAsync(value);
        }
    }

    public ObservableCollection<string> Suggestions { get; } = [];

    private bool _isSuggestionsOpen;
    public bool IsSuggestionsOpen { get => _isSuggestionsOpen; set => Set(ref _isSuggestionsOpen, value); }

    public void Search(string? query)
    {
        query = query?.Trim();
        if (string.IsNullOrEmpty(query)) return;
        _suggestCts?.Cancel();
        IsSuggestionsOpen = false;
        if (_searchText != query) { _searchText = query; OnPropertyChanged(nameof(SearchText)); }
        Navigate(Cached("search:" + query, () => PageViewModel.Search(Api, query)));
    }

    private async Task UpdateSuggestionsAsync(string text)
    {
        _suggestCts?.Cancel();
        var cts = _suggestCts = new CancellationTokenSource();
        if (string.IsNullOrWhiteSpace(text)) { IsSuggestionsOpen = false; return; }
        try
        {
            await Task.Delay(180, cts.Token);
            var list = await Api.GetSuggestionsAsync(text, cts.Token);
            if (cts.IsCancellationRequested) return;
            Suggestions.Clear();
            foreach (var s in list.Take(8)) Suggestions.Add(s);
            IsSuggestionsOpen = Suggestions.Count > 0;
        }
        catch
        {
            // Suggestions are optional.
        }
    }

    // ---------- account ----------

    private AccountInfo? _account;
    public AccountInfo? Account { get => _account; private set => Set(ref _account, value); }

    private async Task LoadAccountAsync()
    {
        try { Account = await Api.GetAccountAsync(); }
        catch (Exception ex) { ShowToast("Couldn't load your account: " + ex.Message); }
    }

    private void SignIn()
    {
        var dialog = new Views.LoginWindow(Auth) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;
        _resolver.Clear();
        _pages.Clear(); // pages differ per account
        _history.Clear();
        CurrentPage = null;
        NavigateRoot("home");
        _ = LoadAccountAsync();
        ShowToast("Signed in");
    }

    private void SignOut()
    {
        Auth.SignOut();
        _resolver.Clear();
        Account = null;
        _pages.Clear();
        _history.Clear();
        CurrentPage = null;
        NavigateRoot("home");
        ShowToast("Signed out");
    }

    // ---------- layout state (driven by MainWindow size) ----------

    private bool _isSidebarExpanded = true;
    public bool IsSidebarExpanded { get => _isSidebarExpanded; set => Set(ref _isSidebarExpanded, value); }

    private bool _isNarrow;
    /// <summary>Phone-like widths: hide secondary controls.</summary>
    public bool IsNarrow { get => _isNarrow; set => Set(ref _isNarrow, value); }

    private bool _isQueueOpen;
    public bool IsQueueOpen { get => _isQueueOpen; set => Set(ref _isQueueOpen, value); }

    private Thickness _contentMargin = new(32, 0, 32, 0);
    public Thickness ContentMargin { get => _contentMargin; set => Set(ref _contentMargin, value); }

    // ---------- toast ----------

    private string? _toast;
    public string? Toast { get => _toast; private set => Set(ref _toast, value); }

    public void ShowToast(string message)
    {
        Toast = message;
        _toastTimer.Stop();
        _toastTimer.Start();
    }
}
