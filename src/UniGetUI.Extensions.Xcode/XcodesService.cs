using UniGetUI.Extensions.Xcode.AppleAuth;
using UniGetUI.Extensions.Xcode.Local;

namespace UniGetUI.Extensions.Xcode;

/// <summary>
/// Lists, installs and selects Xcode on this Mac, and manages the Apple ID it downloads with. It has no user
/// interface of its own, so the page and the tests can share it; the tests give it a backend that touches nothing.
/// </summary>
public sealed class XcodesService
{
    internal const string UnknownBuildMessage =
        "The exact build is not yet known. Check again after release details are available.";

    private readonly IXcodeBackend _backend;

    /// <param name="catalog">The Xcode Releases list, shared with whatever else reads it.</param>
    /// <param name="archiveDirectory">Where downloaded archives are kept until they are installed.</param>
    /// <param name="account">The Apple ID to download with; one is shared by everything that signs in.</param>
    public XcodesService(IXcodeReleasesCatalog catalog, Func<string> archiveDirectory, AppleAccount account)
        : this(new XcodeNativeBackend(catalog, account, archiveDirectory))
    {
    }

    internal XcodesService(IXcodeBackend backend)
    {
        _backend = backend;
    }

    /// <summary>The saved Apple ID, read again from the keychain, and the session last checked.</summary>
    public XcodeAccountStatus GetAccount() => _backend.GetAccount();

    /// <summary>Asks Apple whether the saved session is still signed in, without prompting or signing in again.</summary>
    public Task<XcodeAccountStatus> RefreshAccountAsync(CancellationToken cancellationToken = default)
        => _backend.RefreshAccountAsync(cancellationToken);

    /// <summary>Signs in, asking <paramref name="prompts"/> for the Apple ID, password and codes it needs.</summary>
    /// <param name="suggestedAppleId">The Apple ID to sign in with, or null to ask for one.</param>
    public Task<XcodeAccountStatus> SignInAsync(XcodesPromptHandler prompts, string? suggestedAppleId = null,
        CancellationToken cancellationToken = default)
        => _backend.SignInAsync(prompts, suggestedAppleId, cancellationToken);

    /// <summary>Forgets the Apple ID, its password and its session.</summary>
    public Task SignOutAsync(CancellationToken cancellationToken = default) => _backend.SignOutAsync(cancellationToken);

    /// <summary>Every Xcode that can be installed on this Mac, newest first.</summary>
    /// <param name="refresh">Download the list again instead of using the saved copy.</param>
    public Task<IReadOnlyList<XcodeRelease>> GetReleasesAsync(
        bool refresh, CancellationToken cancellationToken = default, string? installDirectory = null)
        => _backend.GetReleasesAsync(refresh, installDirectory, cancellationToken);

    /// <summary>The Xcode apps on this Mac, newest first.</summary>
    public Task<IReadOnlyList<InstalledXcode>> GetInstalledAsync(
        CancellationToken cancellationToken = default, string? installDirectory = null)
        => _backend.GetInstalledAsync(installDirectory, cancellationToken);

    /// <summary>
    /// Downloads and installs an Xcode next to any others; it is never replaced. Asks <paramref name="prompts"/>
    /// whatever it needs to know to sign in. Without a prompt handler, requires an already valid Apple session.
    /// Returns the installed path and bundle identity verified by the installer.
    /// </summary>
    public Task<XcodeInstallationResult> InstallAsync(
        XcodeRelease release,
        XcodeInstallOptions options,
        XcodesPromptHandler? prompts,
        Action<XcodesOutputLine>? output = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRelease(release);
        return _backend.InstallAsync(release, options, prompts, output, cancellationToken);
    }

    /// <summary>
    /// Downloads an Xcode archive to UniGetUI's cache without installing it. A later installation can reuse it.
    /// The archive's Apple signature and build are checked during installation, not by this method.
    /// </summary>
    public Task<string> DownloadAsync(
        XcodeRelease release,
        XcodesPromptHandler? prompts,
        Action<XcodesOutputLine>? output = null,
        CancellationToken cancellationToken = default,
        string? archiveDirectory = null)
    {
        ValidateRelease(release);
        return _backend.DownloadAsync(release, prompts, output, cancellationToken, archiveDirectory);
    }

    private static void ValidateRelease(XcodeRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!XcodeVersion.TryParse(release.Version, out _))
            throw new ArgumentException($"'{release.Version}' is not an Xcode version.", nameof(release));
        if (HasUnusableIdentity(release))
            throw new XcodesException(UnknownBuildMessage);
    }

    internal static bool HasUnusableIdentity(XcodeRelease release)
        => release.IsIdentityUnverified
           || (release.Build is not null && !XcodeSimulatorImageFile.IsValidBuild(release.Build));

    /// <summary>Validates an installation directory, or returns null for the default one.</summary>
    public static string? ValidateInstallDirectory(string? directory)
    {
        if (string.IsNullOrEmpty(directory))
            return null;
        if (!Path.IsPathFullyQualified(directory) || directory.Contains('\0'))
            throw new XcodesException("Enter an absolute path for the Xcode installation directory.");
        if (!Directory.Exists(directory))
            throw new XcodesException("The Xcode installation directory does not exist or is inaccessible.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    }

    /// <summary>Validates an archive directory, or returns null for the default cache folder.</summary>
    public static string? ValidateArchiveDirectory(string? directory)
    {
        if (string.IsNullOrEmpty(directory))
            return null;
        if (!Path.IsPathFullyQualified(directory) || directory.Contains('\0'))
            throw new XcodesException("Enter an absolute path for the Xcode archive directory.");
        if (!Directory.Exists(directory))
            throw new XcodesException("The Xcode archive directory does not exist or is inaccessible.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
    }

    /// <summary>
    /// Makes this specific installed Xcode the one command line tools use. macOS asks for an administrator's
    /// password itself.
    /// </summary>
    public Task SelectAsync(
        InstalledXcode xcode,
        Action<XcodesOutputLine>? output = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(xcode);
        string path = xcode.Path?.Trim() ?? "";
        if (!Path.IsPathFullyQualified(path) || !path.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"'{xcode.Path}' is not an installed Xcode app path.", nameof(xcode));
        return _backend.SelectAsync(path, output, cancellationToken);
    }

    /// <summary>
    /// The Xcode to offer as an update: the newest one listed that is newer than every Xcode installed. A
    /// beta is only offered when <paramref name="includePrereleases"/> says so. Nothing is offered to a Mac with no
    /// Xcode, since there is nothing there to update.
    /// </summary>
    public static XcodeRelease? FindUpdate(
        IEnumerable<XcodeRelease> releases,
        IReadOnlyCollection<InstalledXcode> installed,
        bool includePrereleases)
    {
        if (installed.Count == 0)
            return null;

        XcodeRelease[] all = [.. releases];
        XcodeRelease? newest = null;
        foreach (XcodeRelease release in all)
        {
            if (HasUnusableIdentity(release) || (release.IsPrerelease && !includePrereleases))
                continue;
            if (newest is null || XcodeVersion.Compare(release.Version, newest.Version) > 0)
                newest = release;
        }
        if (newest is null)
            return null;

        // The list can have more than one row for a version, and only the one matching an installed build is marked installed.
        if (all.Any(release => !HasUnusableIdentity(release) && release.IsInstalled
            && XcodeVersion.Compare(release.Version, newest.Version) == 0))
            return null;

        string newestInstalled = installed
            .Select(xcode => xcode.Version)
            .Aggregate((left, right) => XcodeVersion.Compare(left, right) >= 0 ? left : right);
        return XcodeVersion.Compare(newest.Version, newestInstalled) > 0 ? newest : null;
    }

    /// <summary>Finds a stable counterpart to the active prerelease without choosing or installing it.</summary>
    public static string? FindStableCounterpart(
        InstalledXcode active,
        IEnumerable<XcodeRelease> releases,
        IEnumerable<InstalledXcode> installed)
    {
        if (!active.IsSelected || active.Architecture == XcodeArchitecture.Unspecified
            || !XcodeVersion.TryParse(active.Version, out XcodeVersion prerelease) || !prerelease.IsPrerelease)
            return null;

        bool Matches(string version, XcodeArchitecture architecture)
            => architecture == active.Architecture
               && XcodeVersion.TryParse(version, out XcodeVersion candidate)
               && !candidate.IsPrerelease
               && candidate.Major == prerelease.Major
               && candidate.Minor == prerelease.Minor
               && candidate.Patch == prerelease.Patch;

        return installed.Where(candidate => Matches(candidate.Version, candidate.Architecture))
                   .Select(candidate => candidate.Version).FirstOrDefault()
               ?? releases.Where(candidate => !HasUnusableIdentity(candidate)
                   && Matches(candidate.Version, candidate.Architecture))
                   .Select(candidate => candidate.Version).FirstOrDefault();
    }
}
