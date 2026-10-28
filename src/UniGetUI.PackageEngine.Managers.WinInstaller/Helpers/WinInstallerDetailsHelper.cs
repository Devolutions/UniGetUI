using UniGetUI.Core.IconEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.WinInstallerManager.Internal;

namespace UniGetUI.PackageEngine.Managers.WinInstallerManager;

internal sealed class WinInstallerDetailsHelper : BasePkgDetailsHelper
{
    private readonly WinInstaller _manager;

    public WinInstallerDetailsHelper(WinInstaller manager)
        : base(manager) { _manager = manager; }

    protected override void GetDetails_UnSafe(IPackageDetails details)
    {
        var item = _manager.FindFeedItem(details.Package);
        if (item is null)
            return;
        details.Version = item.Version;
        details.Publisher = item.Details.Publisher;
        details.InstallerUrl = item.Location;
        details.InstallerHash = item.Sha256;
        details.InstallerType = InstallerKinds.Describe(item.Details.Kind);
        details.InstallerSize = item.Size;
        details.ManifestUrl = item.Location.IsFile ? null : item.Location;
        details.Description = CoreTools.Translate("Published by your organization in {0}.", details.Package.Source.Name);
    }

    protected override CacheableIcon? GetIcon_UnSafe(IPackage package)
    {
        var item = _manager.FindFeedItem(package);
        var icon = item?.Icon
            ?? (item?.Location.IsFile == true && WinFeed.FindIcon(item.Location.LocalPath) is { } extracted
                ? new Uri(extracted)
                : null);
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
            .OrderDescending(_manager.VersionOrder)
            .ToArray();
}
