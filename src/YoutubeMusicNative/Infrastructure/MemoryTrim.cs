using System.Diagnostics;
using System.Runtime.InteropServices;

namespace YoutubeMusicNative.Infrastructure;

internal static class MemoryTrim
{
    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint min, nint max);

    /// <summary>
    /// Collects garbage and asks Windows to page out what the process isn't actively using.
    /// Pages come back on demand when the window is restored.
    /// </summary>
    public static void TrimWorkingSet()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using var self = Process.GetCurrentProcess();
        SetProcessWorkingSetSize(self.Handle, -1, -1);
    }
}
