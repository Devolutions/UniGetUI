using System.Text;
using Microsoft.Win32.SafeHandles;
using UniGetUI.Core.Logging;

namespace UniGetUI.PackageEngine.RemoteHosts;

public sealed class WslLaunchTransport : IRemotePosixTransport
{
    public async Task<RemoteProcessResult> RunAsync(
        RemoteHost host,
        string posixCommand,
        Action<string>? onProgress = null,
        CancellationToken cancellationToken = default
    )
    {
#if WINDOWS
        if (!OperatingSystem.IsWindows())
            throw new RemoteSshException(RemoteSshErrorKind.WslNotAvailable, host.Destination);

        return await Task.Run(
            () => RunBlocking(host, posixCommand, onProgress, cancellationToken),
            cancellationToken
        ).ConfigureAwait(false);
#else
        throw new RemoteSshException(RemoteSshErrorKind.WslNotAvailable, host.Destination);
#endif
    }

#if WINDOWS
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static RemoteProcessResult RunBlocking(
        RemoteHost host,
        string posixCommand,
        Action<string>? onProgress,
        CancellationToken cancellationToken
    )
    {
        try
        {
            if (!WslApi.WslIsDistributionRegistered(host.Destination))
            {
                throw new RemoteSshException(RemoteSshErrorKind.WslDistroNotFound, host.Destination);
            }
        }
        catch (DllNotFoundException)
        {
            throw new RemoteSshException(RemoteSshErrorKind.WslNotAvailable, host.Destination);
        }
        catch (EntryPointNotFoundException)
        {
            throw new RemoteSshException(RemoteSshErrorKind.WslNotAvailable, host.Destination);
        }

        WslApi.SecurityAttributes inherit = new()
        {
            nLength = System.Runtime.InteropServices.Marshal.SizeOf<WslApi.SecurityAttributes>(),
            lpSecurityDescriptor = 0,
            bInheritHandle = 1,
        };

        nint stdoutRead = 0, stdoutWrite = 0, stderrRead = 0, stderrWrite = 0, stdin = 0, process = 0;
        try
        {
            if (!WslApi.CreatePipe(out stdoutRead, out stdoutWrite, in inherit, 0)
                || !WslApi.SetHandleInformation(stdoutRead, WslApi.HandleFlagInherit, 0))
            {
                throw new RemoteSshException(RemoteSshErrorKind.WslLaunchFailed, host.Destination, "Could not create stdout pipe.");
            }

            if (!WslApi.CreatePipe(out stderrRead, out stderrWrite, in inherit, 0)
                || !WslApi.SetHandleInformation(stderrRead, WslApi.HandleFlagInherit, 0))
            {
                throw new RemoteSshException(RemoteSshErrorKind.WslLaunchFailed, host.Destination, "Could not create stderr pipe.");
            }

            stdin = WslApi.CreateFileW(
                "NUL",
                WslApi.GenericRead,
                WslApi.FileShareRead | WslApi.FileShareWrite,
                in inherit,
                WslApi.OpenExisting,
                WslApi.FileAttributeNormal,
                0
            );
            if (stdin == 0 || stdin == WslApi.InvalidHandle)
            {
                throw new RemoteSshException(RemoteSshErrorKind.WslLaunchFailed, host.Destination, "Could not open NUL for stdin.");
            }

            int hr;
            try
            {
                hr = WslApi.WslLaunch(
                    host.Destination,
                    posixCommand,
                    useCurrentWorkingDirectory: false,
                    stdin,
                    stdoutWrite,
                    stderrWrite,
                    out process
                );
            }
            catch (DllNotFoundException)
            {
                throw new RemoteSshException(RemoteSshErrorKind.WslNotAvailable, host.Destination);
            }
            catch (EntryPointNotFoundException)
            {
                throw new RemoteSshException(RemoteSshErrorKind.WslNotAvailable, host.Destination);
            }

            CloseAndClear(ref stdin);
            CloseAndClear(ref stdoutWrite);
            CloseAndClear(ref stderrWrite);

            if (hr < 0 || process == 0)
            {
                throw new RemoteSshException(
                    RemoteSshErrorKind.WslLaunchFailed,
                    host.Destination,
                    $"WslLaunch failed (0x{hr:X8})."
                );
            }

            SafeFileHandle stdoutHandle = new(stdoutRead, ownsHandle: true);
            SafeFileHandle stderrHandle = new(stderrRead, ownsHandle: true);
            stdoutRead = 0;
            stderrRead = 0;

            Task<string> stdoutTask = Task.Run(
                () => ReadUtf8Pipe(stdoutHandle, onProgress, cancellationToken),
                cancellationToken
            );
            Task<string> stderrTask = Task.Run(
                () => ReadUtf8Pipe(stderrHandle, onProgress, cancellationToken),
                cancellationToken
            );

            using (cancellationToken.Register(() =>
            {
                try { WslApi.TerminateProcess(process, 1); }
                catch { /* already exited */ }
            }))
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    uint wait = WslApi.WaitForSingleObject(process, 200);
                    if (wait == WslApi.WaitObject0)
                        break;
                }
            }

            if (!WslApi.GetExitCodeProcess(process, out uint exitCode))
                exitCode = 1;

            string stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();
            return new RemoteProcessResult((int)exitCode, stdout, stderr);
        }
        finally
        {
            CloseAndClear(ref stdin);
            CloseAndClear(ref stdoutWrite);
            CloseAndClear(ref stderrWrite);
            CloseAndClear(ref stdoutRead);
            CloseAndClear(ref stderrRead);
            CloseAndClear(ref process);
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string ReadUtf8Pipe(
        SafeFileHandle handle,
        Action<string>? onProgress,
        CancellationToken cancellationToken
    )
    {
        try
        {
            using var stream = new FileStream(handle, FileAccess.Read, 4096, isAsync: false);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 4096, leaveOpen: false);
            var builder = new StringBuilder();
            while (reader.ReadLine() is { } line)
            {
                cancellationToken.ThrowIfCancellationRequested();
                builder.AppendLine(line);
                onProgress?.Invoke(line);
            }

            return builder.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warn("Failed to read a WSL pipe");
            Logger.Warn(ex);
            return "";
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void CloseAndClear(ref nint handle)
    {
        if (handle == 0 || handle == WslApi.InvalidHandle)
        {
            handle = 0;
            return;
        }

        WslApi.CloseHandle(handle);
        handle = 0;
    }
#endif
}
