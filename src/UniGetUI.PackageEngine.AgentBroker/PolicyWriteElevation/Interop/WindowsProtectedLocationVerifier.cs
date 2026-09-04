#if WINDOWS
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;

namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation.Interop;

/// <summary>
/// The outcome of verifying a packaged install location from held kernel handles.
/// </summary>
/// <remarks>
/// <see cref="FailureReason"/> is safe to show to a user and names no path, principal or right.
/// <see cref="Detail"/> carries the specifics and belongs in the developer log only.
/// </remarks>
public sealed class PolicyElevationLocationVerification : IDisposable
{
    private readonly SafeFileHandle[] _handles;

    private PolicyElevationLocationVerification(
        bool isProtected,
        SafeFileHandle[] handles,
        string? canonicalInstallRoot,
        string? canonicalHelperPath,
        string? canonicalHostPath,
        string? failureReason,
        string? detail,
        int? win32ErrorCode)
    {
        IsProtected = isProtected;
        _handles = handles;
        CanonicalInstallRoot = canonicalInstallRoot;
        CanonicalHelperPath = canonicalHelperPath;
        CanonicalHostPath = canonicalHostPath;
        FailureReason = failureReason;
        Detail = detail;
        Win32ErrorCode = win32ErrorCode;
    }

    public bool IsProtected { get; }

    /// <summary>The install root as the kernel resolved it from the held directory handle.</summary>
    public string? CanonicalInstallRoot { get; }

    /// <summary>The helper path as the kernel resolved it from the held file handle.</summary>
    public string? CanonicalHelperPath { get; }

    /// <summary>The host path as the kernel resolved it from the held file handle.</summary>
    public string? CanonicalHostPath { get; }

    public string? FailureReason { get; }

    public string? Detail { get; }

    public int? Win32ErrorCode { get; }

    internal static PolicyElevationLocationVerification Protected(
        SafeFileHandle[] handles,
        string canonicalInstallRoot,
        string canonicalHelperPath,
        string canonicalHostPath)
        => new(true, handles, canonicalInstallRoot, canonicalHelperPath, canonicalHostPath, null, null, null);

    /// <summary>
    /// A successful verification that holds no handles.
    /// </summary>
    /// <remarks>
    /// This exists purely so the surrounding protocol can be driven by a test
    /// <see cref="IPolicyElevationLocationVerifier"/>. It is not a bypass: the shipping composition
    /// roots only ever construct <see cref="WindowsProtectedLocationVerifier"/>, which is the only
    /// code that can produce a verification backed by real kernel handles.
    /// </remarks>
    public static PolicyElevationLocationVerification Verified(
        string canonicalInstallRoot,
        string canonicalHelperPath,
        string canonicalHostPath)
        => new(true, [], canonicalInstallRoot, canonicalHelperPath, canonicalHostPath, null, null, null);

    public static PolicyElevationLocationVerification Rejected(
        string reason,
        string? detail = null,
        int? win32ErrorCode = null)
        => new(false, [], null, null, null, reason, detail, win32ErrorCode);

    /// <summary>
    /// Releases the handles that pinned the verified objects. The caller must keep this alive for
    /// the whole exchange: while the handles are open the verified directories and files cannot be
    /// deleted, renamed or modified out from under the elevation, which is what closes the window
    /// between "the path was checked" and "the binary was launched".
    /// </summary>
    public void Dispose()
    {
        foreach (SafeFileHandle handle in _handles)
        {
            handle.Dispose();
        }
    }
}

/// <summary>
/// Verifies that a packaged install location is genuinely administrator-protected. Injected so the
/// surrounding protocol can be tested; the product only ever composes
/// <see cref="WindowsProtectedLocationVerifier"/>.
/// </summary>
public interface IPolicyElevationLocationVerifier
{
    PolicyElevationLocationVerification Verify(string installRoot, string helperPath, string hostPath);
}

/// <summary>
/// Handle-based replacement for the old lexical install-root test.
/// </summary>
/// <remarks>
/// <para>
/// Each object in the packaged layout — the volume root, every directory down to the install root,
/// the <c>Assets\Utilities</c> staging directories, the helper and the host — is opened with
/// <c>FILE_FLAG_OPEN_REPARSE_POINT</c> so that a junction, symbolic link or mount point is seen
/// rather than silently followed. Any reparse point anywhere in the chain is a rejection: a
/// standard user who can plant one can redirect the "protected" path at will.
/// </para>
/// <para>
/// Identity then comes from the handle, not the name: <c>GetFinalPathNameByHandleW</c> reports what
/// the kernel actually opened, and that must equal the exact expected path. A path swapped between
/// the lookup and the open therefore fails, and because every handle stays open for the lifetime of
/// the returned <see cref="PolicyElevationLocationVerification"/>, none of the verified objects can
/// be deleted, renamed or modified during the exchange.
/// </para>
/// <para>
/// Finally the security descriptor of every object is read from its handle and evaluated by
/// <see cref="PolicyElevationAccessPolicy"/>: write, delete, replace and security-control rights
/// must belong exclusively to SYSTEM, the built-in Administrators group, or TrustedInstaller.
/// </para>
/// </remarks>
public sealed class WindowsProtectedLocationVerifier : IPolicyElevationLocationVerifier
{
    private const string GenericRejection =
        "Elevated policy writes require UniGetUI to be installed in an administrator-protected location.";

    public PolicyElevationLocationVerification Verify(string installRoot, string helperPath, string hostPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(installRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(helperPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostPath);

        var handles = new List<SafeFileHandle>();
        try
        {
            // The staging directories below the install root are as sensitive as the root itself.
            string utilities = Path.Combine(
                installRoot,
                PolicyElevationProtocol.HelperRelativeDirectory,
                PolicyElevationProtocol.HelperRelativeSubDirectory);

            if (!TryOpenDirectoryChain(installRoot, handles, out string? canonicalRoot, out PolicyElevationLocationVerification? failure)
                || canonicalRoot is null)
            {
                return failure!;
            }

            foreach (string staged in EnumerateStagingDirectories(installRoot, utilities))
            {
                if (!TryVerifyObject(
                        staged,
                        isDirectory: true,
                        PolicyElevationAccessPolicy.DirectoryControlMask,
                        handles,
                        out _,
                        out failure))
                {
                    return failure!;
                }
            }

            if (!TryVerifyObject(
                    helperPath,
                    isDirectory: false,
                    PolicyElevationAccessPolicy.FileControlMask,
                    handles,
                    out string? canonicalHelper,
                    out failure)
                || canonicalHelper is null)
            {
                return failure!;
            }

            if (!TryVerifyObject(
                    hostPath,
                    isDirectory: false,
                    PolicyElevationAccessPolicy.FileControlMask,
                    handles,
                    out string? canonicalHost,
                    out failure)
                || canonicalHost is null)
            {
                return failure!;
            }

            SafeFileHandle[] held = [.. handles];
            handles.Clear();
            return PolicyElevationLocationVerification.Protected(
                held,
                canonicalRoot,
                canonicalHelper,
                canonicalHost);
        }
        finally
        {
            foreach (SafeFileHandle handle in handles)
            {
                handle.Dispose();
            }
        }
    }

    /// <summary>
    /// Applies the whole per-object rule set — open without following reparse points, reject a
    /// reparse point, require the handle-resolved path to match, and require control to be
    /// restricted to trusted principals — to exactly one file or directory.
    /// </summary>
    /// <remarks>
    /// Exposed so each rule can be exercised in isolation against a real object. The product never
    /// calls this directly: it uses <see cref="Verify"/>, which applies the same rules to the whole
    /// packaged layout and keeps the resulting handles open.
    /// </remarks>
    public static PolicyElevationLocationVerification InspectObject(
        string path,
        bool isDirectory,
        uint controlMask)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var handles = new List<SafeFileHandle>();
        try
        {
            if (!TryVerifyObject(
                    path,
                    isDirectory,
                    controlMask,
                    handles,
                    out string? canonical,
                    out PolicyElevationLocationVerification? failure)
                || canonical is null)
            {
                return failure!;
            }

            return PolicyElevationLocationVerification.Verified(canonical, canonical, canonical);
        }
        finally
        {
            foreach (SafeFileHandle handle in handles)
            {
                handle.Dispose();
            }
        }
    }

    /// <summary>
    /// The install root plus <c>Assets</c> and <c>Assets\Utilities</c>, in descending order.
    /// </summary>
    private static IEnumerable<string> EnumerateStagingDirectories(string installRoot, string utilities)
    {
        var stack = new Stack<string>();
        for (string? current = utilities;
             current is not null && !PathsAreEqual(current, installRoot);
             current = Path.GetDirectoryName(current))
        {
            stack.Push(current);
        }

        while (stack.Count > 0)
        {
            yield return stack.Pop();
        }
    }

    /// <summary>
    /// Verifies every directory from the volume root down to <paramref name="installRoot"/>.
    /// </summary>
    private static bool TryOpenDirectoryChain(
        string installRoot,
        List<SafeFileHandle> handles,
        out string? canonicalInstallRoot,
        out PolicyElevationLocationVerification? failure)
    {
        canonicalInstallRoot = null;
        failure = null;

        var chain = new Stack<string>();
        string? current = Path.TrimEndingDirectorySeparator(installRoot);
        while (!string.IsNullOrEmpty(current))
        {
            chain.Push(current);
            string? parent = Path.GetDirectoryName(current);
            if (parent is null || PathsAreEqual(parent, current))
            {
                break;
            }

            current = parent;
        }

        while (chain.Count > 0)
        {
            string directory = chain.Pop();
            bool isInstallRoot = chain.Count is 0;

            uint mask = isInstallRoot
                ? PolicyElevationAccessPolicy.DirectoryControlMask
                : PolicyElevationAccessPolicy.AncestorControlMask;

            if (!TryVerifyObject(directory, isDirectory: true, mask, handles, out string? canonical, out failure))
            {
                return false;
            }

            if (isInstallRoot)
            {
                canonicalInstallRoot = canonical;
            }
        }

        return canonicalInstallRoot is not null;
    }

    private static bool TryVerifyObject(
        string path,
        bool isDirectory,
        uint controlMask,
        List<SafeFileHandle> handles,
        out string? canonicalPath,
        out PolicyElevationLocationVerification? failure)
    {
        canonicalPath = null;
        failure = null;

        SafeFileHandle handle = OpenVerificationLease(path, isDirectory);

        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            failure = PolicyElevationLocationVerification.Rejected(
                GenericRejection,
                $"'{path}' could not be opened for verification (Win32 {error}).",
                error);
            return false;
        }

        handles.Add(handle);

        if (!PolicyElevationNative.GetFileInformationByHandle(
                handle,
                out PolicyElevationNative.ByHandleFileInformation information))
        {
            int error = Marshal.GetLastWin32Error();
            failure = PolicyElevationLocationVerification.Rejected(
                GenericRejection,
                $"'{path}' did not expose file information (Win32 {error}).",
                error);
            return false;
        }

        if ((information.dwFileAttributes & PolicyElevationNative.FileAttributeReparsePoint) is not 0)
        {
            failure = PolicyElevationLocationVerification.Rejected(
                GenericRejection,
                $"'{path}' is a reparse point, so the packaged layout can be redirected.");
            return false;
        }

        canonicalPath = WindowsProcessInspector.TryGetFinalPath(handle);
        if (canonicalPath is null)
        {
            failure = PolicyElevationLocationVerification.Rejected(
                GenericRejection,
                $"The kernel did not resolve a final path for '{path}'.");
            return false;
        }

        if (!PathsAreEqual(canonicalPath, path))
        {
            failure = PolicyElevationLocationVerification.Rejected(
                GenericRejection,
                $"'{path}' actually resolved to '{canonicalPath}'.");
            return false;
        }

        if (!TryReadSecurityDescriptor(handle, out RawSecurityDescriptor? descriptor, out int securityError))
        {
            failure = PolicyElevationLocationVerification.Rejected(
                GenericRejection,
                $"The security descriptor of '{path}' could not be read (Win32 {securityError}).",
                securityError);
            return false;
        }

        if (!PolicyElevationAccessPolicy.IsControlRestrictedToTrustedPrincipals(
                descriptor,
                controlMask,
                out string? violation))
        {
            failure = PolicyElevationLocationVerification.Rejected(
                GenericRejection,
                $"'{path}' is not administrator-protected: {violation}");
            return false;
        }

        return true;
    }

    internal static SafeFileHandle OpenVerificationLease(string path, bool isDirectory)
    {
        uint flags = PolicyElevationNative.FileFlagOpenReparsePoint
            | (isDirectory ? PolicyElevationNative.FileFlagBackupSemantics : 0);
        uint desiredAccess = PolicyElevationNative.ReadControl
            | (isDirectory
                ? PolicyElevationNative.FileReadAttributes
                : PolicyElevationNative.GenericRead);
        uint shareMode = PolicyElevationNative.FileShareRead
            | (isDirectory ? PolicyElevationNative.FileShareWrite : 0);

        return PolicyElevationNative.CreateFile(
            path,
            desiredAccess,
            shareMode,
            nint.Zero,
            PolicyElevationNative.OpenExisting,
            flags,
            nint.Zero);
    }

    private static bool TryReadSecurityDescriptor(
        SafeFileHandle handle,
        out RawSecurityDescriptor? descriptor,
        out int errorCode)
    {
        descriptor = null;

        uint status = PolicyElevationNative.GetSecurityInfo(
            handle,
            PolicyElevationNative.SeFileObject,
            PolicyElevationNative.OwnerSecurityInformation | PolicyElevationNative.DaclSecurityInformation,
            nint.Zero,
            nint.Zero,
            nint.Zero,
            nint.Zero,
            out nint raw);

        errorCode = unchecked((int)status);
        if (status is not 0 || raw == nint.Zero)
        {
            return false;
        }

        try
        {
            uint length = PolicyElevationNative.GetSecurityDescriptorLength(raw);
            if (length is 0)
            {
                return false;
            }

            byte[] buffer = new byte[length];
            Marshal.Copy(raw, buffer, 0, buffer.Length);
            descriptor = new RawSecurityDescriptor(buffer, 0);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        finally
        {
            PolicyElevationNative.LocalFree(raw);
        }
    }

    private static bool PathsAreEqual(string? left, string? right)
        => WindowsProcessInspector.PathsAreEqual(left, right);
}
#endif
