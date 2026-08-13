using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.RemoteHosts;
using UniGetUI.PackageOperations;

namespace UniGetUI.PackageEngine.Operations;

public sealed class RemotePackageOperation : AbstractOperation
{
    public IPackage Package { get; }
    public RemoteHost Host { get; }
    public OperationType Role { get; }

    private readonly RemoteSshClient _client;

    public RemotePackageOperation(
        IPackage package,
        RemoteHost host,
        OperationType role,
        RemoteSshClient? client = null
    )
        : base(queue_enabled: true)
    {
        Package = package;
        Host = host;
        Role = role;
        _client = client ?? new RemoteSshClient();

        string hostName = host.DisplayName;
        string verb = role switch
        {
            OperationType.Update => CoreTools.Translate("Update"),
            OperationType.Uninstall => CoreTools.Translate("Uninstall"),
            OperationType.Install => CoreTools.Translate("Install"),
            _ => CoreTools.Translate("Operation"),
        };

        Metadata.Title = $"{verb} {package.Name} ({hostName})";
        Metadata.Status = CoreTools.Translate("{0} on {1}", package.Name, hostName);
        Metadata.SuccessTitle = CoreTools.Translate("{0} succeeded", verb);
        Metadata.SuccessMessage = CoreTools.Translate(
            "{package} completed on {host}",
            new Dictionary<string, object?>
            {
                { "package", package.Name },
                { "host", hostName },
            }
        );
        Metadata.FailureTitle = CoreTools.Translate("{0} failed", verb);
        Metadata.FailureMessage = CoreTools.Translate(
            "{package} could not be processed on {host}",
            new Dictionary<string, object?>
            {
                { "package", package.Name },
                { "host", hostName },
            }
        );
        Metadata.OperationInformation =
            $"Remote {role} for {package.Id} via {package.Manager.Id} on {host.Destination}";
    }

    public override Task<Uri> GetOperationIcon() => Task.FromResult(Package.GetIconUrl());

    protected override void ApplyRetryAction(string retryMode) { }

    protected override async Task<OperationVeredict> PerformOperation()
    {
        Package.SetTag(PackageTag.BeingProcessed);
        Line(
            CoreTools.Translate("Connecting to {0} over SSH…", Host.Destination),
            LineType.Information
        );

        try
        {
            RemoteControlResponse response = Role switch
            {
                OperationType.Update => await _client.UpdateAsync(
                    Host,
                    Package.Manager.Id,
                    Package.Id,
                    line => Line(line, LineType.Information),
                    CancellationToken
                ),
                OperationType.Uninstall => await _client.UninstallAsync(
                    Host,
                    Package.Manager.Id,
                    Package.Id,
                    line => Line(line, LineType.Information),
                    CancellationToken
                ),
                _ => throw new InvalidOperationException($"Remote {Role} is not supported."),
            };

            if (RemoteHostService.Instance.TryGetHost(Host.Id, out RemoteHost liveHost))
                RemoteHostService.Instance.GetSession(liveHost).Apply(response);

            Package.SetTag(PackageTag.Default);
            return response.Ok ? OperationVeredict.Success : OperationVeredict.Failure;
        }
        catch (OperationCanceledException)
        {
            Package.SetTag(PackageTag.Default);
            return OperationVeredict.Canceled;
        }
        catch (Exception ex)
        {
            Package.SetTag(PackageTag.Failed);
            Line(ex.Message, LineType.Error);
            return OperationVeredict.Failure;
        }
    }
}
