using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.Managers.MacAppStoreManager.Internal;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Managers.MacAppStoreManager;

/// <summary>
/// mas 4 and later need root to install and update apps, and every release needs it to uninstall
/// one, so those operations use configured sudo Touch ID support or the native password dialog
/// (<see cref="ElevatedMas"/>). The others (mas 3 and earlier, or UniGetUI already running as root)
/// run as an ordinary process.
/// </summary>
internal sealed class MacAppStorePkgOperationHelper : BasePkgOperationHelper, IManagedPackageOperationHelper
{
    private readonly MacAppStore _manager;

    /// <summary>Whether UniGetUI already runs as root. Replaceable for tests.</summary>
    internal static Func<bool> IsRoot { get; set; } = CoreTools.IsAdministrator;

    public MacAppStorePkgOperationHelper(MacAppStore manager)
        : base(manager) { _manager = manager; }

    // An unknown version (0) is treated as a current one.
    private bool IsLegacyMas => _manager.MasMajorVersion is > 0 and < 3;

    /// <summary>
    /// The mas command for an operation. "get" acquires a free app, whether or not the account
    /// already has it; a paid app must already be bought, and "install" downloads it again.
    /// </summary>
    internal string Verb(IPackage package, OperationType operation) => operation switch
    {
        OperationType.Uninstall => "uninstall",
        OperationType.Update => IsLegacyMas ? "upgrade" : "update",
        _ => _manager.Catalog.Peek(package.Id) is { } app && AppStoreRecord.Price(app) > 0
            ? "install"
            : IsLegacyMas ? "purchase" : "get",
    };

    internal bool NeedsRoot(OperationType operation) =>
        !IsRoot() && (operation == OperationType.Uninstall || _manager.MasMajorVersion is 0 or >= 4);

    protected override IReadOnlyList<string> _getOperationParameters(
        IPackage package,
        InstallOptions options,
        OperationType operation)
    {
        List<string> parameters = [Verb(package, operation), package.Id];
        parameters.AddRange(
            operation switch
            {
                OperationType.Update => options.CustomParameters_Update,
                OperationType.Uninstall => options.CustomParameters_Uninstall,
                _ => options.CustomParameters_Install,
            });
        return parameters;
    }

    protected override OperationVeredict _getOperationResult(
        IPackage package,
        OperationType operation,
        IReadOnlyList<string> processOutput,
        int returnCode) =>
        Verdict(returnCode, processOutput, message => Logger.Warn(message));

    public Task<OperationVeredict?> TryPerformAsync(IPackage package, InstallOptions options,
        OperationType operation, Action<string> log, Action<OperationProgress> progress,
        CancellationToken cancellationToken) =>
        // Operations start on the UI thread; the price lookup and the wait for mas run off it.
        Task.Run(() => PerformAsync(package, options, operation, log, cancellationToken), CancellationToken.None);

    private async Task<OperationVeredict?> PerformAsync(IPackage package, InstallOptions options,
        OperationType operation, Action<string> log, CancellationToken cancellationToken)
    {
        // The price decides between "get" and "install"; cached, it also reaches the command line
        // an ordinary process builds when this returns null.
        if (operation == OperationType.Install)
            _manager.Catalog.Get(package.Id);

        if (!NeedsRoot(operation))
            return null;

        if (Settings.Get(Settings.K.ProhibitElevation))
        {
            log(CoreTools.Translate(
                "The Mac App Store needs administrator rights for this operation, but elevation is prohibited in UniGetUI's settings."));
            return OperationVeredict.Failure;
        }

        IReadOnlyList<string> arguments = GetParameters(package, options, operation);
        var (exitCode, output) = await ElevatedMas.RunAsync(
            _manager.Status.ExecutablePath, arguments, Prompt(package, operation), log, cancellationToken);

        return Verdict(exitCode, output, log);
    }

    internal OperationVeredict Verdict(int exitCode, IReadOnlyList<string> output, Action<string> log)
    {
        if (exitCode == 0)
        {
            _manager.InvalidateInstalledPackages();
            return OperationVeredict.Success;
        }

        if (exitCode == NativeMas.CancellationResult
            || output.Any(line => line.TrimEnd().EndsWith("(-128)", StringComparison.Ordinal)))
            return OperationVeredict.Canceled;

        if (output.Any(line => line.Contains("No downloads initiated", StringComparison.OrdinalIgnoreCase)))
            log(CoreTools.Translate(
                "The Mac App Store did not start a download. Open the App Store and try this app there. Check that the signed-in Apple Account owns it and that it is available in your region."));

        return OperationVeredict.Failure;
    }

    internal static string Prompt(IPackage package, OperationType operation) => operation switch
    {
        OperationType.Uninstall => CoreTools.Translate("UniGetUI wants to uninstall {0}.", package.Name),
        OperationType.Update => CoreTools.Translate("UniGetUI wants to update {0} from the Mac App Store.", package.Name),
        _ => CoreTools.Translate("UniGetUI wants to install {0} from the Mac App Store.", package.Name),
    };
}
