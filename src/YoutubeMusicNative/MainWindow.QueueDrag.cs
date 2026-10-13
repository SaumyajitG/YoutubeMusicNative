using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using YoutubeMusicNative.Models;

namespace YoutubeMusicNative;

/// <summary>
/// Drag-to-reorder for the "Up next" queue, started from each row's ≡ grip. A semi-transparent copy
/// of the row follows the pointer vertically while the other rows slide apart to open a gap where
/// the song will land.
/// </summary>
public partial class MainWindow
{
    private const double AutoScrollZone = 40;
    private const double AutoScrollStep = 12;
    private static readonly Duration ShiftDuration = TimeSpan.FromMilliseconds(120);

    private ScrollViewer? _queueScroller;
    private QueueDrag? _drag;

    private sealed class QueueDrag(MediaItem item, double rowHeight, double grabOffset, DragPreview preview)
    {
        public MediaItem Item { get; } = item;
        public double RowHeight { get; } = rowHeight;
        public double GrabOffset { get; } = grabOffset;
        public DragPreview Preview { get; } = preview;
        /// <summary>Where the song would land; equals its own index when the drop wouldn't move it.</summary>
        public int TargetIndex { get; set; } = -1;
        /// <summary>Current slide offset of every row container touched during this drag.</summary>
        public Dictionary<ListBoxItem, double> Shifts { get; } = [];
    }

    private void OnQueueGripMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: MediaItem item } grip) return;
        if (ItemsControl.ContainerFromElement(QueueList, grip) is not ListBoxItem row) return;
        if (AdornerLayer.GetAdornerLayer(QueueList) is not { } layer) return;
        e.Handled = true; // the grip only drags; it never plays the row

        var origin = row.TranslatePoint(new Point(0, 0), QueueList);
        var preview = new DragPreview(QueueList, Snapshot(row), origin.X, row.ActualWidth, row.ActualHeight) { Y = origin.Y };
        layer.Add(preview);
        _drag = new QueueDrag(item, row.ActualHeight, e.GetPosition(row).Y, preview)
        {
            TargetIndex = Vm.Player.Queue.IndexOf(item),
        };
        UpdateQueueRows();

        try
        {
            DragDrop.DoDragDrop(grip, new DataObject(typeof(MediaItem), item), DragDropEffects.Move);
        }
        finally
        {
            EndQueueDrag();
        }
    }

    private void OnQueueDragOver(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_drag is null)
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        var y = e.GetPosition(QueueList).Y;
        AutoScrollQueue(y);
        _drag.Preview.Y = Math.Clamp(y - _drag.GrabOffset, 0, Math.Max(0, QueueList.ActualHeight - _drag.RowHeight));

        // Over a row: aim there (or back to the start if the rules forbid it). Between rows: keep the last aim.
        if (RowIndexAt(y) is { } insertAt)
        {
            var from = Vm.Player.Queue.IndexOf(_drag.Item);
            var to = insertAt > from ? insertAt - 1 : insertAt;
            _drag.TargetIndex = to == from || Vm.Player.CanMove(_drag.Item, to) ? to : from;
        }
        e.Effects = DragDropEffects.Move;
        UpdateQueueRows();
    }

    private void OnQueueDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (_drag is not null) Vm.Player.Move(_drag.Item, _drag.TargetIndex);
    }

    private void OnQueueDragLeave(object sender, DragEventArgs e)
    {
        // Fires when crossing child elements too; the drag is cleaned up when DoDragDrop returns.
    }

    /// <summary>Insertion index (0..Count) for a pointer at <paramref name="y"/>, using untransformed row positions.</summary>
    private int? RowIndexAt(double y)
    {
        var generator = QueueList.ItemContainerGenerator;
        for (var i = 0; i < Vm.Player.Queue.Count; i++)
        {
            if (generator.ContainerFromIndex(i) is not ListBoxItem row) continue;
            var shift = row.RenderTransform is TranslateTransform t ? t.Y : 0;
            var top = row.TranslatePoint(new Point(0, 0), QueueList).Y - shift;
            if (y >= top && y < top + row.ActualHeight)
                return y > top + row.ActualHeight / 2 ? i + 1 : i;
        }
        return null;
    }

    /// <summary>Hides the dragged song's row and slides the rows between it and the target to open a gap.</summary>
    private void UpdateQueueRows()
    {
        if (_drag is null) return;
        var from = Vm.Player.Queue.IndexOf(_drag.Item);
        var to = _drag.TargetIndex;
        var generator = QueueList.ItemContainerGenerator;
        for (var i = 0; i < Vm.Player.Queue.Count; i++)
        {
            if (generator.ContainerFromIndex(i) is not ListBoxItem row) continue;
            row.Opacity = i == from ? 0 : 1;
            var shift = to > from && i > from && i <= to ? -_drag.RowHeight
                      : to < from && i >= to && i < from ? _drag.RowHeight
                      : 0;
            SlideRow(row, shift);
        }
    }

    private void SlideRow(ListBoxItem row, double shift)
    {
        if (_drag!.Shifts.TryGetValue(row, out var current) && current == shift) return;
        _drag.Shifts[row] = shift;
        if (row.RenderTransform is not TranslateTransform { IsFrozen: false } transform)
            row.RenderTransform = transform = new TranslateTransform();
        transform.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(shift, ShiftDuration) { EasingFunction = new QuadraticEase() });
    }

    private void EndQueueDrag()
    {
        if (_drag is null) return;
        AdornerLayer.GetAdornerLayer(QueueList)?.Remove(_drag.Preview);
        foreach (var row in _drag.Shifts.Keys)
        {
            row.Opacity = 1;
            if (row.RenderTransform is TranslateTransform transform)
            {
                transform.BeginAnimation(TranslateTransform.YProperty, null);
                transform.Y = 0;
            }
        }
        _drag = null;
    }

    /// <summary>Holding a dragged song near the top/bottom edge scrolls the queue (DragOver keeps firing while held).</summary>
    private void AutoScrollQueue(double y)
    {
        _queueScroller ??= FindDescendant<ScrollViewer>(QueueList);
        if (_queueScroller is null) return;
        if (y < AutoScrollZone) _queueScroller.ScrollToVerticalOffset(_queueScroller.VerticalOffset - AutoScrollStep);
        else if (y > QueueList.ActualHeight - AutoScrollZone) _queueScroller.ScrollToVerticalOffset(_queueScroller.VerticalOffset + AutoScrollStep);
    }

    private static BitmapSource Snapshot(FrameworkElement row)
    {
        var dpi = VisualTreeHelper.GetDpi(row);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(row.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(row.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(row);
        bitmap.Freeze();
        return bitmap;
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    /// <summary>Semi-transparent copy of the dragged row, drawn above the queue list.</summary>
    private sealed class DragPreview : Adorner
    {
        private readonly ImageSource _image;
        private readonly double _x, _width, _height;
        private double _y;

        public DragPreview(UIElement list, ImageSource image, double x, double width, double height) : base(list)
        {
            IsHitTestVisible = false;
            _image = image;
            _x = x;
            _width = width;
            _height = height;
        }

        public double Y
        {
            set { _y = value; InvalidateVisual(); }
        }

        protected override void OnRender(DrawingContext dc)
        {
            var rect = new Rect(_x, _y, _width, _height);
            dc.PushOpacity(0.7);
            dc.DrawRoundedRectangle((Brush)Application.Current.FindResource("PopupBgBrush"), null, rect, 4, 4);
            dc.DrawImage(_image, rect);
            dc.Pop();
        }
    }
}
