using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Threading;
using UniGetUI.Core.Logging;

namespace UniGetUI.Avalonia.Infrastructure;

// Collects and returns the working set to the OS once loading has settled — neither .NET nor the
// native allocator hand freed memory back on their own. Debounced so it never runs mid-load.
// Windows-only, best-effort.
internal static class MemoryTrimmer
{
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);
    private static DispatcherTimer? _timer;

    public static void RequestTrimAfterIdle()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RequestTrimAfterIdle);
            return;
        }

        _timer ??= CreateTimer();
        _timer.Stop();
        _timer.Start();
    }

    public static void CancelPending()
    {
        if (!OperatingSystem.IsWindows()) return;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(CancelPending);
            return;
        }

        _timer?.Stop();
    }

    private static DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer { Interval = SettleDelay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Trim();
        };
        return timer;
    }

    private static void Trim() => _ = Task.Run(() =>
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true);
            NativeMethods.SetProcessWorkingSetSize(NativeMethods.GetCurrentProcess(), -1, -1);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Post-load working-set trim failed: {ex.Message}");
        }
    });

    private static class NativeMethods
    {
        [DllImport("kernel32.dll")]
        public static extern nint GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetProcessWorkingSetSize(
            nint hProcess,
            nint dwMinimumWorkingSetSize,
            nint dwMaximumWorkingSetSize
        );
    }
}
