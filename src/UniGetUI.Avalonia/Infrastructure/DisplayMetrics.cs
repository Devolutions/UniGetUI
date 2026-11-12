using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using UniGetUI.Core.Logging;

namespace UniGetUI.Avalonia.Infrastructure;

internal static partial class DisplayMetrics
{
    public static long LargestScreenPixels()
    {
        if (!OperatingSystem.IsWindows())
            return 0;

        try
        {
            return LargestMonitorPixels();
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not read the monitor sizes");
            Logger.Warn(ex);
            return 0;
        }
    }

    private static unsafe long LargestMonitorPixels()
    {
        long largest = 0;
        EnumDisplayMonitors(0, 0, &OnMonitor, (nint)(&largest));
        return largest;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe int OnMonitor(nint monitor, nint hdc, Bounds* bounds, nint largest)
    {
        long pixels = (long)(bounds->Right - bounds->Left) * (bounds->Bottom - bounds->Top);
        long* current = (long*)largest;
        if (pixels > *current)
            *current = pixels;
        return 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Bounds
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool EnumDisplayMonitors(
        nint hdc,
        nint clip,
        delegate* unmanaged[Stdcall]<nint, nint, Bounds*, nint, int> callback,
        nint data);
}
