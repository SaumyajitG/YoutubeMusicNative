using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using YoutubeMusicNative.Models;
using YoutubeMusicNative.ViewModels;

namespace YoutubeMusicNative.Views;

public partial class CarouselShelfView : UserControl
{
    private double _scrollTarget;
    private bool _animating;

    public CarouselShelfView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind();
    }

    private void Bind()
    {
        if (DataContext is not CarouselRow { Shelf: var shelf }) return;

        if (shelf.Style == ShelfStyle.QuickPicks)
        {
            Items.ItemsPanel = (ItemsPanelTemplate)Resources["QuickPicksPanel"];
            Items.ItemContainerStyle = (Style)Resources["QuickPickContainer"];
            Items.ItemTemplateSelector = null;
            Items.ItemsSource = shelf.TrackRows;
        }
        else
        {
            Items.ItemsPanel = (ItemsPanelTemplate)Resources["RowPanel"];
            Items.ItemContainerStyle = null;
            Items.ItemTemplateSelector = (DataTemplateSelector)FindResource("CardSelector");
            Items.ItemsSource = shelf.Items;
        }
        Scroller.ScrollToHorizontalOffset(0);
    }

    private void OnLeft(object sender, RoutedEventArgs e) => ScrollBy(-Scroller.ViewportWidth * 0.85);
    private void OnRight(object sender, RoutedEventArgs e) => ScrollBy(Scroller.ViewportWidth * 0.85);

    private void ScrollBy(double delta)
    {
        var start = _animating ? _scrollTarget : Scroller.HorizontalOffset;
        _scrollTarget = Math.Clamp(start + delta, 0, Scroller.ScrollableWidth);
        if (_animating) return;
        _animating = true;
        CompositionTarget.Rendering += AnimateStep;
    }

    // Smooth ease-out scroll without allocating storyboards (HorizontalOffset is not animatable).
    private void AnimateStep(object? sender, EventArgs e)
    {
        var current = Scroller.HorizontalOffset;
        var diff = _scrollTarget - current;
        if (Math.Abs(diff) < 1)
        {
            Scroller.ScrollToHorizontalOffset(_scrollTarget);
            CompositionTarget.Rendering -= AnimateStep;
            _animating = false;
            return;
        }
        Scroller.ScrollToHorizontalOffset(current + diff * 0.2);
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        LeftButton.IsEnabled = Scroller.HorizontalOffset > 1;
        RightButton.IsEnabled = Scroller.HorizontalOffset < Scroller.ScrollableWidth - 1;
    }

    /// <summary>Vertical wheel scrolls the page; Shift+wheel scrolls the carousel.</summary>
    private void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (Keyboard.Modifiers == ModifierKeys.Shift)
        {
            Scroller.ScrollToHorizontalOffset(Scroller.HorizontalOffset - e.Delta);
            return;
        }
        if (VisualTreeHelper.GetParent(this) is UIElement parent)
        {
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = MouseWheelEvent,
                Source = this,
            });
        }
    }
}
