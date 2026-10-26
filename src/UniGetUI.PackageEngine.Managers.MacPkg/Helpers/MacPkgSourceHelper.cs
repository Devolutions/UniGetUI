using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.Providers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;

namespace UniGetUI.PackageEngine.Managers.MacPkgManager;

/// <summary>
/// Feeds come from the machine-wide software catalog, which an administrator deploys; they cannot
/// be added or removed from UniGetUI.
/// </summary>
internal sealed class MacPkgSourceHelper : BaseSourceHelper
{
    public MacPkgSourceHelper(MacPkg manager)
        : base(manager) { }

    protected override IReadOnlyList<IManagerSource> GetSources_UnSafe() =>
        PkgCatalogFeeds.Load()
            .Select(feed => (IManagerSource)new ManagerSource(Manager, feed.Name, feed.Location))
            .ToArray();

    public override string[] GetAddSourceParameters(IManagerSource source) =>
        throw new NotSupportedException("Package feeds are declared in the software catalog.");

    public override string[] GetRemoveSourceParameters(IManagerSource source) =>
        throw new NotSupportedException("Package feeds are declared in the software catalog.");

    protected override OperationVeredict _getAddSourceOperationVeredict(IManagerSource source, int ReturnCode, string[] Output) =>
        OperationVeredict.Failure;

    protected override OperationVeredict _getRemoveSourceOperationVeredict(IManagerSource source, int ReturnCode, string[] Output) =>
        OperationVeredict.Failure;
}
