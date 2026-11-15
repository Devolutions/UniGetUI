using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Interface;

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

/// <summary>A copy of the last completed scans served to the Agent.</summary>
public sealed record RemoteInventorySnapshot(
    DateTime CompletedUtc,
    DateTime? UpdateCompletedUtc,
    IReadOnlyList<IpcManagerInfo> Managers,
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
    public static IReadOnlyList<IpcRemoteManagerInfo> ListManagers() =>
        PEInterface.Managers
            .OrderBy(manager => manager.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(ToRemoteManagerInfo)
            .ToArray();

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
        IReadOnlyList<IpcManagerInfo> managerInfo
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

        var observed = installedPackages.Concat(updatePackages).Select(package => package.Source);
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
            .Where(update => update.Manager == row.Manager && update.Id == row.Id)
            .ToArray();
        var sameScope = samePackage
            .Where(update => (update.ReportedScope ?? "") == (row.ReportedScope ?? ""))
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
        bool loneInstallation =
            installed.Count(other => other.Manager == row.Manager && other.Id == row.Id) == 1;
        var anyScope = samePackage
            .Where(update => update.ReportedScope is null)
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
}
