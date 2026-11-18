using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Threading;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Interface;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Enums;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// Puts the process in Windows' efficiency mode (EcoQoS, the green leaf in Task Manager) while
/// UniGetUI sits in the background with nothing to do, and takes it out again as soon as there
/// is something to do. In efficiency mode Windows runs the process at reduced clock speed, on
/// efficiency cores where the CPU has them, so background update checks cost less power.
///
/// It is on only while all of these hold: the window is hidden or minimized, the package
/// managers have finished loading (so startup is never slowed, including a start straight to
/// the tray), and no operation is queued or running (so an install started from a notification,
/// or an automatic update, runs at full speed). Only the power-throttling state is changed, not
/// the priority class, which child processes would inherit.
///
/// Does nothing on other platforms or when the DisableBackgroundEfficiencyMode setting is set.
/// </summary>
internal static partial class BackgroundEfficiencyMode
{
    private static bool _enabled;
    private static bool _subscribedToManagersLoaded;

    /// <summary>Whether the process is currently in efficiency mode because of this class.</summary>
    internal static bool IsEnabled => _enabled;

    /// <summary>
    /// Re-evaluates whether the process should be in efficiency mode. Call it whenever the
    /// window goes on or off screen, or an operation is added, removed or changes status.
    /// Safe to call from any thread.
    /// </summary>
    public static void Refresh()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 16299))
            return;

        if (Dispatcher.UIThread.CheckAccess())
            RefreshOnUIThread();
        else
            Dispatcher.UIThread.Post(RefreshOnUIThread);
    }

    /// <summary>The decision itself, separated from its inputs.</summary>
    internal static bool ShouldBeEnabled(
        bool disabledBySetting,
        bool windowOnScreen,
        bool managersLoaded,
        bool anyActiveOperation
    ) => !disabledBySetting && !windowOnScreen && managersLoaded && !anyActiveOperation;

    [SupportedOSPlatform("windows10.0.16299")]
    private static void RefreshOnUIThread()
    {
        if (!_subscribedToManagersLoaded)
        {
            _subscribedToManagersLoaded = true;
            PEInterface.ManagersLoaded += (_, _) => Refresh();

            // Operations started over IPC (the command line) are not in the shell's operation
            // list, so they are followed where every operation is tracked.
            IpcOperationApi.ActivityChanged += (_, _) => Refresh();
        }

        bool wanted = ShouldBeEnabled(
            Settings.Get(Settings.K.DisableBackgroundEfficiencyMode),
            Views.MainWindow.IsWindowOnScreen,
            PEInterface.AreManagersLoaded,
            AvaloniaOperationRegistry.Operations.Any(operation =>
                operation.Status is OperationStatus.InQueue or OperationStatus.Running)
                || IpcOperationApi.HasActiveOperations()
        );

        if (wanted == _enabled)
            return;

        if (TrySetPowerThrottling(wanted))
        {
            _enabled = wanted;
            Logger.Debug(wanted ? "Entered efficiency mode (background, idle)" : "Left efficiency mode");
        }
    }

    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [SupportedOSPlatform("windows10.0.16299")]
    private static bool TrySetPowerThrottling(bool throttle)
    {
        try
        {
            // Throttling on: take control of execution speed and turn throttling on.
            // Throttling off: hand control back to Windows (both masks zero), its default.
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = throttle ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
                StateMask = throttle ? PROCESS_POWER_THROTTLING_EXECUTION_SPEED : 0,
            };

            if (SetProcessInformation(
                    GetCurrentProcess(),
                    ProcessPowerThrottling,
                    ref state,
                    (uint)Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>()))
            {
                return true;
            }

            Logger.Warn($"Could not change efficiency mode (error {Marshal.GetLastPInvokeError()})");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not change efficiency mode");
            Logger.Warn(ex);
            return false;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessInformation(
        nint hProcess,
        int processInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE processInformation,
        uint processInformationSize
    );

    [LibraryImport("kernel32.dll")]
    private static partial nint GetCurrentProcess();
}
