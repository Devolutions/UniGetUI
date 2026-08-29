#if WINDOWS
using UniGetUI.Core.Data;
using UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation.Interop;

namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

/// <summary>Where the packaged helper and host live, once every check has passed.</summary>
/// <remarks>
/// <see cref="FailureReason"/> is written for a user interface and never names a path.
/// <see cref="Detail"/> carries the path for the developer log only.
/// </remarks>
public sealed record PolicyElevationHelperLocation(
    bool Found,
    string? CanonicalHelperPath = null,
    string? CanonicalHostPath = null,
    string? CanonicalInstallRoot = null,
    string? FailureReason = null,
    string? Detail = null,
    PolicyElevationLocationVerification? Verification = null)
{
    public static PolicyElevationHelperLocation NotFound(string reason, string? detail = null)
        => new(false, FailureReason: reason, Detail: detail);
}

public interface IPolicyElevationHelperLocator
{
    PolicyElevationHelperLocation Locate();
}

/// <summary>
/// Fail-closed discovery. The helper is accepted at exactly one path inside the packaged install
/// tree; there is no PATH lookup, no environment override, no "nearby" fallback and no copy to a
/// writable staging directory.
/// </summary>
public sealed class PolicyElevationHelperLocator : IPolicyElevationHelperLocator
{
    private readonly Func<string> _installRootProvider;
    private readonly Func<string?, string?> _canonicalize;
    private readonly Func<string, bool> _fileExists;
    private readonly IPolicyElevationLocationVerifier _locationVerifier;

    public PolicyElevationHelperLocator()
        : this(() => CoreData.UniGetUIExecutableDirectory)
    {
    }

    public PolicyElevationHelperLocator(
        Func<string> installRootProvider,
        Func<string?, string?>? canonicalize = null,
        Func<string, bool>? fileExists = null,
        IPolicyElevationLocationVerifier? locationVerifier = null)
    {
        _installRootProvider = installRootProvider;
        _canonicalize = canonicalize ?? WindowsProcessInspector.TryGetCanonicalPath;
        _fileExists = fileExists ?? File.Exists;
        _locationVerifier = locationVerifier ?? new WindowsProtectedLocationVerifier();
    }

    public PolicyElevationHelperLocation Locate()
    {
        string installRoot;
        try
        {
            installRoot = _installRootProvider();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return PolicyElevationHelperLocation.NotFound("The UniGetUI install root could not be resolved.");
        }

        if (string.IsNullOrWhiteSpace(installRoot))
        {
            return PolicyElevationHelperLocation.NotFound("The UniGetUI install root could not be resolved.");
        }

        string helperPath = PolicyElevationPaths.GetHelperPath(installRoot);
        string hostPath = PolicyElevationPaths.GetHostPath(installRoot);

        if (!_fileExists(helperPath))
        {
            return PolicyElevationHelperLocation.NotFound(
                "The elevated policy helper is not part of this UniGetUI installation.",
                $"The elevated policy helper is not present at '{helperPath}'.");
        }

        if (!_fileExists(hostPath))
        {
            return PolicyElevationHelperLocation.NotFound(
                "This UniGetUI installation is not laid out as a packaged install.",
                $"The packaged UniGetUI host is not present at '{hostPath}'.");
        }

        string? canonicalHelperPath = _canonicalize(helperPath);
        string? canonicalHostPath = _canonicalize(hostPath);

        if (canonicalHelperPath is null || canonicalHostPath is null)
        {
            return PolicyElevationHelperLocation.NotFound(
                "The packaged policy elevation binaries could not be canonicalised.");
        }

        if (!PolicyElevationPaths.TryGetInstallRootFromHelperPath(canonicalHelperPath, out string? canonicalRoot)
            || canonicalRoot is null)
        {
            return PolicyElevationHelperLocation.NotFound(
                "The elevated policy helper is not laid out as a packaged binary.");
        }

        if (!WindowsProcessInspector.PathsAreEqual(
                canonicalHostPath,
                PolicyElevationPaths.GetHostPath(canonicalRoot)))
        {
            return PolicyElevationHelperLocation.NotFound(
                "The elevated policy helper and the UniGetUI host do not share one install root.");
        }

        // The name-based work above only narrowed the candidate down. The binding decision is made
        // from kernel handles: no reparse point anywhere in the chain, handle-resolved paths that
        // match exactly, and write/delete/replace/security-control restricted to SYSTEM,
        // Administrators and TrustedInstaller. The handles stay open in the returned verification
        // so the objects cannot be swapped between this check and the launch that follows.
        PolicyElevationLocationVerification verification = _locationVerifier.Verify(
            canonicalRoot,
            canonicalHelperPath,
            canonicalHostPath);

        if (!verification.IsProtected)
        {
            verification.Dispose();
            return PolicyElevationHelperLocation.NotFound(
                verification.FailureReason
                ?? "Elevated policy writes require UniGetUI to be installed in an administrator-protected location.",
                verification.Detail);
        }

        if (!WindowsProcessInspector.PathsAreEqual(verification.CanonicalHelperPath, canonicalHelperPath)
            || !WindowsProcessInspector.PathsAreEqual(verification.CanonicalHostPath, canonicalHostPath)
            || !WindowsProcessInspector.PathsAreEqual(verification.CanonicalInstallRoot, canonicalRoot))
        {
            verification.Dispose();
            return PolicyElevationHelperLocation.NotFound(
                "The packaged policy elevation binaries could not be verified.",
                "The handle-resolved packaged paths did not match the canonical paths that were checked.");
        }

        return new PolicyElevationHelperLocation(
            true,
            canonicalHelperPath,
            canonicalHostPath,
            canonicalRoot,
            Verification: verification);
    }
}
#endif
