namespace UniGetUI.Interface.AgentRegistration;

/// <summary>
/// The read-only view of local state that remote registration requests may use.
/// There is deliberately no execution member: remote operations never start anything.
/// </summary>
internal interface IAgentRegistrationBackend
{
    bool TryListInstalledPackages(out IReadOnlyList<IpcPackageInfo> packages);
    bool TryListUpgradablePackages(out IReadOnlyList<IpcPackageInfo> packages);
    IReadOnlyList<IpcManagerInfo> ListManagers();
    IReadOnlyList<IpcSourceInfo> ListSources();
    RemoteSourceResolution ResolveSource(
        string manager,
        string sourceName,
        bool requireInstalledOrigin = false
    );
}

internal sealed class IpcRemoteInventoryBackend : IAgentRegistrationBackend
{
    public bool TryListInstalledPackages(out IReadOnlyList<IpcPackageInfo> packages) =>
        IpcRemoteInventory.TryListInstalledPackages(out packages);

    public bool TryListUpgradablePackages(out IReadOnlyList<IpcPackageInfo> packages) =>
        IpcRemoteInventory.TryListUpgradablePackages(out packages);

    public IReadOnlyList<IpcManagerInfo> ListManagers() => IpcRemoteInventory.ListManagers();

    public IReadOnlyList<IpcSourceInfo> ListSources() => IpcRemoteInventory.ListSources();

    public RemoteSourceResolution ResolveSource(
        string manager,
        string sourceName,
        bool requireInstalledOrigin = false
    ) => IpcRemoteInventory.ResolveSource(manager, sourceName, requireInstalledOrigin);
}
