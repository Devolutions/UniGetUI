using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Managers.MacPkgManager;

/// <summary>
/// Installs and updates run in-process (<see cref="PkgInstaller"/>). macOS has no uninstaller for
/// .pkg, so uninstalling is refused.
/// </summary>
internal sealed class MacPkgOperationHelper : BasePkgOperationHelper, IManagedPackageOperationHelper
{
    private readonly MacPkg _manager;

    public MacPkgOperationHelper(MacPkg manager)
        : base(manager) { _manager = manager; }

    // Only shown as the operation's command line; the install itself never runs it.
    protected override IReadOnlyList<string> _getOperationParameters(
        IPackage package, InstallOptions options, OperationType operation) =>
        ["-pkg", package.Id, "-target", "/"];

    protected override IReadOnlyList<string> _getOperationParameters(
        IPackage package, InstallOptions options, OperationType operation, bool standalone) =>
        standalone
            ? throw new InvalidOperationException("Installer packages are installed by UniGetUI itself and have no command line of their own.")
            : _getOperationParameters(package, options, operation);

    protected override OperationVeredict _getOperationResult(
        IPackage package, OperationType operation, IReadOnlyList<string> processOutput, int returnCode) =>
        returnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;

    // An update installs its known newer version, else the newest the feed has; reinstalling the installed
    // version, as an update with no newer one known would ask for, never upgrades anything.
    internal static string? TargetVersion(IPackage package, InstallOptions options, OperationType operation) =>
        operation == OperationType.Update
            ? package.NewVersionString.Length > 0 && package.NewVersionString != package.VersionString ? package.NewVersionString : null
            : options.Version.Length > 0 ? options.Version : null;

    public async Task<OperationVeredict?> TryPerformAsync(IPackage package, InstallOptions options,
        OperationType operation, Action<string> log, Action<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        if (operation == OperationType.Uninstall)
        {
            log("Installer packages cannot be uninstalled by UniGetUI. Ask your administrator to remove this app.");
            return OperationVeredict.Failure;
        }

        // Operations start on the UI thread; staging, hashing and the tools it waits on run off it.
        return await Task.Run(() => PerformAsync(package, options, operation, log, progress, cancellationToken),
            CancellationToken.None);
    }

    private async Task<OperationVeredict?> PerformAsync(IPackage package, InstallOptions options,
        OperationType operation, Action<string> log, Action<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        string? version = TargetVersion(package, options, operation);
        // Installs act on what the feed publishes now, not on the listing kept for a few minutes.
        _manager.RefreshPackageIndexes();
        var item = _manager.FindFeedItem(package, version, currentOnly: true)
            ?? throw new InvalidOperationException(
                $"The package feed {package.Source.Name} does not publish {package.Id}{(version is null ? "" : " " + version)} for this Mac.");

        bool allowUnsigned = _manager.FindFeedSource(package)?.AllowUnsignedPackages is true;
        var verdict = await PkgInstaller.InstallAsync(item, package.Name, log, progress, cancellationToken, allowUnsigned,
            feedId: package.Source.Name);
        if (verdict == OperationVeredict.Success)
            _manager.InvalidateInstalledPackages();
        return verdict;
    }
}
