using UniGetUI.Core.IconEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.Managers.MacPkgManager;

internal sealed class MacPkgDetailsHelper : BasePkgDetailsHelper
{
    private readonly MacPkg _manager;

    public MacPkgDetailsHelper(MacPkg manager)
        : base(manager) { _manager = manager; }

    // A .pkg carries no description or publisher; the details show what the feed knows.
    protected override void GetDetails_UnSafe(IPackageDetails details)
    {
        var item = _manager.FindFeedItem(details.Package);
        if (item is null)
            return;
        details.Version = item.Version;
        details.InstallerUrl = item.Location;
        details.InstallerHash = item.Sha256;
        details.InstallerType = item.IsDiskImage
            ? CoreTools.Translate("Disk image (.dmg)")
            : CoreTools.Translate("Installer package (.pkg)");
        details.InstallerSize = item.Size;
        details.ManifestUrl = item.Location.IsFile ? null : item.Location;
        details.Description = CoreTools.Translate("Published by your organization in {0}.", details.Package.Source.Name);
    }

    protected override CacheableIcon? GetIcon_UnSafe(IPackage package)
    {
        var icon = _manager.FindFeedItem(package)?.Icon;
        if (icon is null)
            return null;
        return icon.IsFile ? new CacheableIcon(icon.LocalPath) : new CacheableIcon(icon, package.VersionString);
    }

    protected override IReadOnlyList<Uri> GetScreenshots_UnSafe(IPackage package) => [];

    protected override string? GetInstallLocation_UnSafe(IPackage package) => null;

    protected override IReadOnlyList<string> GetInstallableVersions_UnSafe(IPackage package) =>
        _manager.ListFeedItems(package)
            .Select(i => i.Version)
            .Distinct(StringComparer.Ordinal)
            .OrderDescending(Comparer<string>.Create((a, b) => _manager.CompareVersions(a, b) ?? string.CompareOrdinal(a, b)))
            .ToArray();
}
