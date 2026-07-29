using System.Text.Json.Nodes;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.Operations.History;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// Reconstructs the package/options behind a persisted <see cref="OperationHistoryRecord"/> and
/// launches either the same operation again (re-run) or its smart inverse (revert).
/// </summary>
internal static class OperationHistoryActionService
{
    private static readonly string[] _packageKinds = ["install-package", "update-package", "uninstall-package"];

    public static bool CanReRun(OperationHistoryRecord record)
        => _packageKinds.Contains(record.Kind) && ResolveManager(record) is not null;

    public static bool CanRevert(OperationHistoryRecord record)
        => _packageKinds.Contains(record.Kind)
           && record.Status == OperationHistoryRecord.StatusSucceeded
           && ResolveManager(record) is not null;

    public static async Task ReRunAsync(OperationHistoryRecord record)
    {
        var (manager, source) = BuildContext(record);
        if (manager is null || source is null)
            return;

        // Re-running an uninstall removes a currently-installed package, so confirm first;
        // the other kinds are non-destructive repeats.
        if (record.Kind == "uninstall-package" && !await ConfirmationDialog.ShowAsync(
                CoreTools.Translate("This will uninstall {0}. Continue?", DisplayName(record))))
            return;

        Launch(BuildSameKindOperation(record, manager, source, LoadOptions(record)));
    }

    /// <summary>Retry modes offered for a failed package operation, mirroring the live-operations retry menu.</summary>
    public static (bool AsAdmin, bool Interactive, bool SkipHash) GetRetryModes(OperationHistoryRecord record)
    {
        if (record.Status != OperationHistoryRecord.StatusFailed || !_packageKinds.Contains(record.Kind))
            return (false, false, false);

        var manager = ResolveManager(record);
        if (manager is null) return (false, false, false);

        var options = LoadOptions(record);
        bool asAdmin = manager.Capabilities.CanRunAsAdmin && !options.RunAsAdministrator;
        bool interactive = manager.Capabilities.CanRunInteractively && !options.InteractiveInstallation;
        bool skipHash = manager.Capabilities.CanSkipIntegrityChecks && !options.SkipHashCheck
                        && record.Role != (int)OperationType.Uninstall;
        return (asAdmin, interactive, skipHash);
    }

    public static Task RetryAsync(OperationHistoryRecord record, string mode)
    {
        var (manager, source) = BuildContext(record);
        if (manager is null || source is null)
            return Task.CompletedTask;

        var options = LoadOptions(record);
        switch (mode)
        {
            case "admin": options.RunAsAdministrator = true; break;
            case "interactive": options.InteractiveInstallation = true; break;
            case "skip-hash": options.SkipHashCheck = true; break;
        }

        Launch(BuildSameKindOperation(record, manager, source, options));
        return Task.CompletedTask;
    }

    private static AbstractOperation? BuildSameKindOperation(
        OperationHistoryRecord record, IPackageManager manager, IManagerSource source, InstallOptions options)
    {
        string name = DisplayName(record);
        return record.Kind switch
        {
            "install-package" => new InstallPackageOperation(
                new Package(name, record.PackageId, InstalledVersion(record), source, manager), options),
            "uninstall-package" => new UninstallPackageOperation(
                new Package(name, record.PackageId, InstalledVersion(record), source, manager), options),
            "update-package" => new UpdatePackageOperation(
                new Package(name, record.PackageId, record.VersionBefore, record.VersionAfter, source, manager), options),
            _ => null,
        };
    }

    public static async Task RevertAsync(OperationHistoryRecord record)
    {
        var (manager, source) = BuildContext(record);
        if (manager is null || source is null)
            return;

        string name = DisplayName(record);

        switch (record.Kind)
        {
            case "install-package":
                {
                    // Undo an install by uninstalling the version that got installed.
                    if (!await ConfirmationDialog.ShowAsync(
                            CoreTools.Translate("This will uninstall {0}. Continue?", name)))
                        return;

                    Launch(new UninstallPackageOperation(
                        new Package(name, record.PackageId, InstalledVersion(record), source, manager),
                        LoadOptions(record)));
                    break;
                }

            case "uninstall-package":
                {
                    // Undo an uninstall by reinstalling the version that was removed (not destructive).
                    var options = LoadOptions(record);
                    if (record.VersionBefore.Length > 0) options.Version = record.VersionBefore;
                    Launch(new InstallPackageOperation(
                        new Package(name, record.PackageId, record.VersionBefore, source, manager), options));
                    break;
                }

            case "update-package":
                {
                    // Undo an update by downgrading. A plain install of the old version would leave the
                    // newer one in place (managers that allow side-by-side versions end up with two copies),
                    // so first uninstall the currently-installed version, then install the previous one.
                    if (!await ConfirmationDialog.ShowAsync(
                            CoreTools.Translate("This will downgrade {0} to version {1}. Continue?", name, record.VersionBefore)))
                        return;

                    var uninstallOp = new UninstallPackageOperation(
                        new Package(name, record.PackageId, InstalledVersion(record), source, manager),
                        LoadOptions(record));

                    var installOptions = LoadOptions(record);
                    if (record.VersionBefore.Length > 0) installOptions.Version = record.VersionBefore;
                    var installOp = new InstallPackageOperation(
                        new Package(name, record.PackageId, record.VersionBefore, source, manager),
                        installOptions, req: uninstallOp);

                    AvaloniaOperationRegistry.Add(uninstallOp);
                    AvaloniaOperationRegistry.Add(installOp);
                    // uninstallOp runs as installOp's prerequisite; launching it directly too would run it twice.
                    _ = installOp.MainThread();
                    break;
                }

            default:
                return;
        }
    }

    private static void Launch(AbstractOperation? op)
    {
        if (op is null) return;
        AvaloniaOperationRegistry.Add(op);
        _ = op.MainThread();
    }

    private static (IPackageManager? manager, IManagerSource? source) BuildContext(OperationHistoryRecord record)
    {
        try
        {
            var manager = ResolveManager(record);
            if (manager is null)
            {
                Logger.Warn($"Cannot act on history entry: unknown manager \"{record.ManagerName}\"");
                return (null, null);
            }

            var source = manager.SourcesHelper.Factory.GetSourceOrDefault(record.SourceName);
            return (manager, source);
        }
        catch (Exception ex)
        {
            Logger.Warn("Failed to reconstruct the package context for a history action");
            Logger.Warn(ex);
            return (null, null);
        }
    }

    private static IPackageManager? ResolveManager(OperationHistoryRecord record)
        => PEInterface.Managers.FirstOrDefault(
            m => m.Id.Equals(record.ManagerName, StringComparison.OrdinalIgnoreCase));

    private static InstallOptions LoadOptions(OperationHistoryRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.OptionsJson))
        {
            try
            {
                var options = new InstallOptions();
                if (JsonNode.Parse(record.OptionsJson) is { } node)
                {
                    options.LoadFromJson(node);
                    return options;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn("Failed to load persisted install options; using defaults");
                Logger.Warn(ex);
            }
        }
        return new InstallOptions();
    }

    private static string DisplayName(OperationHistoryRecord record)
        => string.IsNullOrEmpty(record.PackageName) ? record.PackageId : record.PackageName;

    // The version currently installed at record time (install/uninstall store it in VersionAfter/Before).
    private static string InstalledVersion(OperationHistoryRecord record)
        => record.VersionAfter.Length > 0 ? record.VersionAfter : record.VersionBefore;
}
