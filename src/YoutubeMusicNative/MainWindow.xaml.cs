using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using YoutubeMusicNative.Infrastructure;
using YoutubeMusicNative.ViewModels;

namespace YoutubeMusicNative;

public partial class MainWindow : Window
{
    private const double CompactSidebarBelow = 1000;
    private const double NarrowBelow = 720;
    private bool? _wasCompact;

    public MainWindow()
    {
        InitializeComponent();
        WindowTheming.ApplyDark(this);
        SizeChanged += (_, _) => UpdateLayoutState();
        Loaded += (_, _) =>
        {
            RestorePlacement();
            Vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainViewModel.IsSidebarExpanded)) UpdateLayoutState();
            };
        };
        Closing += (_, _) => SavePlacement();
        PreviewKeyDown += OnWindowKeyDown;
        PreviewMouseDown += OnWindowMouseDown;
    }

    private MainViewModel Vm => (MainViewModel)DataContext;

    // ---------- responsive layout ----------

    private void UpdateLayoutState()
    {
        var width = ActualWidth;
        var compact = width < CompactSidebarBelow;
        // Only auto-collapse/expand when crossing the threshold so the hamburger toggle still wins.
        if (_wasCompact != compact)
        {
            Vm.IsSidebarExpanded = !compact;
            _wasCompact = compact;
        }
        Vm.IsNarrow = width < NarrowBelow;

        var sidebar = Vm.IsSidebarExpanded ? 257 : 73;
        var available = width - sidebar;
        var side = Vm.IsNarrow ? 16.0 : 32.0;
        const double maxContent = 1480;
        if (available - side * 2 > maxContent) side = (available - maxContent) / 2;
        Vm.ContentMargin = new Thickness(side, 0, side, 0);
    }

    private void RestorePlacement()
    {
        var s = Vm.Settings;
        if (s.WindowWidth >= MinWidth && s.WindowWidth <= SystemParameters.VirtualScreenWidth) Width = s.WindowWidth;
        if (s.WindowHeight >= MinHeight && s.WindowHeight <= SystemParameters.VirtualScreenHeight) Height = s.WindowHeight;
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
        UpdateLayoutState();
    }

    private void SavePlacement()
    {
        var s = Vm.Settings;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        s.WindowWidth = RestoreBounds.Width is > 0 and var w and not double.PositiveInfinity ? w : Width;
        s.WindowHeight = RestoreBounds.Height is > 0 and var h and not double.PositiveInfinity ? h : Height;
    }

    // ---------- keyboard & mouse ----------

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        var typing = Keyboard.FocusedElement is TextBox;
        if (e.Key == Key.Space && !typing)
        {
            Vm.Player.TogglePlayPause();
            e.Handled = true;
        }
        else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt || e.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt)
        {
            Vm.GoBack();
            e.Handled = true;
        }
        else if ((e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control) || (e.Key == Key.OemQuestion && !typing))
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1)
        {
            Vm.GoBack();
            e.Handled = true;
        }
    }

    // ---------- search ----------

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                Vm.Search(SearchBox.Text);
                Keyboard.ClearFocus();
                FocusManager.SetFocusedElement(this, this);
                e.Handled = true;
                break;
            case Key.Escape:
                Vm.IsSuggestionsOpen = false;
                e.Handled = true;
                break;
            case Key.Down when Vm.IsSuggestionsOpen && SuggestList.Items.Count > 0:
                SuggestList.SelectedIndex = 0;
                (SuggestList.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
                e.Handled = true;
                break;
        }
    }

    private void OnSuggestKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SuggestList.SelectedItem is string s)
        {
            Vm.Search(s);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Vm.IsSuggestionsOpen = false;
            SearchBox.Focus();
            e.Handled = true;
        }
    }

    private void OnSuggestionClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: string s }) Vm.Search(s);
    }

    private void OnAvatarClick(object sender, RoutedEventArgs e)
    {
        if (AvatarButton.ContextMenu is { } menu)
        {
            menu.PlacementTarget = AvatarButton;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.DataContext = DataContext;
            menu.IsOpen = true;
        }
    }
}
