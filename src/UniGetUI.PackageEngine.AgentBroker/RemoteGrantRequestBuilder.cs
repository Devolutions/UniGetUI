using Devolutions.Now.Policy.Api;
using Devolutions.Now.Policy.Client;
using UniGetUI.Core.Data;

namespace UniGetUI.PackageEngine.AgentBroker;

/// <summary>What an Agent-authorized operation asks for, already checked for shape.</summary>
public sealed record RemoteGrantOperation(
    string BrokerRequestId,
    Operation Operation,
    ManagerName Manager,
    string SourceName,
    string PackageId,
    string Version,
    Scope RequestedScope,
    string? ReportedScope
);

/// <summary>The Broker request for a remote operation, and what must be checked before sending it.</summary>
public sealed record RemoteGrantPlan(
    PackageOperationRequest Request,
    Elevation Elevation,
    bool RequiresUniqueInstalledRow
);

/// <summary>
/// Builds the exact Broker request bound by an Agent-issued grant. It carries only what the grant
/// binds: no architecture, channel, location, custom parameters, hooks or kill list, and every
/// behavioural flag off except <c>NoUpgrade</c> for a WinGet install.
/// </summary>
public static class RemoteGrantRequestBuilder
{
    /// <summary>
    /// Maps a manager named by the Agent: a Broker manager spelling ignoring ASCII case, or one of
    /// the inventory ids the Agent also accepts. Chocolatey is refused, as by the Agent.
    /// </summary>
    public static bool TryParseManager(string value, out ManagerName manager)
    {
        ManagerName? mapped = value.ToLowerInvariant() switch
        {
            "winget" => ManagerName.Winget,
            "powershell" or "winps" => ManagerName.PowerShell,
            "powershell7" or "pwsh" => ManagerName.PowerShell7,
            "dotnet" or "dotnet-tool" => ManagerName.Dotnet,
            "npm" => ManagerName.Npm,
            "pip" => ManagerName.Pip,
            "scoop" => ManagerName.Scoop,
            "cargo" => ManagerName.Cargo,
            "bun" => ManagerName.Bun,
            "vcpkg" => ManagerName.Vcpkg,
            _ => null,
        };

        manager = mapped ?? default;
        return mapped is not null && value.All(char.IsAscii);
    }

    /// <summary>Whether a UniGetUI manager is the one the Broker knows by this name.</summary>
    public static bool IsManager(string uniGetUIManagerName, ManagerName manager) =>
        BrokerRequestBuilder.TryMapManagerName(uniGetUIManagerName, out var mapped) && mapped == manager;

    /// <summary>Null when the scope rules of the contract forbid this operation.</summary>
    public static RemoteGrantPlan? Plan(RemoteGrantOperation operation)
    {
        bool install = operation.Operation is Operation.Install;
        if (install != operation.ReportedScope is null)
        {
            return null;
        }

        Scope? scope;
        bool uniqueRow;
        if (operation.Manager is ManagerName.Winget)
        {
            scope = operation.RequestedScope;
            uniqueRow = false;
        }
        else if (operation.RequestedScope is not Scope.User)
        {
            return null;
        }
        else if (install)
        {
            scope = Scope.User;
            uniqueRow = false;
        }
        else if (
            (operation.Operation is Operation.Update && operation.Manager is ManagerName.PowerShell or ManagerName.PowerShell7)
            || (operation.Operation is Operation.Uninstall && operation.Manager is ManagerName.PowerShell7)
        )
        {
            string reported = operation.ReportedScope!;
            if (reported.Length == 0)
            {
                scope = null;
                uniqueRow = true;
            }
            else if (reported.Equals("user", StringComparison.OrdinalIgnoreCase))
            {
                scope = Scope.User;
                uniqueRow = false;
            }
            else if (reported.Equals("machine", StringComparison.OrdinalIgnoreCase))
            {
                scope = Scope.Machine;
                uniqueRow = false;
            }
            else
            {
                return null;
            }
        }
        else
        {
            scope = Scope.User;
            uniqueRow = true;
        }

        bool elevated = operation.Manager is ManagerName.Winget && operation.RequestedScope is Scope.Machine;
        var request = new PackageOperationRequest
        {
            RequestId = operation.BrokerRequestId,
            CreatedAt = DateTimeOffset.UtcNow,
            Operation = operation.Operation,
            Manager = operation.Manager,
            Source = new RequestSource { Name = operation.SourceName, Url = null },
            Package = new RequestPackage
            {
                Id = operation.PackageId,
                Version = operation.Version.Length == 0 ? null : operation.Version,
                Architecture = null,
                Channel = null,
            },
            Options = new RequestOptions
            {
                Scope = scope,
                Interactive = false,
                SkipHashCheck = false,
                PreRelease = false,
                CustomInstallLocation = null,
                CustomParameters = [],
                PreOperationCommand = null,
                PostOperationCommand = null,
                KillBeforeOperation = [],
                UninstallPrevious = false,
                NoUpgrade = install && operation.Manager is ManagerName.Winget,
            },
            IncludeCommandPreview = false,
            CaptureOutput = true,
        };

        return new RemoteGrantPlan(request, elevated ? Elevation.Elevated : Elevation.Standard, uniqueRow);
    }
}

/// <summary>Whether a grant could have reached the Broker.</summary>
public enum RemoteGrantDelivery
{
    /// <summary>The client established that nothing was sent or started.</summary>
    NotSent,

    /// <summary>The grant may have been delivered, so the outcome is unknown.</summary>
    MaybeSent,
}

/// <summary>Submits a remote operation once, with its grant, and never anything else.</summary>
public interface IRemoteGrantSubmitter
{
    Task<RemoteGrantDelivery> SubmitAsync(RemoteGrantPlan plan, string grant, CancellationToken cancellationToken);
}

/// <summary>
/// Sends the request to the Broker's grant route after authenticating the pipe server as the
/// Agent service. It never retries, never uses the legacy route, never reads the operation's
/// output and never logs: the grant, the response and any exception text stay here.
/// </summary>
public sealed class BrokerRemoteGrantSubmitter : IRemoteGrantSubmitter
{
    private readonly Func<IBrokerTransport>? _transport;

    public BrokerRemoteGrantSubmitter() { }

    /// <summary>For tests: replaces the named-pipe transport, keeping everything else.</summary>
    internal BrokerRemoteGrantSubmitter(Func<IBrokerTransport> transport)
    {
        _transport = transport;
    }

    public async Task<RemoteGrantDelivery> SubmitAsync(
        RemoteGrantPlan plan,
        string grant,
        CancellationToken cancellationToken
    )
    {
        BrokerClient client;
        try
        {
            client = new BrokerClient(
                new BrokerClientOptions
                {
                    Transport = _transport?.Invoke(),
                    RequestedElevation = plan.Elevation,
                    EffectiveUser = BrokerClientFactory.GetEffectiveUser(),
                    ClientExecutablePath = Environment.ProcessPath,
                    ClientVersion = CoreData.VersionName,
                    ServerAuthenticator = new ServiceBrokerServerAuthenticator(),
                }
            );
        }
        catch (Exception)
        {
            return RemoteGrantDelivery.NotSent;
        }

        try
        {
            using (client)
            {
                // A response is not evidence of the outcome, and the Broker opens no event channel.
                await client.ExecuteGrant(plan.Request, grant, cancellationToken).ConfigureAwait(false);
            }

            return RemoteGrantDelivery.MaybeSent;
        }
        catch (BrokerClientException ex)
        {
            return ClassifyFailure(ex.NoWorkStarted, ex.Kind);
        }
        catch (Exception)
        {
            return RemoteGrantDelivery.MaybeSent;
        }
    }

    /// <summary>
    /// Only failures raised before the grant left this process count as not sent. A Broker
    /// rejection proves only that this request started nothing, not that the grant is unspent.
    /// </summary>
    internal static RemoteGrantDelivery ClassifyFailure(bool noWorkStarted, BrokerClientErrorKind kind) =>
        noWorkStarted
        && kind
            is not (
                BrokerClientErrorKind.BrokerError
                or BrokerClientErrorKind.PolicyDenied
                or BrokerClientErrorKind.UnsupportedEndpoint
            )
            ? RemoteGrantDelivery.NotSent
            : RemoteGrantDelivery.MaybeSent;
}
