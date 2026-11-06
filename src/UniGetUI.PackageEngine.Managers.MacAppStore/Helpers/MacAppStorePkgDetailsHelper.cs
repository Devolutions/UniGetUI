using System.Globalization;
using System.Text.Json.Nodes;
using UniGetUI.Core.IconEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.MacAppStoreManager.Internal;

namespace UniGetUI.PackageEngine.Managers.MacAppStoreManager;

internal sealed class MacAppStorePkgDetailsHelper : BasePkgDetailsHelper
{
    private readonly MacAppStore _manager;

    public MacAppStorePkgDetailsHelper(MacAppStore manager)
        : base(manager) { _manager = manager; }

    protected override void GetDetails_UnSafe(IPackageDetails details)
    {
        var logger = Manager.TaskLogger.CreateNew(Enums.LoggableTaskType.LoadPackageDetails);
        JsonObject? app = _manager.Catalog.Get(details.Package.Id);
        if (app is null)
        {
            logger.Error($"The App Store has no record of the app {details.Package.Id} in the {_manager.Storefront} storefront.");
            logger.Close(1);
            return;
        }

        Fill(details, app);
        logger.Log($"Loaded the App Store record of {details.Package.Id}.");
        logger.Close(0);
    }

    internal static void Fill(IPackageDetails details, JsonObject app)
    {
        details.Description = AppStoreRecord.Text(app, "description");
        details.Author = AppStoreRecord.Text(app, "developerName", "artistName");
        details.Publisher = AppStoreRecord.Text(app, "sellerName") ?? details.Author;
        Uri? page = AppStoreRecord.Url(app, "appStorePageURL", "trackViewUrl");
        details.HomepageUrl = AppStoreRecord.Url(app, "sellerURL", "sellerUrl") ?? page;
        details.ManifestUrl = page;
        details.ReleaseNotesUrl = page;
        details.ReleaseNotes = AppStoreRecord.Text(app, "releaseNotes");
        details.InstallerType = CoreTools.Translate("Mac App Store");
        details.InstallerSize = AppStoreRecord.SizeInBytes(app);
        details.Tags = AppStoreRecord.Strings(app, "genres", "categories").ToArray();
        if (DateTimeOffset.TryParse(AppStoreRecord.Text(app, "currentVersionReleaseDate"),
                CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset released))
            details.UpdateDate = released.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    protected override CacheableIcon? GetIcon_UnSafe(IPackage package) =>
        _manager.Catalog.Get(package.Id) is { } app && AppStoreRecord.Icon(app) is { } icon
            ? new CacheableIcon(icon)
            : null;

    protected override IReadOnlyList<Uri> GetScreenshots_UnSafe(IPackage package) =>
        _manager.Catalog.Get(package.Id) is { } app ? AppStoreRecord.Screenshots(app) : [];

    protected override string? GetInstallLocation_UnSafe(IPackage package) => _manager.InstallPathOf(package);

    protected override IReadOnlyList<string> GetInstallableVersions_UnSafe(IPackage package) =>
        throw new InvalidOperationException("The Mac App Store only installs the latest version of an app");
}
