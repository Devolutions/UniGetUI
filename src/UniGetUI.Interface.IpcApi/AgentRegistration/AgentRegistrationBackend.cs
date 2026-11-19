namespace UniGetUI.Interface.AgentRegistration;

/// <summary>
/// The read-only view of local state that remote registration requests may use.
/// There is deliberately no execution member: remote operations never start anything.
/// </summary>
internal interface IAgentRegistrationBackend
{
    IReadOnlyList<IpcRemoteManagerInfo> ListManagers();
}

internal sealed class IpcRemoteInventoryBackend : IAgentRegistrationBackend
{
    public IReadOnlyList<IpcRemoteManagerInfo> ListManagers() => IpcRemoteInventory.ListManagers();
}
