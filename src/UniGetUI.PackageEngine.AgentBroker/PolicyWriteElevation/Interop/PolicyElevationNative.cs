#if WINDOWS
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation.Interop;

/// <summary>
/// Every native entry point used by the elevated policy-write channel. All declarations use
/// <see cref="LibraryImportAttribute"/> so the generated marshalling stays NativeAOT friendly.
/// </summary>
internal static partial class PolicyElevationNative
{
    internal const uint SeeMaskNoCloseProcess = 0x00000040;
    internal const uint SeeMaskNoAsync = 0x00000100;
    internal const uint SeeMaskFlagNoUi = 0x00000400;
    internal const uint SeeMaskNoZoneChecks = 0x00800000;
    internal const int SwHide = 0;

    internal const uint ProcessQueryLimitedInformation = 0x1000;
    internal const uint ProcessQueryInformation = 0x0400;
    internal const uint Synchronize = 0x00100000;

    internal const uint TokenQuery = 0x0008;
    internal const uint TokenDuplicate = 0x0002;
    internal const int TokenElevationInformationClass = 20;
    internal const int SecurityImpersonationLevel = 2;
    internal const int TokenImpersonationType = 2;

    internal const uint FileNameNormalized = 0x0;
    internal const uint VolumeNameDos = 0x0;

    internal const int ErrorInsufficientBuffer = 122;

    [StructLayout(LayoutKind.Sequential)]
    internal struct ShellExecuteInfoW
    {
        public uint cbSize;
        public uint fMask;
        public nint hwnd;
        public nint lpVerb;
        public nint lpFile;
        public nint lpParameters;
        public nint lpDirectory;
        public int nShow;
        public nint hInstApp;
        public nint lpIDList;
        public nint lpClass;
        public nint hkeyClass;
        public uint dwHotKey;
        public nint hIconOrMonitor;
        public nint hProcess;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WinTrustFileInfo
    {
        public uint cbStruct;
        public nint pcwszFilePath;
        public nint hFile;
        public nint pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WinTrustData
    {
        public uint cbStruct;
        public nint pPolicyCallbackData;
        public nint pSipClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public nint pFile;
        public uint dwStateAction;
        public nint hWVTStateData;
        public nint pwszUrlReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public nint pSignatureSettings;
    }

    internal const uint WtdUiNone = 2;
    internal const uint WtdRevokeNone = 0;
    internal const uint WtdChoiceFile = 1;
    internal const uint WtdStateActionVerify = 1;
    internal const uint WtdStateActionClose = 2;
    internal const uint WtdSaferFlag = 0x100;
    internal const uint WtdCacheOnlyUrlRetrieval = 0x1000;
    internal const uint WtdLifetimeSigningFlag = 0x800;

    internal static readonly Guid WinTrustActionGenericVerifyV2 =
        new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [LibraryImport("shell32.dll", EntryPoint = "ShellExecuteExW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShellExecuteEx(ref ShellExecuteInfoW lpExecInfo);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint GetProcessId(nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessTimes(
        nint process,
        out long creationTime,
        out long exitTime,
        out long kernelTime,
        out long userTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "QueryFullProcessImageNameW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryFullProcessImageName(
        nint process,
        uint flags,
        ref char exeName,
        ref uint size);

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "GetFinalPathNameByHandleW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        ref char filePath,
        uint charCount,
        uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeClientProcessId(SafeHandle pipe, out uint clientProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeServerProcessId(SafeHandle pipe, out uint serverProcessId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNamedPipeClientSessionId(SafeHandle pipe, out uint clientSessionId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(
        nint process,
        uint desiredAccess,
        out SafeAccessTokenHandle tokenHandle);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetTokenInformation(
        SafeAccessTokenHandle tokenHandle,
        int tokenInformationClass,
        out uint tokenInformation,
        uint tokenInformationLength,
        out uint returnLength);

    [LibraryImport("advapi32.dll", EntryPoint = "DuplicateTokenEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DuplicateTokenEx(
        SafeAccessTokenHandle existingToken,
        uint desiredAccess,
        nint tokenAttributes,
        int impersonationLevel,
        int tokenType,
        out SafeAccessTokenHandle newToken);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CheckTokenMembership(
        SafeAccessTokenHandle tokenHandle,
        byte[] sidToCheck,
        [MarshalAs(UnmanagedType.Bool)] out bool isMember);

    [LibraryImport("wintrust.dll", EntryPoint = "WinVerifyTrust")]
    internal static partial int WinVerifyTrust(nint window, ref Guid actionId, ref WinTrustData data);

    // These two structures are only ever populated by WinTrust through a pointer, so the compiler
    // cannot see any managed assignment to their fields.
#pragma warning disable CS0649
    /// <summary>
    /// Leading fields of <c>CRYPT_PROVIDER_CERT</c>. Only the certificate context pointer is read,
    /// and the trailing fields are deliberately omitted because the structure is only ever accessed
    /// through a pointer owned by WinTrust.
    /// </summary>
    internal struct CryptProviderCert
    {
        public uint cbStruct;
        public nint pCert;
    }

    /// <summary>
    /// <c>CERT_CONTEXT</c>, whose encoded blob is the DER certificate WinTrust actually validated.
    /// </summary>
    internal struct CertContext
    {
        public uint dwCertEncodingType;
        public nint pbCertEncoded;
        public uint cbCertEncoded;
        public nint pCertInfo;
        public nint hCertStore;
    }
#pragma warning restore CS0649

    [LibraryImport("wintrust.dll", EntryPoint = "WTHelperProvDataFromStateData")]
    internal static partial nint WTHelperProvDataFromStateData(nint stateData);

    [LibraryImport("wintrust.dll", EntryPoint = "WTHelperGetProvSignerFromChain")]
    internal static partial nint WTHelperGetProvSignerFromChain(
        nint providerData,
        uint signerIndex,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner,
        uint counterSignerIndex);

    [LibraryImport("wintrust.dll", EntryPoint = "WTHelperGetProvCertFromChain")]
    internal static partial nint WTHelperGetProvCertFromChain(nint signer, uint certificateIndex);

    // ---- Handle-based location verification -------------------------------------------------

    internal const uint GenericRead = 0x80000000;
    internal const uint FileReadAttributes = 0x0080;
    internal const uint ReadControl = 0x00020000;
    internal const uint FileShareRead = 0x00000001;
    internal const uint FileShareWrite = 0x00000002;
    internal const uint FileShareDelete = 0x00000004;
    internal const uint OpenExisting = 3;
    internal const uint FileFlagBackupSemantics = 0x02000000;
    internal const uint FileFlagOpenReparsePoint = 0x00200000;
    internal const uint FileAttributeReparsePoint = 0x00000400;

    /// <summary>SE_FILE_OBJECT — the object type passed to <c>GetSecurityInfo</c>.</summary>
    internal const int SeFileObject = 1;

    internal const uint OwnerSecurityInformation = 0x00000001;
    internal const uint DaclSecurityInformation = 0x00000004;

    [StructLayout(LayoutKind.Sequential)]
    internal struct FileTime
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ByHandleFileInformation
    {
        public uint dwFileAttributes;
        public FileTime ftCreationTime;
        public FileTime ftLastAccessTime;
        public FileTime ftLastWriteTime;
        public uint dwVolumeSerialNumber;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint nNumberOfLinks;
        public uint nFileIndexHigh;
        public uint nFileIndexLow;
    }

    [LibraryImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetFileInformationByHandle(
        SafeFileHandle file,
        out ByHandleFileInformation fileInformation);

    [LibraryImport("advapi32.dll", EntryPoint = "GetSecurityInfo", SetLastError = false)]
    internal static partial uint GetSecurityInfo(
        SafeFileHandle handle,
        int objectType,
        uint securityInformation,
        nint owner,
        nint group,
        nint dacl,
        nint sacl,
        out nint securityDescriptor);

    [LibraryImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorLength")]
    internal static partial uint GetSecurityDescriptorLength(nint securityDescriptor);

    [LibraryImport("kernel32.dll", EntryPoint = "LocalFree")]
    internal static partial nint LocalFree(nint memory);
}
#endif
