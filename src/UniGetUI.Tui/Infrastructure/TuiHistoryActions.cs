using System.Text.Json.Nodes;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.Operations.History;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageOperations;
using UniGetUI.Tui.Views.Dialogs;

namespace UniGetUI.Tui.Infrastructure;

/// <summary>
/// Re-run, retry and revert for operation history entries, ported from the desktop
/// <c>OperationHistoryActionService</c>.
/// </summary>
internal static class TuiHistoryActions
{
    private static readonly string[] PackageKinds = ["install-package", "update-package", "uninstall-package"];

    public static bool CanReRun(OperationHistoryRecord record)
        => PackageKinds.Contains(record.Kind) && ResolveManager(record) is not null;

    public static bool CanRevert(OperationHistoryRecord record)
        => PackageKinds.Contains(record.Kind) && record.Status == OperationHistoryRecord.StatusSucceeded
                                              && ResolveManager(record) is not null;

    public static (bool AsAdmin, bool Interactive, bool SkipHash) GetRetryModes(OperationHistoryRecord record)
    {
        if (record.Status != OperationHistoryRecord.StatusFailed || !PackageKinds.Contains(record.Kind))
            return (false, false, false);
        var manager = ResolveManager(record);
        if (manager is null) return (false, false, false);
        var options = LoadOptions(record);
        bool asAdmin = manager.Capabilities.CanRunAsAdmin && !options.RunAsAdministrator;
        bool interactive = manager.Capabilities.CanRunInteractively && !options.InteractiveInstallation;
        bool skipHash = PackageOperation.CanRetrySkippingIntegrityChecks(manager, options, (OperationType)record.Role,
                            record.RanElevated ?? (CoreTools.IsAdministrator() || options.RunAsAdministrator))
                        && record.Role != (int)OperationType.Uninstall;
        return (asAdmin, interactive, skipHash);
    }

    public static async Task ReRunAsync(OperationHistoryRecord record)
    {
        var (manager, source) = BuildContext(record);
        if (manager is null || source is null) return;
        if (record.Kind == "uninstall-package"
            && !await TuiPrompts.ConfirmAsync(CoreTools.Translate("Run again"), CoreTools.Translate("This will uninstall {0}. Continue?", DisplayName(record))))
            return;
        Launch(BuildSameKind(record, manager, source, LoadOptions(record)));
    }

    public static Task RetryAsync(OperationHistoryRecord record, string mode)
    {
        var (manager, source) = BuildContext(record);
        if (manager is null || source is null) return Task.CompletedTask;
        var options = LoadOptions(record);
        switch (mode)
        {
            case "admin": options.RunAsAdministrator = true; break;
            case "interactive": options.InteractiveInstallation = true; break;
            case "skip-hash": options.SkipHashCheck = true; break;
        }

        Launch(BuildSameKind(record, manager, source, options));
        return Task.CompletedTask;
    }

    public static async Task RevertAsync(OperationHistoryRecord record)
    {
        var (manager, source) = BuildContext(record);
        if (manager is null || source is null) return;
        string name = DisplayName(record);
        switch (record.Kind)
        {
            case "install-package":
                if (!await TuiPrompts.ConfirmAsync(CoreTools.Translate("Revert"), CoreTools.Translate("This will uninstall {0}. Continue?", name))) return;
                Launch(new UninstallPackageOperation(new Package(name, record.PackageId, InstalledVersion(record), source, manager), LoadOptions(record)));
                break;
            case "uninstall-package":
                {
                    var options = LoadOptions(record);
                    if (record.VersionBefore.Length > 0) options.Version = record.VersionBefore;
                    Launch(new InstallPackageOperation(new Package(name, record.PackageId, record.VersionBefore, source, manager), options));
                    break;
                }
            case "update-package":
                {
                    if (!await TuiPrompts.ConfirmAsync(CoreTools.Translate("Revert"),
                            CoreTools.Translate("This will downgrade {0} to version {1}. Continue?", name, record.VersionBefore))) return;
                    var uninstall = new UninstallPackageOperation(new Package(name, record.PackageId, InstalledVersion(record), source, manager), LoadOptions(record));
                    var installOptions = LoadOptions(record);
                    if (record.VersionBefore.Length > 0) installOptions.Version = record.VersionBefore;
                    var install = new InstallPackageOperation(new Package(name, record.PackageId, record.VersionBefore, source, manager), installOptions, req: uninstall);
                    TuiOperationRegistry.Track(uninstall);
                    TuiOperationRegistry.Start(install);
                    break;
                }
        }
    }

    private static AbstractOperation? BuildSameKind(OperationHistoryRecord record, IPackageManager manager, IManagerSource source, InstallOptions options)
    {
        string name = DisplayName(record);
        return record.Kind switch
        {
            "install-package" => new InstallPackageOperation(new Package(name, record.PackageId, InstalledVersion(record), source, manager), options),
            "uninstall-package" => new UninstallPackageOperation(new Package(name, record.PackageId, InstalledVersion(record), source, manager), options),
            "update-package" => new UpdatePackageOperation(new Package(name, record.PackageId, record.VersionBefore, record.VersionAfter, source, manager), options),
            _ => null,
        };
    }

    private static void Launch(AbstractOperation? op)
    {
        if (op is null) return;
        TuiOperationRegistry.Start(op);
        TuiNotifications.Info(CoreTools.Translate("Operation history"), op.Metadata.Title);
    }

    private static (IPackageManager? Manager, IManagerSource? Source) BuildContext(OperationHistoryRecord record)
    {
        try
        {
            var manager = ResolveManager(record);
            if (manager is null) return (null, null);
            return (manager, manager.SourcesHelper.Factory.GetSourceOrDefault(record.SourceName));
        }
        catch (Exception ex)
        {
            Logger.Warn(ex);
            return (null, null);
        }
    }

    public static IPackageManager? ResolveManager(OperationHistoryRecord record)
        => TuiEngine.Managers.FirstOrDefault(m => m.Id.Equals(record.ManagerName, StringComparison.OrdinalIgnoreCase));

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
                Logger.Warn(ex);
            }
        }

        return new InstallOptions();
    }

    public static string DisplayName(OperationHistoryRecord record)
        => string.IsNullOrEmpty(record.PackageName) ? record.PackageId : record.PackageName;

    private static string InstalledVersion(OperationHistoryRecord record)
        => record.VersionAfter.Length > 0 ? record.VersionAfter : record.VersionBefore;
}
