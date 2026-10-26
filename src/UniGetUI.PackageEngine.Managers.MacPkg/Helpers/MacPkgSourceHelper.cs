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
            .Select(feed => (IManagerSource)new PkgFeedSource(Manager, feed.Id, feed.Location, feed.AllowUnsignedPackages))
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

/// <summary>
/// A feed's source, named by its catalog's id: unique, kept when the catalog is renamed, and what
/// "Installer packages: &lt;id&gt;" (how sources are written in bundles and the CLI) resolves back to.
/// </summary>
internal sealed class PkgFeedSource : ManagerSource
{
    public PkgFeedSource(IPackageManager manager, string catalogId, Uri location, bool allowUnsignedPackages)
        : base(manager, catalogId, location)
    {
        AllowUnsignedPackages = allowUnsignedPackages;
    }

    /// <summary>Whether the catalog lets packages that Gatekeeper rejects (unsigned, not notarized) install.</summary>
    public bool AllowUnsignedPackages { get; }
}
