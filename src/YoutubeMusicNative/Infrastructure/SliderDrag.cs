using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace YoutubeMusicNative.Infrastructure;

/// <summary>
/// Pressing a slider's bar jumps the value there (IsMoveToPointEnabled) but WPF doesn't start a drag;
/// hand the press to the thumb so the slider can be dragged from wherever it's pressed.
/// </summary>
public static class SliderDrag
{
    public static void Register() =>
        EventManager.RegisterClassHandler(typeof(Slider), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnPress), handledEventsToo: true);

    private static void OnPress(object sender, MouseButtonEventArgs e)
    {
        var slider = (Slider)sender;
        if (slider.Template.FindName("PART_Track", slider) is not Track { Thumb: { IsMouseOver: false, IsDragging: false } thumb } track) return;
        // Move the thumb under the pointer first (this runs before Slider's own move-to-point).
        slider.SetCurrentValue(RangeBase.ValueProperty, track.ValueFromPoint(e.GetPosition(track))); // keeps bindings
        slider.UpdateLayout();
        thumb.RaiseEvent(new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            Source = thumb,
        });
    }
}
