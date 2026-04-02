using UniGetUI.Core.Logging;
using UniGetUI.Interface.Enums;
using UniGetUI.Interface.Telemetry;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// Avalonia-side helpers for bulk package update operations, consumed by
/// the BackgroundApi event handlers and the --updateapps CLI flag.
/// </summary>
internal static class AvaloniaPackageOperationHelper
{
    public static async Task UpdateAllAsync()
    {
        foreach (var pkg in UpgradablePackagesLoader.Instance.Packages.ToList())
        {
            if (pkg.Tag is PackageTag.BeingProcessed or PackageTag.OnQueue) continue;
            var opts = await InstallOptionsFactory.LoadApplicableAsync(pkg);
            var op = new UpdatePackageOperation(pkg, opts);
            op.OperationSucceeded += (_, _) => TelemetryHandler.UpdatePackage(pkg, TEL_OP_RESULT.SUCCESS);
            op.OperationFailed += (_, _) => TelemetryHandler.UpdatePackage(pkg, TEL_OP_RESULT.FAILED);
            AvaloniaOperationRegistry.Add(op);
            _ = op.MainThread();
        }
    }

    public static async Task UpdateAllForManagerAsync(string managerName)
    {
        foreach (var pkg in UpgradablePackagesLoader.Instance.Packages
            .Where(p => p.Manager.Name == managerName || p.Manager.DisplayName == managerName)
            .ToList())
        {
            if (pkg.Tag is PackageTag.BeingProcessed or PackageTag.OnQueue) continue;
            var opts = await InstallOptionsFactory.LoadApplicableAsync(pkg);
            var op = new UpdatePackageOperation(pkg, opts);
            op.OperationSucceeded += (_, _) => TelemetryHandler.UpdatePackage(pkg, TEL_OP_RESULT.SUCCESS);
            op.OperationFailed += (_, _) => TelemetryHandler.UpdatePackage(pkg, TEL_OP_RESULT.FAILED);
            AvaloniaOperationRegistry.Add(op);
            _ = op.MainThread();
        }
    }

    public static async Task UpdateForIdAsync(string packageId)
    {
        var pkg = UpgradablePackagesLoader.Instance.Packages.FirstOrDefault(p => p.Id == packageId);
        if (pkg is null)
        {
            Logger.Warn($"BackgroundApi: no upgradable package found with id={packageId}");
            return;
        }

        var opts = await InstallOptionsFactory.LoadApplicableAsync(pkg);
        var op = new UpdatePackageOperation(pkg, opts);
        op.OperationSucceeded += (_, _) => TelemetryHandler.UpdatePackage(pkg, TEL_OP_RESULT.SUCCESS);
        op.OperationFailed += (_, _) => TelemetryHandler.UpdatePackage(pkg, TEL_OP_RESULT.FAILED);
        AvaloniaOperationRegistry.Add(op);
        _ = op.MainThread();
    }
}
