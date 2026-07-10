using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Threading;
using UniGetUI.Core.Logging;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// Returns reclaimable memory to the OS once a package load has settled. .NET and the native
/// allocator keep freed memory committed for reuse, so a search/refresh leaves the working set high
/// even after the app goes idle. This collects the managed heap and hands the freed pages back so
/// the reported footprint drops once the list has finished loading. Windows-only, best-effort.
///
/// Debounced: a trim is scheduled when a load finishes and cancelled if another load starts, so it
/// only runs when everything is quiet (and never mid-load).
/// </summary>
internal static class MemoryTrimmer
{
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(3);
    private static DispatcherTimer? _timer;

    /// <summary>Schedule a trim once loading has been quiet for a moment.</summary>
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

    /// <summary>Cancel a pending trim because a new load has started.</summary>
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
