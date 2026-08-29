#if WINDOWS
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation.Interop;

namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

/// <summary>A launched elevated helper whose process handle is held for the whole exchange.</summary>
public interface IElevatedHelperProcess : IDisposable
{
    uint ProcessId { get; }

    long CreationTimeUtcTicks { get; }

    uint SessionId { get; }

    /// <summary>The live process handle. Holding it is what makes <see cref="ProcessId"/> trustworthy.</summary>
    nint Handle { get; }

    bool HasExited { get; }

    /// <summary>Waits for exit and returns the exit code, or null when the wait timed out.</summary>
    Task<int?> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Outcome of trying to raise the consent prompt and start the helper.</summary>
public sealed record ElevatedHelperLaunchResult(
    IElevatedHelperProcess? Process,
    PolicyElevationOutcome FailureOutcome = PolicyElevationOutcome.Replaced,
    string? FailureReason = null,
    int? Win32ErrorCode = null)
{
    public bool Succeeded => Process is not null;

    public static ElevatedHelperLaunchResult Failed(
        PolicyElevationOutcome outcome,
        string reason,
        int? win32ErrorCode = null)
        => new(null, outcome, reason, win32ErrorCode);
}

public interface IElevatedHelperLauncher
{
    Task<ElevatedHelperLaunchResult> LaunchAsync(
        string helperPath,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>
/// Starts the helper through <c>ShellExecuteEx</c> with the <c>runas</c> verb, which is what
/// raises the consent prompt. The command line carries routing arguments only.
/// </summary>
public sealed class WindowsElevatedHelperLauncher : IElevatedHelperLauncher
{
    public async Task<ElevatedHelperLaunchResult> LaunchAsync(
        string helperPath,
        string arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
        ArgumentNullException.ThrowIfNull(arguments);

        var completion = new TaskCompletionSource<ElevatedHelperLaunchResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // ShellExecuteEx wants an STA thread with COM initialised.
        var worker = new Thread(() => completion.TrySetResult(LaunchCore(helperPath, arguments)))
        {
            IsBackground = true,
            Name = "UniGetUI policy elevation launcher",
        };

        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();

        try
        {
            return await completion.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DisposeLateProcess(completion.Task);
            return ElevatedHelperLaunchResult.Failed(
                PolicyElevationOutcome.Cancelled,
                "The elevation request was cancelled.");
        }
        catch (TimeoutException)
        {
            DisposeLateProcess(completion.Task);
            return ElevatedHelperLaunchResult.Failed(
                PolicyElevationOutcome.TimedOut,
                "The elevation prompt did not complete before the deadline.");
        }
    }

    private static void DisposeLateProcess(Task<ElevatedHelperLaunchResult> completion)
    {
        _ = completion.ContinueWith(
            static task =>
            {
                if (task.Status == TaskStatus.RanToCompletion)
                {
                    task.Result.Process?.Dispose();
                }

                _ = task.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static ElevatedHelperLaunchResult LaunchCore(string helperPath, string arguments)
    {
        nint verb = Marshal.StringToHGlobalUni("runas");
        nint file = Marshal.StringToHGlobalUni(helperPath);
        nint parameters = Marshal.StringToHGlobalUni(arguments);
        nint directory = Marshal.StringToHGlobalUni(Path.GetDirectoryName(helperPath) ?? string.Empty);

        try
        {
            var info = new PolicyElevationNative.ShellExecuteInfoW
            {
                cbSize = (uint)Marshal.SizeOf<PolicyElevationNative.ShellExecuteInfoW>(),
                fMask = PolicyElevationNative.SeeMaskNoCloseProcess
                    | PolicyElevationNative.SeeMaskNoAsync
                    | PolicyElevationNative.SeeMaskFlagNoUi
                    | PolicyElevationNative.SeeMaskNoZoneChecks,
                lpVerb = verb,
                lpFile = file,
                lpParameters = parameters,
                lpDirectory = directory,
                nShow = PolicyElevationNative.SwHide,
            };

            if (!PolicyElevationNative.ShellExecuteEx(ref info))
            {
                int error = Marshal.GetLastWin32Error();
                return error == PolicyElevationProtocol.ErrorCancelled
                    ? ElevatedHelperLaunchResult.Failed(
                        PolicyElevationOutcome.UserDeclinedElevation,
                        "The elevation prompt was dismissed.",
                        error)
                    : ElevatedHelperLaunchResult.Failed(
                        PolicyElevationOutcome.LaunchFailed,
                        "The elevated policy helper could not be started.",
                        error);
            }

            if (info.hProcess == nint.Zero)
            {
                return ElevatedHelperLaunchResult.Failed(
                    PolicyElevationOutcome.LaunchFailed,
                    "The shell did not return a handle to the elevated policy helper.");
            }

            var handle = new SafeProcessHandle(info.hProcess, ownsHandle: true);
            ElevatedHelperProcess? process = ElevatedHelperProcess.TryCreate(handle);
            if (process is null)
            {
                handle.Dispose();
                return ElevatedHelperLaunchResult.Failed(
                    PolicyElevationOutcome.LaunchFailed,
                    "The identity of the elevated policy helper could not be established.",
                    Marshal.GetLastWin32Error());
            }

            return new ElevatedHelperLaunchResult(process);
        }
        finally
        {
            Marshal.FreeHGlobal(verb);
            Marshal.FreeHGlobal(file);
            Marshal.FreeHGlobal(parameters);
            Marshal.FreeHGlobal(directory);
        }
    }
}

internal sealed class ElevatedHelperProcess : IElevatedHelperProcess
{
    private readonly SafeProcessHandle _handle;
    private bool _disposed;

    private ElevatedHelperProcess(
        SafeProcessHandle handle,
        uint processId,
        long creationTimeUtcTicks,
        uint sessionId)
    {
        _handle = handle;
        ProcessId = processId;
        CreationTimeUtcTicks = creationTimeUtcTicks;
        SessionId = sessionId;
    }

    public uint ProcessId { get; }

    public long CreationTimeUtcTicks { get; }

    public uint SessionId { get; }

    public nint Handle => _disposed ? nint.Zero : _handle.DangerousGetHandle();

    public bool HasExited =>
        WindowsProcessInspector.TryGetExitCode(Handle, out uint code) && code is not 259;

    internal static ElevatedHelperProcess? TryCreate(SafeProcessHandle handle)
    {
        nint raw = handle.DangerousGetHandle();

        if (!WindowsProcessInspector.TryGetProcessId(raw, out uint processId)
            || !WindowsProcessInspector.TryGetCreationTimeUtcTicks(raw, out long creationTime)
            || !WindowsProcessInspector.TryGetSessionId(processId, out uint sessionId))
        {
            return null;
        }

        return new ElevatedHelperProcess(handle, processId, creationTime, sessionId);
    }

    public async Task<int?> WaitForExitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!await ProcessExitWaiter.WaitAsync(_handle, timeout, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return WindowsProcessInspector.TryGetExitCode(Handle, out uint exitCode) ? unchecked((int)exitCode) : null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }
}

internal static class ProcessExitWaiter
{
    public static async Task<bool> WaitAsync(
        SafeProcessHandle processHandle,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var waitHandle = new ManualResetEvent(false);
        SafeWaitHandle previous = waitHandle.SafeWaitHandle;
        waitHandle.SafeWaitHandle = new SafeWaitHandle(processHandle.DangerousGetHandle(), ownsHandle: false);
        previous.Dispose();

        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        RegisteredWaitHandle? registration = null;
        registration = ThreadPool.RegisterWaitForSingleObject(
            waitHandle,
            static (state, timedOut) => ((TaskCompletionSource<bool>)state!).TrySetResult(!timedOut),
            completion,
            timeout,
            executeOnlyOnce: true);

        await using CancellationTokenRegistration cancellation = cancellationToken.Register(
            static state => ((TaskCompletionSource<bool>)state!).TrySetCanceled(),
            completion);

        try
        {
            return await completion.Task.ConfigureAwait(false);
        }
        finally
        {
            registration.Unregister(null);
        }
    }
}
#endif
