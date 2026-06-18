using UniGetUI.Core.IconEngine;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;

namespace UniGetUI.PackageEngine.Managers.CargoManager;

internal sealed class CargoPkgDetailsHelper(Cargo manager) : BasePkgDetailsHelper(manager)
{
    protected override void GetDetails_UnSafe(IPackageDetails details)
    {
        details.InstallerType = "Source";

        INativeTaskLogger logger = Manager.TaskLogger.CreateNew(
            Enums.LoggableTaskType.LoadPackageDetails
        );

        Uri manifestUrl;
        CargoManifest manifest;
        try
        {
            (manifestUrl, manifest) = CratesIOClient.GetManifest(details.Package.Id);
        }
        catch (Exception ex)
        {
            logger.Error(ex);
            logger.Close(1);
            return;
        }

        details.Description = manifest.crate.description;
        details.ManifestUrl = manifestUrl;

        var homepage =
            manifest.crate.homepage ?? manifest.crate.repository ?? manifest.crate.documentation;
        if (!string.IsNullOrEmpty(homepage))
        {
            details.HomepageUrl = new Uri(homepage);
        }

        var keywords = manifest.crate.keywords is null
            ? []
            : (string[])manifest.crate.keywords.Clone();
        var categories = manifest.categories?.Select(c => c.category) ?? [];
        details.Tags = [.. keywords, .. categories];

        var versionData = manifest
            .versions.Where((v) => v.num == details.Package.VersionString)
            .First();

        details.Author = versionData.published_by?.name;
        details.License = versionData.license;
        details.InstallerUrl = new Uri(
            (CratesIOClient.ApiUrl + versionData.dl_path).Replace("/api/v1/api/v1", "/api/v1")
        );
        details.InstallerSize = versionData.crate_size ?? 0;
        details.InstallerHash = versionData.checksum;
        details.Publisher = versionData.published_by?.name;
        details.UpdateDate = versionData.updated_at;

        // TODO: most packages are hosted on Github; see if there's a way to use the repository
        // info to extract release notes

        logger.Close(0);
        return;
    }

    protected override CacheableIcon? GetIcon_UnSafe(IPackage package)
    {
        throw new NotImplementedException();
    }

    protected override IReadOnlyList<Uri> GetScreenshots_UnSafe(IPackage package)
    {
        throw new NotImplementedException();
    }

    protected override string? GetInstallLocation_UnSafe(IPackage package)
    {
        // Cargo installs binaries into $CARGO_HOME/bin (default ~/.cargo/bin).
        var cargoHome = Environment.GetEnvironmentVariable("CARGO_HOME");
        if (string.IsNullOrEmpty(cargoHome))
            cargoHome = Path.Join(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".cargo"
            );
        return Path.Join(cargoHome, "bin");
    }

    protected override IReadOnlyList<string> GetInstallableVersions_UnSafe(IPackage package)
    {
        INativeTaskLogger logger = Manager.TaskLogger.CreateNew(
            Enums.LoggableTaskType.LoadPackageVersions
        );
        try
        {
            var (_, manifest) = CratesIOClient.GetManifest(package.Id);
            var versions = manifest.versions.Select((v) => v.num).ToArray();
            logger.Close(0);
            return versions;
        }
        catch (Exception ex)
        {
            logger.Error(ex);
            logger.Close(1);
            throw;
        }
    }
}
