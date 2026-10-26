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

    protected override OperationVeredict _getOperationResult(
        IPackage package, OperationType operation, IReadOnlyList<string> processOutput, int returnCode) =>
        returnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;

    public async Task<OperationVeredict?> TryPerformAsync(IPackage package, InstallOptions options,
        OperationType operation, Action<string> log, Action<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        if (operation == OperationType.Uninstall)
        {
            log("Installer packages cannot be uninstalled by UniGetUI. Ask your administrator to remove this app.");
            return OperationVeredict.Failure;
        }

        string? version = operation == OperationType.Update ? package.NewVersionString
            : options.Version.Length > 0 ? options.Version : null;
        var item = await Task.Run(() => _manager.FindFeedItem(package, version), cancellationToken)
            ?? throw new InvalidOperationException(
                $"The package feed {package.Source.Name} does not publish {package.Id}{(version is null ? "" : " " + version)} for this Mac.");

        var verdict = await PkgInstaller.InstallAsync(item, package.Name, log, progress, cancellationToken);
        if (verdict == OperationVeredict.Success)
            _manager.InvalidateInstalledPackages();
        return verdict;
    }
}
