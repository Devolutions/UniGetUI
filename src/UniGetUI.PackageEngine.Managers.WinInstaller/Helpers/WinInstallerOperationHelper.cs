using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.Managers.WinInstallerManager.Internal;
using UniGetUI.PackageEngine.Serializable;

namespace UniGetUI.PackageEngine.Managers.WinInstallerManager;

internal sealed class WinInstallerOperationHelper : BasePkgOperationHelper, IManagedPackageOperationHelper
{
    private readonly WinInstaller _manager;

    public WinInstallerOperationHelper(WinInstaller manager)
        : base(manager) { _manager = manager; }

    protected override IReadOnlyList<string> _getOperationParameters(
        IPackage package, InstallOptions options, OperationType operation) =>
        operation == OperationType.Uninstall ? ["/x", package.Id] : ["/i", package.Id];

    protected override IReadOnlyList<string> _getOperationParameters(
        IPackage package, InstallOptions options, OperationType operation, bool standalone) =>
        standalone
            ? throw new InvalidOperationException("Installer packages are installed by UniGetUI itself and have no command line of their own.")
            : _getOperationParameters(package, options, operation);

    protected override OperationVeredict _getOperationResult(
        IPackage package, OperationType operation, IReadOnlyList<string> processOutput, int returnCode) =>
        returnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure;

    internal static string? TargetVersion(IPackage package, InstallOptions options, OperationType operation) =>
        operation == OperationType.Update
            ? package.NewVersionString.Length > 0 && package.NewVersionString != package.VersionString ? package.NewVersionString : null
            : options.Version.Length > 0 ? options.Version : null;

    public async Task<OperationVeredict?> TryPerformAsync(IPackage package, InstallOptions options,
        OperationType operation, Action<string> log, Action<OperationProgress> progress,
        CancellationToken cancellationToken) =>
        await Task.Run(() => operation == OperationType.Uninstall
            ? UninstallAsync(package, log, progress)
            : InstallAsync(package, options, operation, log, progress, cancellationToken), CancellationToken.None);

    private async Task<OperationVeredict?> InstallAsync(IPackage package, InstallOptions options,
        OperationType operation, Action<string> log, Action<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        string? version = TargetVersion(package, options, operation);
        _manager.RefreshPackageIndexes();
        var item = _manager.FindFeedItem(package, version, currentOnly: true)
            ?? throw new InvalidOperationException(
                $"The package feed {package.Source.Name} does not publish {package.Id}{(version is null ? "" : " " + version)} for this computer.");

        bool allowUnsigned = _manager.FindFeedSource(package)?.AllowUnsignedPackages is true;
        var verdict = await WinInstallerRunner.InstallAsync(item, allowUnsigned, log, progress, cancellationToken);
        if (verdict == OperationVeredict.Success)
        {
            if (InstalledMatcher.Find(item, ArpEntries.Read()) is { } entry)
                InstallReceipts.Record(item.Id, entry, item.Version);
            else
                log($"{item.Name} installed, but UniGetUI does not find it in Apps and Features.");
            _manager.InvalidateInstalledPackages();
        }
        return verdict;
    }

    private async Task<OperationVeredict?> UninstallAsync(IPackage package, Action<string> log, Action<OperationProgress> progress)
    {
        var installed = _manager.ListInstalled()
            .FirstOrDefault(p => string.Equals(p.Item.Id, package.Id, StringComparison.OrdinalIgnoreCase));
        if (installed is null)
        {
            log($"{package.Name} is not listed in Apps and Features.");
            return OperationVeredict.Failure;
        }
        var verdict = await WinInstallerRunner.UninstallAsync(package.Name, installed.Item.Details, installed.Entry, log, progress);
        if (verdict == OperationVeredict.Success)
            _manager.InvalidateInstalledPackages();
        return verdict;
    }
}
