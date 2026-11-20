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

/// <summary>Projections for Agent inventory.</summary>
public static class IpcRemoteInventory
{
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
}
