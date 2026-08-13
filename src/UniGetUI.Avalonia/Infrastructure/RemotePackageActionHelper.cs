using UniGetUI.Interface.Telemetry;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.RemoteHosts;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class RemotePackageActionHelper
{
    public static async Task LaunchAsync(
        IEnumerable<IPackage> packages,
        OperationType role,
        bool? elevated = null,
        bool? interactive = null,
        bool? no_integrity = null,
        bool? remove_data = null
    )
    {
        foreach (IPackage pkg in packages)
        {
            if (pkg.RemoteHostId is Guid hostId
                && RemoteHostService.Instance.TryGetHost(hostId, out RemoteHost host))
            {
                if (role is OperationType.Install)
                    continue;
                if (!RemoteHostService.Instance.CanMutate(pkg))
                    continue;

                var remoteOp = new RemotePackageOperation(pkg, host, role);
                AttachTelemetry(remoteOp, pkg, role);
                AvaloniaOperationRegistry.Add(remoteOp);
                _ = remoteOp.MainThread();
                continue;
            }

            InstallOptions opts = await InstallOptionsFactory.LoadApplicableAsync(
                pkg, elevated: elevated, interactive: interactive, no_integrity: no_integrity, remove_data: remove_data);
            PackageOperation op = role switch
            {
                OperationType.Update => new UpdatePackageOperation(pkg, opts),
                OperationType.Uninstall => new UninstallPackageOperation(pkg, opts),
                _ => new InstallPackageOperation(pkg, opts),
            };
            AttachTelemetry(op, pkg, role);
            AvaloniaOperationRegistry.Add(op);
            _ = op.MainThread();
        }
    }

    private static void AttachTelemetry(AbstractOperation op, IPackage pkg, OperationType role)
    {
        op.OperationSucceeded += (_, _) => Report(pkg, role, TEL_OP_RESULT.SUCCESS);
        op.OperationFailed += (_, _) => Report(pkg, role, TEL_OP_RESULT.FAILED);
    }

    private static void Report(IPackage pkg, OperationType role, TEL_OP_RESULT result)
    {
        switch (role)
        {
            case OperationType.Update:
                TelemetryHandler.UpdatePackage(pkg, result);
                break;
            case OperationType.Uninstall:
                TelemetryHandler.UninstallPackage(pkg, result);
                break;
            default:
                TelemetryHandler.InstallPackage(pkg, result, TEL_InstallReferral.DIRECT_SEARCH);
                break;
        }
    }
}
