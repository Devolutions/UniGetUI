using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace UniGetUI.Interface.AgentRegistration;

/// <summary>An opened registration pipe whose server must be authenticated before any write.</summary>
internal interface IAgentPipeConnection : IAsyncDisposable
{
    Stream Stream { get; }

    /// <summary>Checks the server is the running DevolutionsAgent service in session 0.</summary>
    bool VerifyServer();
}

internal interface IAgentPipeConnector
{
    /// <summary>Opens the registration pipe, or returns null when it is not available.</summary>
    Task<IAgentPipeConnection?> ConnectAsync(CancellationToken ct);
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsAgentPipeConnector : IAgentPipeConnector
{
    public const string PipePath = @"\\.\pipe\Devolutions.Now.PackageBroker.Registration.v1";

    public Task<IAgentPipeConnection?> ConnectAsync(CancellationToken ct) =>
        Task.Run(
            () =>
            {
                SafePipeHandle handle = AgentPipeNative.CreateFileW(
                    PipePath,
                    AgentPipeNative.GenericRead | AgentPipeNative.FileWriteData,
                    0,
                    0,
                    AgentPipeNative.OpenExisting,
                    AgentPipeNative.FileFlagOverlapped
                        | AgentPipeNative.SecuritySqosPresent
                        | AgentPipeNative.SecurityIdentification,
                    0
                );
                if (handle.IsInvalid)
                {
                    handle.Dispose();
                    return null;
                }

                return (IAgentPipeConnection?)new WindowsAgentPipeConnection(handle);
            },
            ct
        );
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsAgentPipeConnection : IAgentPipeConnection
{
    private const string ServiceName = "DevolutionsAgent";
    private readonly SafePipeHandle _handle;
    private readonly NamedPipeClientStream _stream;

    public WindowsAgentPipeConnection(SafePipeHandle handle)
    {
        _handle = handle;
        _stream = new NamedPipeClientStream(PipeDirection.InOut, true, true, handle);
    }

    public Stream Stream => _stream;

    public bool VerifyServer()
    {
        try
        {
            if (
                !AgentPipeNative.GetNamedPipeServerProcessId(_handle, out uint serverPid)
                || !AgentPipeNative.GetNamedPipeServerSessionId(_handle, out uint serverSession)
                || serverSession != 0
            )
            {
                return false;
            }

            nint scm = AgentPipeNative.OpenSCManagerW(null, null, AgentPipeNative.ScManagerConnect);
            if (scm == 0)
            {
                return false;
            }

            try
            {
                nint service = AgentPipeNative.OpenServiceW(
                    scm,
                    ServiceName,
                    AgentPipeNative.ServiceQueryStatus
                );
                if (service == 0)
                {
                    return false;
                }

                try
                {
                    return AgentPipeNative.QueryServiceStatusEx(
                            service,
                            AgentPipeNative.ScStatusProcessInfo,
                            out AgentPipeNative.ServiceStatusProcess status,
                            (uint)Marshal.SizeOf<AgentPipeNative.ServiceStatusProcess>(),
                            out _
                        )
                        && status.CurrentState == AgentPipeNative.ServiceRunning
                        && status.ProcessId != 0
                        && status.ProcessId == serverPid;
                }
                finally
                {
                    AgentPipeNative.CloseServiceHandle(service);
                }
            }
            finally
            {
                AgentPipeNative.CloseServiceHandle(scm);
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    public ValueTask DisposeAsync()
    {
        _stream.Dispose();
        _handle.Dispose();
        return ValueTask.CompletedTask;
    }
}

[SupportedOSPlatform("windows")]
internal static partial class AgentPipeNative
{
    public const uint GenericRead = 0x80000000;
    public const uint FileWriteData = 0x0002;
    public const uint OpenExisting = 3;
    public const uint FileFlagOverlapped = 0x40000000;
    public const uint SecuritySqosPresent = 0x00100000;
    public const uint SecurityIdentification = 0x00010000;
    public const uint ScManagerConnect = 0x0001;
    public const uint ServiceQueryStatus = 0x0004;
    public const int ScStatusProcessInfo = 0;
    public const uint ServiceRunning = 4;

    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial SafePipeHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile
    );

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint serverProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetNamedPipeServerSessionId(SafePipeHandle pipe, out uint serverSessionId);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    public static partial nint OpenServiceW(nint scManager, string serviceName, uint desiredAccess);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryServiceStatusEx(
        nint service,
        int infoLevel,
        out ServiceStatusProcess buffer,
        uint bufferSize,
        out uint bytesNeeded
    );

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseServiceHandle(nint handle);
}
