#if WINDOWS
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation.Interop;

/// <summary>Everything the two peers assert about each other's process.</summary>
public readonly record struct ProcessIdentitySnapshot(
    uint ProcessId,
    long CreationTimeUtcTicks,
    uint SessionId,
    string ImagePath,
    bool IsElevated,
    bool IsAdministrator);

/// <summary>
/// Kernel-sourced identity for a process the caller already holds a handle to. Nothing here
/// re-opens a process by id on the host side: the launched handle is kept alive for the whole
/// exchange so the process id cannot be recycled underneath the checks.
/// </summary>
public static class WindowsProcessInspector
{
    private const int MaxPathCharacters = 32768;
    public const int MaxEffectiveUserCharacters = 512;

    public static bool TryGetProcessId(nint processHandle, out uint processId)
    {
        processId = PolicyElevationNative.GetProcessId(processHandle);
        return processId is not 0;
    }

    public static bool TryGetCreationTimeUtcTicks(nint processHandle, out long ticks)
    {
        ticks = 0;
        if (!PolicyElevationNative.GetProcessTimes(processHandle, out long creation, out _, out _, out _)
            || creation <= 0)
        {
            return false;
        }

        try
        {
            ticks = DateTime.FromFileTimeUtc(creation).Ticks;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        return ticks > 0;
    }

    public static bool TryGetSessionId(uint processId, out uint sessionId)
        => PolicyElevationNative.ProcessIdToSessionId(processId, out sessionId);

    public static bool TryGetImagePath(nint processHandle, out string? imagePath)
    {
        imagePath = null;

        char[] buffer = new char[MaxPathCharacters];
        uint size = (uint)buffer.Length;

        if (!PolicyElevationNative.QueryFullProcessImageName(
                processHandle,
                PolicyElevationNative.FileNameNormalized,
                ref buffer[0],
                ref size)
            || size is 0)
        {
            return false;
        }

        imagePath = new string(buffer, 0, (int)size);
        return imagePath.Length > 0;
    }

    /// <summary>
    /// Reports both "the token is elevated" and "the token is a member of BUILTIN\Administrators".
    /// Failure to determine either is reported as a failure so callers can fail closed.
    /// </summary>
    public static bool TryGetTokenElevation(nint processHandle, out bool isElevated, out bool isAdministrator)
    {
        isElevated = false;
        isAdministrator = false;

        if (!PolicyElevationNative.OpenProcessToken(
                processHandle,
                PolicyElevationNative.TokenQuery | PolicyElevationNative.TokenDuplicate,
                out SafeAccessTokenHandle token))
        {
            return false;
        }

        using (token)
        {
            if (!PolicyElevationNative.GetTokenInformation(
                    token,
                    PolicyElevationNative.TokenElevationInformationClass,
                    out uint elevation,
                    sizeof(uint),
                    out _))
            {
                return false;
            }

            isElevated = elevation is not 0;

            if (!PolicyElevationNative.DuplicateTokenEx(
                    token,
                    PolicyElevationNative.TokenQuery,
                    nint.Zero,
                    PolicyElevationNative.SecurityImpersonationLevel,
                    PolicyElevationNative.TokenImpersonationType,
                    out SafeAccessTokenHandle impersonation))
            {
                return false;
            }

            using (impersonation)
            {
                var administrators = new System.Security.Principal.SecurityIdentifier(
                    System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid,
                    null);

                byte[] sid = new byte[administrators.BinaryLength];
                administrators.GetBinaryForm(sid, 0);

                if (!PolicyElevationNative.CheckTokenMembership(impersonation, sid, out bool member))
                {
                    return false;
                }

                isAdministrator = member;
            }
        }

        return true;
    }

    /// <summary>
    /// Resolves the Windows account carried by a retained process handle. This intentionally reads
    /// the process token rather than the current process environment, so over-the-shoulder elevation
    /// cannot substitute the administrator who approved the prompt for the initiating user.
    /// </summary>
    public static bool TryGetEffectiveUser(nint processHandle, out string? effectiveUser)
    {
        effectiveUser = null;
        if (processHandle is 0 or -1)
            return false;

        if (!PolicyElevationNative.OpenProcessToken(
                processHandle,
                PolicyElevationNative.TokenQuery,
                out SafeAccessTokenHandle token))
        {
            return false;
        }

        using (token)
        {
            try
            {
                using var identity = new WindowsIdentity(token.DangerousGetHandle());
                string? name = identity.Name;
                if (!IsValidEffectiveUser(name))
                {
                    return false;
                }

                effectiveUser = name;
                return true;
            }
            catch (Exception ex) when (ex is ArgumentException
                                           or SecurityException
                                           or UnauthorizedAccessException
                                           or IdentityNotMappedException)
            {
                return false;
            }
        }
    }

    public static bool IsValidEffectiveUser(string? effectiveUser)
    {
        if (string.IsNullOrWhiteSpace(effectiveUser)
            || effectiveUser.Length > MaxEffectiveUserCharacters
            || effectiveUser.Any(char.IsControl))
        {
            return false;
        }

        int separator = effectiveUser.IndexOf('\\');
        return separator > 0
               && separator < effectiveUser.Length - 1
               && effectiveUser.IndexOf('\\', separator + 1) < 0;
    }

    public static bool TryGetExitCode(nint processHandle, out uint exitCode)
        => PolicyElevationNative.GetExitCodeProcess(processHandle, out exitCode);

    /// <summary>
    /// Resolves a path to its final on-disk identity: reparse points, junctions, 8.3 short names
    /// and casing are all normalised, so a comparison against the expected packaged path cannot
    /// be defeated by an alias.
    /// </summary>
    public static string? TryGetCanonicalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            using SafeFileHandle handle = File.OpenHandle(
                Path.GetFullPath(path),
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                FileOptions.None);

            char[] buffer = new char[MaxPathCharacters];
            uint written = PolicyElevationNative.GetFinalPathNameByHandle(
                handle,
                ref buffer[0],
                (uint)buffer.Length,
                PolicyElevationNative.VolumeNameDos);

            if (written is 0 || written >= buffer.Length)
            {
                return null;
            }

            string resolved = new(buffer, 0, (int)written);
            return StripExtendedPrefix(resolved);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or NotSupportedException or Win32Exception or ExternalException)
        {
            return null;
        }
    }

    /// <summary>Canonical path of the currently running executable, or null when unavailable.</summary>
    public static string? TryGetCurrentProcessCanonicalPath()
    {
        string? path = Environment.ProcessPath;
        return path is null ? null : TryGetCanonicalPath(path);
    }

    /// <summary>
    /// The path the kernel resolved for an already open handle. Unlike a name-based lookup this
    /// cannot be redirected after the handle was obtained, so it is the identity a security
    /// decision should be based on.
    /// </summary>
    public static string? TryGetFinalPath(SafeFileHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);

        if (handle.IsInvalid || handle.IsClosed)
        {
            return null;
        }

        char[] buffer = new char[MaxPathCharacters];
        uint written = PolicyElevationNative.GetFinalPathNameByHandle(
            handle,
            ref buffer[0],
            (uint)buffer.Length,
            PolicyElevationNative.VolumeNameDos);

        if (written is 0 || written >= buffer.Length)
        {
            return null;
        }

        return StripExtendedPrefix(new string(buffer, 0, (int)written));
    }

    public static bool PathsAreEqual(string? left, string? right)
        => left is not null
            && right is not null
            && string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string StripExtendedPrefix(string path)
    {
        const string uncPrefix = @"\\?\UNC\";
        const string dosPrefix = @"\\?\";

        if (path.StartsWith(uncPrefix, StringComparison.Ordinal))
        {
            return string.Concat(@"\\", path.AsSpan(uncPrefix.Length));
        }

        return path.StartsWith(dosPrefix, StringComparison.Ordinal)
            ? path[dosPrefix.Length..]
            : path;
    }
}

public static class PolicyElevationInitiatingUserResolver
{
    public static int Resolve(nint authenticatedHostProcessHandle, out string? effectiveUser)
        => WindowsProcessInspector.TryGetEffectiveUser(
            authenticatedHostProcessHandle,
            out effectiveUser)
            ? PolicyElevationProtocol.ExitSuccess
            : PolicyElevationProtocol.ExitPeerAuthenticationFailed;
}
#endif
