// The installation flow follows Xcodes.app and xcodes (https://github.com/XcodesOrg), MIT License.
// See THIRD_PARTY_NOTICES.md.

using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using UniGetUI.Core.Logging;
using UniGetUI.Extensions.Xcode.AppleAuth;

namespace UniGetUI.Extensions.Xcode.Local;

/// <summary>
/// Lists, installs and selects Xcode on this Mac without any other tool: the release list comes from Xcode
/// Releases, the download from Apple with UniGetUI's own sign-in, and the rest from the tools macOS ships.
/// </summary>
internal sealed class XcodeNativeBackend : IXcodeBackend
{
    internal const int StepCount = XcodeArchiveInstaller.StepCount;

    private readonly IXcodeReleasesCatalog _catalog;
    private readonly AppleAccount _account;
    private readonly XcodeToolRunner _slow;
    private readonly Func<string> _archiveDirectory;
    private readonly XcodeArchitecture _machine;
    private readonly InstalledXcodeScanner _scanner;
    private readonly XcodeArchiveInstaller _installer;
    private readonly SemaphoreSlim _appleCatalogLock = new(1, 1);
    private AppleCatalogSnapshot? _appleCatalog;
    private long _accountRevision;

    public XcodeNativeBackend(IXcodeReleasesCatalog catalog, AppleAccount account, Func<string> archiveDirectory,
        XcodeToolRunner? quick = null, XcodeToolRunner? slow = null, XcodeArchitecture? machine = null)
    {
        _catalog = catalog;
        _account = account;
        _archiveDirectory = archiveDirectory;
        quick ??= XcodeTools.Quick;
        _slow = slow ?? XcodeTools.Long;
        _machine = machine ?? (RuntimeInformation.OSArchitecture == Architecture.Arm64
            ? XcodeArchitecture.AppleSilicon : XcodeArchitecture.Intel);
        _scanner = new InstalledXcodeScanner(quick);
        _installer = new XcodeArchiveInstaller(quick, _slow);
        _account.Changed += (_, _) => InvalidateAppleCatalog();
    }

    /// <summary>Replaces the download for tests.</summary>
    internal Func<Uri, string?, Action<double?>, CancellationToken, Task<string>>? Download { get; init; }

    /// <summary>Replaces the Apple download list for tests.</summary>
    internal Func<CancellationToken, Task<IReadOnlyList<AppleXcodeDownload>>>? FetchAppleDownloads { get; init; }

    public async Task<IReadOnlyList<InstalledXcode>> GetInstalledAsync(string? installDirectory, CancellationToken cancellationToken)
    {
        IReadOnlyList<XcodeBundleInfo> bundles = await _scanner.ScanBundlesAsync(
            XcodesService.ValidateInstallDirectory(installDirectory), cancellationToken).ConfigureAwait(false);
        string? selected = await _scanner.GetSelectedAppAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<XcodeCatalogRelease>? catalog = bundles.Count == 0 ? null : await SavedCatalogAsync(cancellationToken).ConfigureAwait(false);
        return [.. bundles
            .Select(bundle => new InstalledXcode(Label(bundle, catalog), bundle.Build, bundle.Architecture,
                selected is not null && InstalledXcodeScanner.Resolve(bundle.Path) == selected, bundle.Path))
            .OrderByDescending(xcode => xcode.Version, Comparer<string>.Create(XcodeVersion.Compare))];
    }

    /// <summary>
    /// The bundle's version never names a beta, so the release with the same build names it; without one, a beta
    /// is at least called one.
    /// </summary>
    internal static string Label(XcodeBundleInfo bundle, IReadOnlyList<XcodeCatalogRelease>? catalog)
        => catalog?.FirstOrDefault(release => string.Equals(release.Build, bundle.Build, StringComparison.Ordinal))?.Version
           ?? (bundle.IsBeta ? $"{bundle.ShortVersion} Beta" : bundle.ShortVersion);

    public async Task<IReadOnlyList<XcodeRelease>> GetReleasesAsync(bool refresh, string? installDirectory, CancellationToken cancellationToken)
    {
        IReadOnlyList<XcodeCatalogRelease> catalog = refresh
            ? await FetchCatalogAsync(cancellationToken).ConfigureAwait(false)
            : await SavedCatalogAsync(cancellationToken).ConfigureAwait(false)
              ?? await FetchCatalogAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<InstalledXcode> installed = await GetInstalledAsync(installDirectory, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<AppleXcodeDownload> apple = await AppleDownloadsAsync(refresh, cancellationToken).ConfigureAwait(false);
        return Merge(catalog, installed, _machine, apple);
    }

    /// <summary>
    /// The releases this Mac can download and run, marked installed only where the same build of the same
    /// architecture is: a version number alone does not say which build is there.
    /// </summary>
    internal static IReadOnlyList<XcodeRelease> Merge(
        IEnumerable<XcodeCatalogRelease> catalog, IReadOnlyList<InstalledXcode> installed, XcodeArchitecture machine,
        IReadOnlyList<AppleXcodeDownload>? apple = null)
    {
        XcodeCatalogRelease[] entries = [.. catalog];
        IReadOnlyList<AppleXcodeDownload> appleDownloads = apple ?? [];
        List<XcodeRelease> releases = [.. entries
            .Where(release => release.Download is not null && Runs(release.Architecture, machine))
            .GroupBy(release => (release.Version, release.Build, release.Architecture))
            .Select(group => group.First())
            .Select(release =>
            {
                bool knownBuild = XcodeSimulatorImageFile.IsValidBuild(release.Build);
                InstalledXcode[] matches = [.. installed.Where(xcode => knownBuild
                    && string.Equals(xcode.Build, release.Build, StringComparison.Ordinal)
                    && (xcode.Architecture == XcodeArchitecture.Unspecified || xcode.Architecture == release.Architecture))];
                return new XcodeRelease(release.Version, knownBuild ? release.Build : null, release.Architecture,
                    matches.Length > 0, matches.Any(xcode => xcode.IsSelected))
                {
                    IsIdentityUnverified = !knownBuild,
                    AppleSizeMB = knownBuild ? AppleSize(release.Download!, appleDownloads) : null,
                };
            })
            .DistinctBy(release => (release.Version, release.Build, release.Architecture))];

        foreach (AppleXcodeDownload download in appleDownloads)
        {
            if (releases.Any(release => XcodeVersion.Compare(release.Version, download.Version) == 0))
                continue;
            releases.Add(new XcodeRelease(download.Version, null, XcodeArchitecture.Unspecified, false, false)
            {
                IsIdentityUnverified = true,
            });
        }
        return [.. releases.OrderByDescending(release => release.Version, Comparer<string>.Create(XcodeVersion.Compare))
            .ThenBy(release => release.IsIdentityUnverified)
            .ThenBy(release => ArchitecturePreference(release.Architecture, machine))];
    }

    private static int? AppleSize(Uri address, IReadOnlyList<AppleXcodeDownload> downloads)
    {
        long[] sizes = [.. downloads.Where(item => item.Address == address && item.SizeBytes is > 0)
            .Select(item => item.SizeBytes!.Value).Distinct()];
        if (sizes.Length != 1)
            return null;
        long megabytes = sizes[0] / 1_000_000 + (sizes[0] % 1_000_000 == 0 ? 0 : 1);
        return megabytes <= int.MaxValue ? (int)megabytes : null;
    }

    private static bool Runs(XcodeArchitecture release, XcodeArchitecture machine)
        => release is XcodeArchitecture.Universal or XcodeArchitecture.Unspecified || release == machine;

    private static int ArchitecturePreference(XcodeArchitecture release, XcodeArchitecture machine)
        => release == machine && machine != XcodeArchitecture.Unspecified ? 0
            : release == XcodeArchitecture.Universal ? 1 : 2;

    public async Task<XcodeInstallationResult> InstallAsync(XcodeRelease release, XcodeInstallOptions options, XcodesPromptHandler? prompts,
        Action<XcodesOutputLine>? output, CancellationToken cancellationToken)
    {
        string directory = XcodesService.ValidateInstallDirectory(options.InstallDirectory) ?? InstalledXcodeScanner.DefaultDirectory;
        string archiveRoot = ResolveArchiveDirectory(options.ArchiveDirectory);
        XcodeCatalogRelease entry = await FindDownloadAsync(release, cancellationToken).ConfigureAwait(false);
        var target = new XcodeArchiveInstaller.Target(entry.Version, entry.Build, entry.Architecture);
        string destination = await _installer.FindDestinationAsync(directory, target, cancellationToken).ConfigureAwait(false);

        using DownloadedArchive archive = await DownloadArchiveAsync(entry, archiveRoot, prompts, output, forInstall: true, cancellationToken)
            .ConfigureAwait(false);
        XcodeInstallationResult installed = await _installer.InstallAsync(archive.Path, target,
            options, directory, destination, (step, text) => output?.Invoke(new XcodesOutputLine($"({step}/{StepCount}) {text}")),
            cancellationToken).ConfigureAwait(false);
        output?.Invoke(new XcodesOutputLine($"Xcode {entry.Version} has been installed to {installed.Path}"));
        return installed;
    }

    public async Task<string> DownloadAsync(XcodeRelease release, XcodesPromptHandler? prompts,
        Action<XcodesOutputLine>? output, CancellationToken cancellationToken, string? archiveDirectory = null)
    {
        string archiveRoot = ResolveArchiveDirectory(archiveDirectory);
        XcodeCatalogRelease entry = await FindDownloadAsync(release, cancellationToken).ConfigureAwait(false);
        using DownloadedArchive archive = await DownloadArchiveAsync(entry, archiveRoot, prompts, output, forInstall: false, cancellationToken)
            .ConfigureAwait(false);
        return archive.Path;
    }

    private string ResolveArchiveDirectory(string? archiveDirectory)
        => XcodesService.ValidateArchiveDirectory(archiveDirectory) ?? _archiveDirectory();

    private async Task<DownloadedArchive> DownloadArchiveAsync(XcodeCatalogRelease entry, string archiveRoot, XcodesPromptHandler? prompts,
        Action<XcodesOutputLine>? output, bool forInstall, CancellationToken cancellationToken)
    {
        void Write(string text, bool replaces = false) => output?.Invoke(new XcodesOutputLine(text, replaces));

        Write(prompts is null ? "Checking the Apple session" : "Signing in to Apple");
        AppleSession session = prompts is null
            ? await AuthAsync(() => _account.RefreshAsync(cancellationToken)).ConfigureAwait(false)
              ?? throw new XcodesException("The Apple session is no longer valid. Sign in before trying again.") { IsAccessFailure = true }
            : await AuthAsync(() => _account.EnsureSignedInAsync(
                new AppleSignInPromptAdapter(prompts), cancellationToken)).ConfigureAwait(false);
        Write($"Signed in as {session.FullName ?? _account.AppleId}");

        string downloading = $"(1/{(forInstall ? StepCount : 1)}) Downloading Xcode {entry.Version}";
        int lastPercent = -1;
        object gate = new();
        void Progress(double? fraction)
        {
            lock (gate)
            {
                if (fraction is not { } value)
                {
                    if (lastPercent == -1)
                        Write(downloading);
                    lastPercent = Math.Max(lastPercent, -2);
                    return;
                }
                int percent = (int)Math.Clamp(Math.Floor(value * 100), 0, 100);
                if (percent == lastPercent)
                    return;
                Write(string.Create(CultureInfo.InvariantCulture, $"{downloading}: {percent}%"), replaces: lastPercent >= 0);
                lastPercent = percent;
            }
        }

        XcodeArchiveDownloader.ArchiveLease? archiveLease = null;
        try
        {
            string archive = "";
            for (int attempt = 0; ; attempt++)
            {
                bool downloaded = false;
                try
                {
                    using AppleAccount.SessionLease? lease =
                        await AuthAsync(() => _account.AcquireSessionAsync(cancellationToken)).ConfigureAwait(false);
                    if (lease is null || !ReferenceEquals(lease.Session, session))
                        throw new XcodesException("The Apple account changed before the download started. Try again.") { IsAccessFailure = true };
                    try
                    {
                        if (Download is { } download)
                            archive = await download(entry.Download!, entry.Sha1, Progress, cancellationToken).ConfigureAwait(false);
                        else
                        {
                            archiveLease = await new XcodeArchiveDownloader(_account.Http, () => archiveRoot)
                                .DownloadForInstallAsync(entry.Download!, entry.Sha1, Progress, cancellationToken).ConfigureAwait(false);
                            archive = archiveLease.Path;
                        }
                        downloaded = true;
                        break;
                    }
                    catch (HttpRequestException ex)
                    {
                        throw new XcodesException(ex.StatusCode is { } status
                            ? $"The Xcode download failed with HTTP {(int)status}."
                            : $"Could not download Xcode: {ex.Message}", ex)
                        {
                            // Apple refuses downloads to a session it no longer accepts, or an account without access.
                            IsAccessFailure = ex.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden,
                        };
                    }
                    catch (IOException ex)
                    {
                        throw new XcodesException($"Could not download Xcode: {ex.Message}", ex);
                    }
                    finally
                    {
                        try
                        {
                            lease.SaveCookies();
                        }
                        catch (KeychainException ex) when (downloaded && ex.IsUserCanceledOrDenied)
                        {
                            Logger.Warn($"Xcode: keeping refreshed Apple cookies in memory only because Keychain access was declined: {ex.Message}");
                        }
                        catch (Exception ex) when (ex is KeychainException or IOException or UnauthorizedAccessException)
                        {
                            if (downloaded)
                                throw new XcodesException($"Could not save the Apple session: {ex.Message}", ex);
                            Logger.Warn($"Could not save the Apple session after the download stopped: {ex.Message}");
                        }
                    }
                }
                catch (XcodesException ex) when (ex.IsAdcSessionExpired && attempt == 0 && prompts is not null && Download is null)
                {
                    Logger.Info("Xcode: Apple's developer download session expired; signing in again to refresh cookies");
                    Write("Refreshing the Apple developer session");
                    session = await AuthAsync(() => _account.ReauthenticateAsync(
                        new AppleSignInPromptAdapter(prompts), cancellationToken)).ConfigureAwait(false);
                    Write($"Signed in as {session.FullName ?? _account.AppleId}");
                }
            }

            return new DownloadedArchive(archive, archiveLease);
        }
        catch
        {
            archiveLease?.Dispose();
            throw;
        }
    }

    private sealed class DownloadedArchive(string path, XcodeArchiveDownloader.ArchiveLease? lease) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose() => lease?.Dispose();
    }

    private async Task<XcodeCatalogRelease> FindDownloadAsync(XcodeRelease release, CancellationToken cancellationToken)
    {
        if (XcodesService.HasUnusableIdentity(release))
            throw new XcodesException(XcodesService.UnknownBuildMessage);
        IReadOnlyList<XcodeCatalogRelease> catalog = await SavedCatalogAsync(cancellationToken).ConfigureAwait(false)
            ?? await FetchCatalogAsync(cancellationToken).ConfigureAwait(false);
        XcodeCatalogRelease[] candidates = [.. catalog.Where(entry => entry.Download is not null
            && string.Equals(entry.Version, release.Version, StringComparison.OrdinalIgnoreCase)
            && Runs(entry.Architecture, _machine)
            && (release.Build is null || string.Equals(entry.Build, release.Build, StringComparison.Ordinal))
            && (release.Architecture == XcodeArchitecture.Unspecified || entry.Architecture == release.Architecture))];
        XcodeCatalogRelease? match = candidates.Where(entry => XcodeSimulatorImageFile.IsValidBuild(entry.Build))
            .MinBy(entry => ArchitecturePreference(entry.Architecture, _machine));
        if (match is null && candidates.Length > 0)
            throw new XcodesException(XcodesService.UnknownBuildMessage);
        return match ?? throw new XcodesException(
            $"Xcode {release.Version} is no longer offered for download. Check for updates, then try again.");
    }

    public async Task SelectAsync(string appPath, Action<XcodesOutputLine>? output, CancellationToken cancellationToken)
    {
        output?.Invoke(new XcodesOutputLine($"Selecting {appPath}"));
        await new XcodeSelector(_slow).SelectAsync(appPath, cancellationToken).ConfigureAwait(false);
        output?.Invoke(new XcodesOutputLine($"Selected {appPath}"));
    }

    public XcodeAccountStatus GetAccount()
    {
        try
        {
            _account.LoadSaved();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new XcodesException(ex.Message, ex);
        }
        return Status();
    }

    public async Task<XcodeAccountStatus> RefreshAccountAsync(CancellationToken cancellationToken)
    {
        await AuthAsync(() => _account.RefreshAsync(cancellationToken)).ConfigureAwait(false);
        return Status();
    }

    public async Task<XcodeAccountStatus> SignInAsync(XcodesPromptHandler prompts, string? suggestedAppleId, CancellationToken cancellationToken)
    {
        InvalidateAppleCatalog();
        try
        {
            await AuthAsync(() => _account.SignInAsync(new AppleSignInPromptAdapter(prompts), suggestedAppleId, cancellationToken)).ConfigureAwait(false);
            return Status();
        }
        finally
        {
            InvalidateAppleCatalog();
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        InvalidateAppleCatalog();
        try
        {
            await _account.SignOutAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new XcodesException($"Could not remove the saved Apple ID: {ex.Message}", ex);
        }
        finally
        {
            InvalidateAppleCatalog();
        }
    }

    private XcodeAccountStatus Status() => new(_account.AppleId, _account.Session, _account.HasSavedPassword, _account.HasStoredItem);

    private async Task<IReadOnlyList<AppleXcodeDownload>> AppleDownloadsAsync(bool refresh, CancellationToken cancellationToken)
    {
        long revision = Interlocked.Read(ref _accountRevision);

        await _appleCatalogLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using AppleAccount.SessionLease? lease =
                await AuthAsync(() => _account.AcquireSessionAsync(cancellationToken)).ConfigureAwait(false);
            if (lease is null || revision != Interlocked.Read(ref _accountRevision))
                return [];
            string identity = lease.AppleId;
            AppleCatalogSnapshot? snapshot = Volatile.Read(ref _appleCatalog);
            if (snapshot is not null && (snapshot.Revision != revision
                || !string.Equals(snapshot.AppleId, identity, StringComparison.Ordinal)))
            {
                Interlocked.CompareExchange(ref _appleCatalog, null, snapshot);
                snapshot = null;
            }
            if (!refresh)
                return snapshot?.Downloads ?? [];

            IReadOnlyList<AppleXcodeDownload> downloads;
            try
            {
                downloads = await (FetchAppleDownloads ?? new AppleDownloadsCatalog(_account.Http).GetAsync)(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException)
            {
                throw new XcodesException("Could not read Apple's download list. Check the connection and try again.", ex);
            }
            if (revision != Interlocked.Read(ref _accountRevision))
                return [];
            try
            {
                lease.SaveCookies();
            }
            catch (Exception ex) when (ex is KeychainException or IOException or UnauthorizedAccessException)
            {
                throw new XcodesException("Could not save the Apple session after reading its download list.", ex);
            }
            _appleCatalog = new AppleCatalogSnapshot(identity, revision, downloads);
            return downloads;
        }
        finally
        {
            _appleCatalogLock.Release();
        }
    }

    private void InvalidateAppleCatalog()
    {
        Interlocked.Increment(ref _accountRevision);
        Interlocked.Exchange(ref _appleCatalog, null);
    }

    private sealed record AppleCatalogSnapshot(string AppleId, long Revision, IReadOnlyList<AppleXcodeDownload> Downloads);

    private static async Task<T> AuthAsync<T>(Func<Task<T>> run)
    {
        try
        {
            return await run().ConfigureAwait(false);
        }
        catch (AppleAuthException ex)
        {
            throw new XcodesException(ex.Message, ex)
            {
                IsAccessFailure = ex.Error is AppleAuthError.InvalidCredentials or AppleAuthError.NotDeveloper
                    or AppleAuthError.PrivacyAcknowledgementRequired
                    || ex.StatusCode is 401 or 403,
            };
        }
        catch (HttpRequestException ex)
        {
            throw new XcodesException($"Could not reach Apple: {ex.Message}", ex);
        }
        catch (KeychainException ex) when (ex.IsUserCanceledOrDenied)
        {
            throw new XcodesException("Access to the login Keychain item was canceled or denied.", ex);
        }
        catch (Exception ex) when (ex is KeychainException or IOException or UnauthorizedAccessException)
        {
            throw new XcodesException($"Could not access the saved Apple account: {ex.Message}", ex);
        }
    }

    private async Task<IReadOnlyList<XcodeCatalogRelease>?> SavedCatalogAsync(CancellationToken cancellationToken)
    {
        if (_catalog is not IXcodeReleasesCatalogCache cache)
            return null;
        try
        {
            return (await cache.GetSavedAsync(cancellationToken).ConfigureAwait(false))?.Releases;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or XcodesException)
        {
            Logger.Warn($"Could not read the saved Xcode Releases list: {ex.Message}");
            return null;
        }
    }

    private async Task<IReadOnlyList<XcodeCatalogRelease>> FetchCatalogAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _catalog.GetAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new XcodesException("Xcode Releases did not answer in time.", ex);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException)
        {
            throw new XcodesException($"Could not download the list of Xcode versions: {ex.Message}", ex);
        }
    }
}
