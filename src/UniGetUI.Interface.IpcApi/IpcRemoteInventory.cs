using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations.Reboot;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Interface;

/// <summary>The outcome of resolving a remote request's canonical source name.</summary>
public enum RemoteSourceResolution
{
    Resolved,
    UnknownManager,
    MissingSourceName,
    NotFound,
    Ambiguous,
    /// <summary>The manager cannot report which feed an installed package came from.</summary>
    OriginUnknown,
}

/// <summary>
/// A package manager as sent to the Agent. Every member is listed here, so a field added to the
/// local manager API is not sent remotely until it is added to this type. Executable paths are
/// local paths and health reasons can quote captured output, so both are always blank.
/// </summary>
public sealed class IpcRemoteManagerInfo
{
    public string Name { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public bool Enabled { get; init; }
    public bool Ready { get; init; }
    public bool NotificationsSuppressed { get; init; }
    public string ExecutablePath => "";
    public string ExecutableArguments => "";
    public IpcRemoteManagerCapabilitiesInfo Capabilities { get; init; } = new();
    public IpcRemoteManagerHealthInfo Health { get; } = new();
}

public sealed class IpcRemoteManagerCapabilitiesInfo
{
    public bool CanRunAsAdmin { get; init; }
    public bool CanSkipIntegrityChecks { get; init; }
    public bool CanRunInteractively { get; init; }
    public bool CanRemoveDataOnUninstall { get; init; }
    public bool CanDownloadInstaller { get; init; }
    public bool SupportsCustomVersions { get; init; }
    public bool SupportsCustomArchitectures { get; init; }
    public bool SupportsCustomScopes { get; init; }
    public bool SupportsPreRelease { get; init; }
    public bool SupportsCustomLocations { get; init; }
    public bool SupportsCustomSources { get; init; }
    public bool MustInstallSourcesAsAdmin { get; init; }
}

/// <summary>Manager health is never sent remotely; every member is the unknown default.</summary>
public sealed class IpcRemoteManagerHealthInfo
{
    public string State => "unknown";
    public bool NeedsAttention => false;
    public string? FailingListing => null;
    public string? Reason => null;
    public int ConsecutiveFailures => 0;
    public IpcRemoteManagerListingHealthInfo Search { get; } = new();
    public IpcRemoteManagerListingHealthInfo Installed { get; } = new();
    public IpcRemoteManagerListingHealthInfo Updates { get; } = new();
}

public sealed class IpcRemoteManagerListingHealthInfo
{
    public DateTime? LastSuccessUtc => null;
    public DateTime? LastFailureUtc => null;
    public string? LastFailureReason => null;
    public bool LastFailureWasTimeout => false;
    public int ConsecutiveFailures => 0;
    public DateTime? FailureStreakStartUtc => null;
    public int? LastPackageCount => null;
    public DateTime? LastFastRepairUtc => null;
    public string? LastFastRepairReason => null;
    public IReadOnlyList<long> RecentDurationsMs => [];
}

/// <summary>A copy of the last completed scans served to the Agent.</summary>
public sealed record RemoteInventorySnapshot(
    DateTime CompletedUtc,
    DateTime? UpdateCompletedUtc,
    IReadOnlyList<IpcRemoteManagerInfo> Managers,
    IReadOnlyList<IpcSourceInfo> Sources,
    IReadOnlyList<IpcPackageInfo> InstalledPackages,
    IReadOnlyList<IpcPackageInfo> Updates
);

/// <summary>
/// Projections of UniGetUI's package state for the Devolutions Agent. They read finished scans
/// only and never send local paths, credentials or unproven source names.
/// </summary>
public static class IpcRemoteInventory
{
    private static readonly string[] SensitiveSourceMarkers =
    [
        "password", "passwd", "token", "secret", "apikey", "api_key", "api-key",
        "credential", "bearer", "authorization",
    ];

    /// <summary>
    /// The installed packages already loaded locally, or <c>false</c> while that list is not
    /// complete (never loaded, or being reloaded), which the caller reports as busy.
    /// </summary>
    public static bool TryListInstalledPackages(out IReadOnlyList<IpcPackageInfo> packages) =>
        TryProject(InstalledPackagesLoader.Instance, installed: true, out packages);

    /// <summary>The available updates already loaded locally; see <see cref="TryListInstalledPackages"/>.</summary>
    public static bool TryListUpgradablePackages(out IReadOnlyList<IpcPackageInfo> packages) =>
        TryProject(UpgradablePackagesLoader.Instance, installed: false, out packages);

    public static IReadOnlyList<IpcRemoteManagerInfo> ListManagers() =>
        PEInterface.Managers
            .OrderBy(manager => manager.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(ToRemoteManagerInfo)
            .ToArray();

    private static IpcRemoteManagerInfo ToRemoteManagerInfo(IPackageManager manager)
    {
        // Built from the local projection without health, which would read the health store.
        var local = IpcManagerSettingsApi.ToManagerInfo(manager, includeHealth: false);
        var capabilities = local.Capabilities;
        return new IpcRemoteManagerInfo
        {
            Name = local.Name,
            DisplayName = local.DisplayName,
            Enabled = local.Enabled,
            Ready = local.Ready,
            NotificationsSuppressed = local.NotificationsSuppressed,
            Capabilities = new IpcRemoteManagerCapabilitiesInfo
            {
                CanRunAsAdmin = capabilities.CanRunAsAdmin,
                CanSkipIntegrityChecks = capabilities.CanSkipIntegrityChecks,
                CanRunInteractively = capabilities.CanRunInteractively,
                CanRemoveDataOnUninstall = capabilities.CanRemoveDataOnUninstall,
                CanDownloadInstaller = capabilities.CanDownloadInstaller,
                SupportsCustomVersions = capabilities.SupportsCustomVersions,
                SupportsCustomArchitectures = capabilities.SupportsCustomArchitectures,
                SupportsCustomScopes = capabilities.SupportsCustomScopes,
                SupportsPreRelease = capabilities.SupportsPreRelease,
                SupportsCustomLocations = capabilities.SupportsCustomLocations,
                SupportsCustomSources = capabilities.SupportsCustomSources,
                MustInstallSourcesAsAdmin = capabilities.MustInstallSourcesAsAdmin,
            },
        };
    }

    /// <summary>
    /// A copy of the last completed installed scan, or null when it is not complete for every
    /// enabled and installed manager. It reads only finished scan copies: it never starts or
    /// joins a load, and never reads the pending-reboot or manager-health stores.
    /// </summary>
    public static RemoteInventorySnapshot? TryGetInventorySnapshot() =>
        BuildSnapshot(
            PEInterface.Managers,
            InstalledPackagesLoader.Instance?.LastCompletedScan,
            UpgradablePackagesLoader.Instance?.LastCompletedScan,
            ListManagers()
        );

    internal static RemoteInventorySnapshot? BuildSnapshot(
        IEnumerable<IPackageManager> managers,
        CompletedPackageScan? installedScan,
        CompletedPackageScan? updateScan,
        IReadOnlyList<IpcRemoteManagerInfo> managerInfo
    )
    {
        var all = managers.ToArray();
        var expected = all.Where(manager => manager.IsEnabled() && manager.Status.Found).ToHashSet();
        if (
            installedScan is null
            || expected.Any(manager => !manager.IsReady() || !installedScan.SucceededManagers.Contains(manager))
        )
        {
            return null;
        }

        bool updatesComplete =
            updateScan is not null && expected.All(updateScan.SucceededManagers.Contains);

        var installedPackages = installedScan.Packages.Where(package => expected.Contains(package.Manager)).ToArray();
        var updatePackages = updatesComplete
            ? updateScan!.Packages.Where(package => expected.Contains(package.Manager)).ToArray()
            : [];

        // Nothing here may read PendingRebootStore, so pass an empty set; the field is not sent.
        var noPendingKeys = new HashSet<string>(StringComparer.Ordinal);
        var installed = ProjectPackages(installedPackages, installed: true, noPendingKeys);
        var updates = ProjectPackages(updatePackages, installed: false, noPendingKeys);
        foreach (var row in installed)
        {
            // The live package can be linked to a newer update list; only the copied scan counts.
            row.NewVersion = AvailableVersion(row, installed, updates);
            row.IsUpgradable = row.NewVersion.Length != 0;
        }

        var observed = installedScan.Packages
            .Concat(updateScan?.Packages ?? [])
            .Select(package => package.Source);
        return new RemoteInventorySnapshot(
            installedScan.CompletedUtc,
            updatesComplete ? updateScan!.CompletedUtc : null,
            managerInfo,
            BuildSources(all, observed),
            installed,
            updates
        );
    }

    /// <summary>
    /// Whether exactly one installation has this manager and package ID, and it reports exactly
    /// this scope (an empty string standing for no reported scope).
    /// </summary>
    internal static bool IsOnlyInstalledRow(
        IEnumerable<IPackage> installed,
        string managerId,
        string packageId,
        string reportedScope
    )
    {
        var matches = installed
            .Where(package =>
                string.Equals(IpcManagerSettingsApi.GetPublicManagerId(package.Manager), managerId, StringComparison.Ordinal)
                && string.Equals(package.Id, packageId, StringComparison.Ordinal))
            .DistinctBy(IpcPackageApi.GetInstalledPackageIdentity)
            .ToArray();
        return matches.Length == 1
            && string.Equals(
                string.IsNullOrWhiteSpace(matches[0].ReportedScope) ? "" : matches[0].ReportedScope,
                reportedScope,
                StringComparison.Ordinal);
    }

    private static string AvailableVersion(
        IpcPackageInfo row,
        IReadOnlyList<IpcPackageInfo> installed,
        IReadOnlyList<IpcPackageInfo> updates
    )
    {
        var samePackage = updates
            .Where(update => update.Manager == row.Manager && update.Id == row.Id
                // An update listed for another version is stale: the row was upgraded since.
                && update.Version == row.Version)
            .ToArray();
        bool loneInstallation =
            installed.Count(other => other.Manager == row.Manager && other.Id == row.Id) == 1;
        var sameScope = samePackage
            .Where(update => !string.IsNullOrEmpty(update.ReportedScope)
                && update.ReportedScope == row.ReportedScope)
            .Select(update => update.NewVersion)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (sameScope.Length == 1)
        {
            return sameScope[0];
        }

        if (sameScope.Length > 1)
        {
            return "";
        }

        // An update listing may report no scope; attribute it only to a lone installation.
        var anyScope = samePackage
            .Where(update => string.IsNullOrEmpty(update.ReportedScope))
            .Select(update => update.NewVersion)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return loneInstallation && anyScope.Length == 1 ? anyScope[0] : "";
    }

    /// <summary>
    /// The manager's known sources plus the sources of its loaded packages. Configured sources
    /// are not queried, because that runs the manager's own command and logs its output.
    /// </summary>
    public static IReadOnlyList<IpcSourceInfo> ListSources()
    {
        var observed = SnapshotPackages(InstalledPackagesLoader.Instance)
            .Concat(SnapshotPackages(UpgradablePackagesLoader.Instance))
            .Select(package => package.Source);
        return BuildSources(PEInterface.Managers, observed);
    }

    /// <summary>
    /// Resolves a canonical source name by ordinal comparison against the manager's known sources
    /// and the sources observed in the last completed scans, the copies the Agent chose from, so
    /// a load running meanwhile does not hide them.
    /// </summary>
    public static RemoteSourceResolution ResolveSource(
        string manager,
        string sourceName,
        bool requireInstalledOrigin = false
    )
    {
        return ResolveSource(PEInterface.Managers, ScannedSources(), manager, sourceName, requireInstalledOrigin);
    }

    internal static IEnumerable<IManagerSource> ScannedSources() =>
        (InstalledPackagesLoader.Instance?.LastCompletedScan?.Packages ?? [])
            .Concat(UpgradablePackagesLoader.Instance?.LastCompletedScan?.Packages ?? [])
            .Select(package => package.Source);

    /// <param name="requireInstalledOrigin">
    /// True for update and uninstall, which require package-specific origin evidence not present
    /// in this source-only lookup.
    /// </param>
    internal static RemoteSourceResolution ResolveSource(
        IEnumerable<IPackageManager> managers,
        IEnumerable<IManagerSource> observedSources,
        string manager,
        string sourceName,
        bool requireInstalledOrigin = false
    )
    {
        if (string.IsNullOrEmpty(sourceName))
        {
            return RemoteSourceResolution.MissingSourceName;
        }

        var resolvedManager = managers.FirstOrDefault(candidate =>
            !string.IsNullOrWhiteSpace(manager)
            && candidate.Id.Equals(manager, StringComparison.OrdinalIgnoreCase));
        if (resolvedManager is null)
        {
            return RemoteSourceResolution.UnknownManager;
        }

        if (requireInstalledOrigin)
        {
            return RemoteSourceResolution.OriginUnknown;
        }

        var matches = SourcesOf(resolvedManager, observedSources)
            .Where(source => string.Equals(source.Name, sourceName, StringComparison.Ordinal))
            .Select(source => source.Url.ToString())
            .Distinct(StringComparer.Ordinal)
            .Count();
        return matches switch
        {
            0 => RemoteSourceResolution.NotFound,
            1 => SafeText(sourceName).Length != 0 ? RemoteSourceResolution.Resolved : RemoteSourceResolution.NotFound,
            _ => RemoteSourceResolution.Ambiguous,
        };
    }

    internal static IReadOnlyList<IpcSourceInfo> BuildSources(
        IEnumerable<IPackageManager> managers,
        IEnumerable<IManagerSource> observedSources
    )
    {
        var observed = observedSources.ToArray();
        List<IpcSourceInfo> result = [];
        foreach (var manager in managers)
        {
            var sources = SourcesOf(manager, observed).ToArray();
            var ambiguous = AmbiguousNames(sources);
            var known = manager.Properties.KnownSources.Select(SourceKey).ToHashSet(StringComparer.Ordinal);
            foreach (var source in sources.DistinctBy(SourceKey, StringComparer.Ordinal))
            {
                var name = SafeSourceName(source, ambiguous);
                if (name.Length == 0)
                    continue;

                result.Add(new IpcSourceInfo
                {
                    Manager = IpcManagerSettingsApi.GetPublicManagerId(manager),
                    Name = name,
                    DisplayName = SafeText(source.AsString_DisplayName),
                    Url = "",
                    IsKnown = known.Contains(SourceKey(source)),
                    IsConfigured = !known.Contains(SourceKey(source)),
                });
            }
        }

        return result
            .OrderBy(source => source.Manager, StringComparer.OrdinalIgnoreCase)
            .ThenBy(source => source.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static IReadOnlyList<IpcPackageInfo> ProjectPackages(
        IEnumerable<IPackage> packages,
        bool installed,
        IReadOnlySet<string>? pendingRestartKeys
    )
    {
        var list = packages.ToArray();
        IEnumerable<IPackage> distinct = installed
            ? list.DistinctBy(IpcPackageApi.GetInstalledPackageIdentity)
            : list.DistinctBy(IpcPackageApi.GetPackageIdentity);
        return distinct
            .Select(package =>
            {
                var info = IpcPackageApi.ToIpcPackageInfo(package, pendingRestartKeys);
                info.Source = SafeText(info.Source);
                return info;
            })
            .OrderBy(package => package.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(package => package.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool TryProject(
        AbstractPackageLoader? loader,
        bool installed,
        out IReadOnlyList<IpcPackageInfo> packages
    )
    {
        packages = [];
        if (loader is null || !loader.IsLoaded || loader.IsLoading)
        {
            return false;
        }

        var snapshot = loader.Packages;
        // A reload that started while copying clears the list first, so the copy may be partial.
        if (!loader.IsLoaded || loader.IsLoading)
        {
            return false;
        }

        packages = ProjectPackages(snapshot, installed, PendingRebootStore.GetPendingKeys());
        return true;
    }

    private static IReadOnlyList<IPackage> SnapshotPackages(AbstractPackageLoader? loader) =>
        loader is null || !loader.IsLoaded || loader.IsLoading ? [] : loader.Packages;

    private static IEnumerable<IManagerSource> SourcesOf(
        IPackageManager manager,
        IEnumerable<IManagerSource> observedSources
    ) => manager.Properties.KnownSources.Concat(observedSources.Where(source => source.Manager == manager));

    private static HashSet<string> AmbiguousNames(IReadOnlyList<IManagerSource> sources) =>
        sources
            .GroupBy(source => source.Name, StringComparer.Ordinal)
            .Where(group => group.Select(source => source.Url.ToString()).Distinct(StringComparer.Ordinal).Skip(1).Any())
            .Select(group => group.Key)
            .ToHashSet(StringComparer.Ordinal);

    private static string SafeSourceName(IManagerSource source, IReadOnlySet<string> ambiguous) =>
        ambiguous.Contains(source.Name) ? "" : SafeText(source.Name);

    private static string SafeText(string text) =>
        text.Length != 0
        && text.All(character => char.IsLetterOrDigit(character) || character is ' ' or '-' or '_' or '.')
        && !SensitiveSourceMarkers.Any(marker => text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            ? text
            : "";

    private static string SourceKey(IManagerSource source) => $"{source.Name}\n{source.Url}";
}
