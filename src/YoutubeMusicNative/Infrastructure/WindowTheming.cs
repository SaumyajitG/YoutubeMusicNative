using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace YoutubeMusicNative.Infrastructure;

/// <summary>
/// Switches the app between the dark and light palettes (Themes/Dark.xaml, Themes/Light.xaml)
/// and colours the native title bar to match (keeps snap layouts etc. without a custom chrome).
/// </summary>
internal static class WindowTheming
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static bool _dark = true;

    /// <summary>Call from a window's constructor so its title bar follows the theme.</summary>
    public static void Attach(Window window) => window.SourceInitialized += (_, _) => ApplyTitleBar(window);

    public static void SetTheme(bool dark)
    {
        _dark = dark;
        // Merged dictionary 0 is the palette (see App.xaml).
        Application.Current.Resources.MergedDictionaries[0] = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/YoutubeMusicNative;component/Themes/{(dark ? "Dark" : "Light")}.xaml"),
        };
        foreach (Window window in Application.Current.Windows) ApplyTitleBar(window);
    }

    private static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var darkMode = _dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkMode, sizeof(int));
        var caption = _dark ? 0x00030303 : 0x00FFFFFF; // COLORREF 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
        var text = _dark ? 0x00FFFFFF : 0x000F0F0F;
        DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(int));
    }
}
