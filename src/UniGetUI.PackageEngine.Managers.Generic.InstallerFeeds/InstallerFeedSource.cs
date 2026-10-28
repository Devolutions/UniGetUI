using UniGetUI.Core.Classes;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.Providers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.Managers.InstallerFeeds;

/// <summary>
/// A feed's source, named by its catalog's id: unique, kept when the catalog is renamed, and what
/// "Installer packages: &lt;id&gt;" (how sources are written in bundles and the CLI) resolves back to.
/// </summary>
public sealed class InstallerFeedSource : ManagerSource
{
    public InstallerFeedSource(IPackageManager manager, string catalogId, Uri location, bool allowUnsignedPackages)
        : base(manager, catalogId, location)
    {
        AllowUnsignedPackages = allowUnsignedPackages;
    }

    /// <summary>Whether the catalog lets packages that the platform's signature check rejects install.</summary>
    public bool AllowUnsignedPackages { get; }
}

/// <summary>
/// Feeds come from the machine-wide software catalog, which an administrator deploys; they cannot
/// be added or removed from UniGetUI.
/// </summary>
public sealed class InstallerFeedSourceHelper : BaseSourceHelper
{
    public InstallerFeedSourceHelper(IPackageManager manager)
        : base(manager) { }

    /// <summary>Drops the briefly cached feed list, so the next listing reads the catalog again.</summary>
    public void ForgetSources() => TaskRecycler<IReadOnlyList<IManagerSource>>.RemoveFromCache(_getSources);

    protected override IReadOnlyList<IManagerSource> GetSources_UnSafe() =>
        CatalogFeeds.Load()
            .Select(feed => (IManagerSource)new InstallerFeedSource(Manager, feed.Id, feed.Location, feed.AllowUnsignedPackages))
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
