using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using YoutubeMusicNative.Services;
using YoutubeMusicNative.ViewModels;

namespace YoutubeMusicNative.Views;

/// <summary>Previous / Play-Pause / Next buttons in the taskbar thumbnail preview.</summary>
internal static class TaskbarButtons
{
    public static void Attach(Window window, MainViewModel vm)
    {
        var play = Glyph("");
        var pause = Glyph("");

        var previous = new ThumbButtonInfo { Description = "Previous", ImageSource = Glyph(""), Command = vm.PreviousCommand };
        var toggle = new ThumbButtonInfo { Description = "Play", ImageSource = play, Command = vm.TogglePlayCommand };
        var next = new ThumbButtonInfo { Description = "Next", ImageSource = Glyph(""), Command = vm.NextCommand };

        window.TaskbarItemInfo = new TaskbarItemInfo { ThumbButtonInfos = [previous, toggle, next] };

        vm.Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(PlayerService.IsPlaying)) return;
            toggle.ImageSource = vm.Player.IsPlaying ? pause : play;
            toggle.Description = vm.Player.IsPlaying ? "Pause" : "Play";
        };
    }

    /// <summary>Renders an icon-font glyph to a small white bitmap (taskbar buttons need bitmaps).</summary>
    private static BitmapSource Glyph(string glyph)
    {
        const int size = 32;
        var text = new FormattedText(glyph, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            22, Brushes.White, 1.0);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawText(text, new Point((size - text.Width) / 2, (size - text.Height) / 2));

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
