#if WINDOWS
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace UniGetUI.PackageEngine.RemoteHosts;

[SupportedOSPlatform("windows")]
internal static partial class WslApi
{
    public const nint InvalidHandle = -1;
    public const uint WaitObject0 = 0;
    public const uint HandleFlagInherit = 1;
    public const uint GenericRead = 0x80000000;
    public const uint FileShareRead = 1;
    public const uint FileShareWrite = 2;
    public const uint OpenExisting = 3;
    public const uint FileAttributeNormal = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    public struct SecurityAttributes
    {
        public int nLength;
        public nint lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [LibraryImport("wslapi", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WslIsDistributionRegistered(string distributionName);

    [LibraryImport("wslapi", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int WslLaunch(
        string distributionName,
        string? command,
        [MarshalAs(UnmanagedType.Bool)] bool useCurrentWorkingDirectory,
        nint stdIn,
        nint stdOut,
        nint stdErr,
        out nint process
    );

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CreatePipe(
        out nint hReadPipe,
        out nint hWritePipe,
        in SecurityAttributes lpPipeAttributes,
        uint nSize
    );

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetHandleInformation(nint hObject, uint dwMask, uint dwFlags);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        in SecurityAttributes lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        nint hTemplateFile
    );

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetExitCodeProcess(nint hProcess, out uint lpExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool TerminateProcess(nint hProcess, uint uExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint hObject);
}
#endif
