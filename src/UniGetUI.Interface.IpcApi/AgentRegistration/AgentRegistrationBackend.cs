using Devolutions.Now.Policy.Api;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.AgentBroker;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Interface.AgentRegistration;

/// <summary>
/// The local state remote registration requests may use. Reads only copy finished state, and the
/// only way to act is <see cref="SubmitGrantAsync"/>, which hands an Agent-authorized request to
/// the Broker; nothing here executes a package operation locally.
/// </summary>
internal interface IAgentRegistrationBackend
{
    IReadOnlyList<IpcManagerInfo> ListManagers();

    /// <summary>The last completed scans, or null when inventory is unavailable.</summary>
    RemoteInventorySnapshot? TryGetInventorySnapshot();

    /// <summary>Whether operations may be routed to the Broker at all.</summary>
    bool RemoteOperationsEnabled { get; }

    /// <summary>The public id of the UniGetUI manager the Broker knows by this name, or null.</summary>
    string? FindManagerId(ManagerName manager);

    RemoteSourceResolution ResolveSource(string managerId, string sourceName);

    /// <summary>
    /// Whether the current installed list is complete and holds exactly one installation with this
    /// manager and package ID, and that installation reports exactly this scope.
    /// </summary>
    bool IsOnlyInstalledRow(string managerId, string packageId, string reportedScope);

    Task<RemoteGrantDelivery> SubmitGrantAsync(
        RemoteGrantPlan plan,
        string grant,
        CancellationToken cancellationToken
    );
}

internal sealed class IpcRemoteInventoryBackend : IAgentRegistrationBackend
{
    private readonly IRemoteGrantSubmitter _submitter;

    public IpcRemoteInventoryBackend(IRemoteGrantSubmitter? submitter = null)
    {
        _submitter = submitter ?? new BrokerRemoteGrantSubmitter();
    }

    public IReadOnlyList<IpcManagerInfo> ListManagers() => IpcRemoteInventory.ListManagers();

    public RemoteInventorySnapshot? TryGetInventorySnapshot() =>
        IpcRemoteInventory.TryGetInventorySnapshot();

    public bool RemoteOperationsEnabled => Settings.Get(Settings.K.UseAgentBroker);

    public string? FindManagerId(ManagerName manager) =>
        PEInterface.Managers
            .Where(candidate => RemoteGrantRequestBuilder.IsManager(candidate.Name, manager))
            .Select(IpcManagerSettingsApi.GetPublicManagerId)
            .SingleOrDefault();

    public RemoteSourceResolution ResolveSource(string managerId, string sourceName) =>
        IpcRemoteInventory.ResolveSource(managerId, sourceName);

    public bool IsOnlyInstalledRow(string managerId, string packageId, string reportedScope)
    {
        var loader = InstalledPackagesLoader.Instance;
        if (loader is null || !loader.IsLoaded || loader.IsLoading)
        {
            return false;
        }

        var packages = loader.Packages;
        // A reload that started while copying clears the list first, so the copy may be partial.
        if (!loader.IsLoaded || loader.IsLoading)
        {
            return false;
        }

        return IpcRemoteInventory.IsOnlyInstalledRow(packages, managerId, packageId, reportedScope);
    }

    public Task<RemoteGrantDelivery> SubmitGrantAsync(
        RemoteGrantPlan plan,
        string grant,
        CancellationToken cancellationToken
    ) => _submitter.SubmitAsync(plan, grant, cancellationToken);
}
