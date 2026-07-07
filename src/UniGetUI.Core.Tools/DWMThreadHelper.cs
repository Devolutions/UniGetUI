using System.Diagnostics;
using System.Runtime.InteropServices;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;

namespace UniGetUI;

public class DWMThreadHelper
{
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationThread(
        IntPtr ThreadHandle,
        int ThreadInformationClass,
        ref IntPtr ThreadInformation,
        int ThreadInformationLength,
        out int ReturnLength
    );

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenThread(
        ThreadAccess dwDesiredAccess,
        bool bInheritHandle,
        uint dwThreadId
    );

    [DllImport("kernel32.dll")]
    private static extern uint SuspendThread(IntPtr hThread);

    [DllImport("kernel32.dll")]
    private static extern uint ResumeThread(IntPtr hThread);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    [Flags]
    private enum ThreadAccess : uint
    {
        QUERY_INFORMATION = 0x0040,
        SUSPEND_RESUME = 0x0002,
    }

    private const int ThreadQuerySetWin32StartAddress = 9;
    private static bool DWM_IsSuspended;
    private static bool XAML_IsSuspended;

    private static int? DWMThreadId;
    private static IntPtr? DWMThreadAdress;
    private static int? XAMLThreadId;
    private static IntPtr? XAMLThreadAdress;

    public static void ChangeState_DWM(bool suspend)
    {
        if (Settings.Get(Settings.K.DisableDMWThreadOptimizations))
            return;

        if (DWM_IsSuspended && suspend)
        {
            Logger.Debug("DWM Thread was already suspended");
            return;
        }
        else if (!DWM_IsSuspended && !suspend)
        {
            Logger.Debug("DWM Thread was already running");
            return;
        }

        DWMThreadAdress ??= GetTargetFunctionAddress("dwmcorei.dll", 0x36240); // 0x36170);
        if (DWMThreadAdress is null)
        {
            Logger.Error("Failed to resolve DWM thread start adress.");
            return;
        }

        ChangeState(suspend, (IntPtr)DWMThreadAdress, ref DWM_IsSuspended, ref DWMThreadId, "DWM");
    }

    public static void ChangeState_XAML(bool suspend)
    {
        if (Settings.Get(Settings.K.DisableDMWThreadOptimizations))
            return;

        if (XAML_IsSuspended && suspend)
        {
            Logger.Debug("XAML Thread was already suspended");
            return;
        }
        else if (!XAML_IsSuspended && !suspend)
        {
            Logger.Debug("XAML Thread was already running");
            return;
        }

        // The reported offset on ProcessExplorer seems to be missing a part somehow.
        // To find the real adress, set offset to 0, and then get the offset from the Debugger.Break at ChangeState()
        XAMLThreadAdress ??= GetTargetFunctionAddress("Microsoft.UI.Xaml.dll", 0x27C0E0); //0x2771A0);
        if (XAMLThreadAdress is null)
        {
            Logger.Error("Failed to resolve XAML thread start adress.");
            return;
        }

        ChangeState(
            suspend,
            (IntPtr)XAMLThreadAdress,
            ref XAML_IsSuspended,
            ref XAMLThreadId,
            "XAML"
        );
    }

    private static IntPtr GetThreadStartAdress(int threadId)
    {
        IntPtr hThread = OpenThread(
            ThreadAccess.QUERY_INFORMATION | ThreadAccess.SUSPEND_RESUME,
            false,
            (uint)threadId
        );
        if (hThread == IntPtr.Zero)
            return IntPtr.Zero;

        IntPtr adress = 0x00;
        int status = NtQueryInformationThread(
            hThread,
            ThreadQuerySetWin32StartAddress,
            ref adress,
            Marshal.SizeOf<IntPtr>(),
            out _
        );
        if (status != 0)
            Logger.Warn(
                $"NtQueryInformationThread returned non-zero status code 0x{(uint)status:X}"
            );
        CloseHandle(hThread);
        return adress;
    }

    private static void ChangeState(
        bool suspend,
        IntPtr expectedAdress,
        ref bool IsSuspended,
        ref int? threadId,
        string loggerName,
        bool canRetry = true
    )
    {
        IntPtr minId = -1;
        uint LastDiff = uint.MaxValue;

        if (threadId is null)
        {
            foreach (ProcessThread thread in Process.GetCurrentProcess().Threads)
            {
                var adress = GetThreadStartAdress(thread.Id);
                if (adress == expectedAdress)
                {
                    threadId = thread.Id;
                    Logger.Info($"Thread with Id {threadId} was assigned as {loggerName} thread");
                    break;
                }
                else if (((uint)adress - (uint)expectedAdress) < LastDiff)
                {
                    LastDiff = (uint)adress - (uint)expectedAdress;
                    minId = thread.Id;
                }
            }
        }

        if (threadId is null)
        {
            Logger.Error(
                $"No thread matching {loggerName} with start adress {expectedAdress:X} was found. "
                    + $"Best guess was {minId} with adress offset {LastDiff:X}"
            );
            if (Debugger.IsAttached)
                Debugger.Break();
            return;
        }

        IntPtr hThread = OpenThread(
            ThreadAccess.QUERY_INFORMATION | ThreadAccess.SUSPEND_RESUME,
            false,
            (uint)threadId
        );
        if (hThread == IntPtr.Zero)
        { // When the thread cannot be opened
            if (canRetry)
            {
                threadId = null; // On first try, reset argument threadId so it does get loaded again.
                ChangeState(
                    suspend,
                    expectedAdress,
                    ref IsSuspended,
                    ref threadId,
                    loggerName,
                    false
                );
                return;
            }
            // The threadId was already reloaded
            Logger.Warn(
                $"Thread with id={threadId} and assigned as {loggerName} exists but could not be opened!"
            );
            return;
        }

        if (suspend)
        {
            uint res = SuspendThread(hThread);
            if (res == 0)
            {
                IsSuspended = true;
                Logger.Info($"{loggerName} Thread was suspended successfully");
                CloseHandle(hThread);
                return;
            }
            else
            {
                Logger.Warn($"Could not suspend {loggerName} Thread with NTSTATUS = 0x{res:X}");
            }
        }
        else
        {
            int res = (int)ResumeThread(hThread);
            if (res >= 0)
            {
                IsSuspended = false;
                Logger.Info($"{loggerName} Thread was resumed successfully");
                CloseHandle(hThread);
                return;
            }
            else
            {
                Logger.Error($"Could not resume {loggerName} Thread with NTSTATUS = 0x{res:X}");
            }
        }

        CloseHandle(hThread);
    }

    private static IntPtr? GetTargetFunctionAddress(string moduleName, int offset)
    {
        foreach (ProcessModule module in Process.GetCurrentProcess().Modules)
        {
            if (module.ModuleName.Equals(moduleName, StringComparison.OrdinalIgnoreCase))
            {
                return module.BaseAddress + offset;
            }
        }
        return null;
    }
}
