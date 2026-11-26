using System.Runtime.InteropServices;

namespace UniGetUI.PackageEngine.Managers.WingetManager;

internal static partial class WindowsInstallerLock
{
    private const string MutexName = @"Global\_MSIExecute";
    private const uint Synchronize = 0x00100000;
    private const int ErrorFileNotFound = 2;
    private const int ErrorAccessDenied = 5;

    internal static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan UnknownStateDelay = TimeSpan.FromSeconds(30);
    internal const int ConsecutiveFreeProbes = 2;

    internal static Func<bool?> ProbeBusy = ProbeMutex;
    internal static Func<TimeSpan, CancellationToken, Task> Delay = Task.Delay;

    internal static async Task WaitBeforeRetryAsync(CancellationToken cancellationToken)
    {
        await Delay(SettleDelay, cancellationToken).ConfigureAwait(false);

        TimeSpan waited = TimeSpan.Zero;
        int freeProbes = 0;
        while (waited < MaxWait)
        {
            switch (ProbeBusy())
            {
                case null:
                    await Delay(UnknownStateDelay, cancellationToken).ConfigureAwait(false);
                    return;
                case false:
                    if (++freeProbes >= ConsecutiveFreeProbes)
                        return;
                    break;
                default:
                    freeProbes = 0;
                    break;
            }

            await Delay(PollInterval, cancellationToken).ConfigureAwait(false);
            waited += PollInterval;
        }
    }

    private static bool? ProbeMutex()
    {
        nint handle = OpenMutex(Synchronize, false, MutexName);
        if (handle != 0)
        {
            CloseHandle(handle);
            return true;
        }

        return Marshal.GetLastPInvokeError() switch
        {
            ErrorFileNotFound => false,
            ErrorAccessDenied => true,
            _ => null,
        };
    }

    [LibraryImport("kernel32.dll", EntryPoint = "OpenMutexW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint OpenMutex(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        string name
    );

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
