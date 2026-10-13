using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace YoutubeMusicNative.Infrastructure;

/// <summary>Dark native title bar (keeps snap layouts, Aero shake, etc. without a custom chrome).</summary>
internal static class WindowTheming
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void ApplyDark(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            var on = 1;
            DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
            var caption = 0x00030303; // COLORREF 0x00BBGGRR
            DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref caption, sizeof(int));
            var text = 0x00FFFFFF;
            DwmSetWindowAttribute(hwnd, DwmwaTextColor, ref text, sizeof(int));
        };
    }
}
