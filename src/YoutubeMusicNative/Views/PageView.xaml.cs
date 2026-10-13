using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using YoutubeMusicNative.ViewModels;

namespace YoutubeMusicNative.Views;

public partial class PageView : UserControl
{
    private ScrollViewer? _scroller;
    private bool _restoring;

    public PageView()
    {
        InitializeComponent();
        DataContextChanged += OnPageChanged;
    }

    private ScrollViewer? Scroller => _scroller ??= List.Template.FindName("PART_Scroller", List) as ScrollViewer;

    // The same view instance is reused for every page; remember each page's scroll position.
    private void OnPageChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is PageViewModel old && Scroller is { } sv) old.ScrollOffset = sv.VerticalOffset;
        if (e.NewValue is not PageViewModel page) return;

        _restoring = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            Scroller?.ScrollToVerticalOffset(page.ScrollOffset);
            _restoring = false;
        });
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_restoring || DataContext is not PageViewModel page) return;
        var sv = (ScrollViewer)sender;
        if (sv.ScrollableHeight - sv.VerticalOffset < sv.ViewportHeight * 1.5)
            _ = page.LoadMoreAsync();
    }
}
