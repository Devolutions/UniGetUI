using UniGetUI.Core.IconEngine;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

public sealed class TestPackageDetailsHelper(TestPackageManager manager) : BasePkgDetailsHelper(manager)
{
    public Func<IPackage, IPackageDetails?>? DetailsFactory { get; set; }

    public Action<IPackageDetails>? PopulateDetails { get; set; }

    public Func<IPackage, IReadOnlyList<string>> VersionsFactory { get; set; } = static _ => [];

    public Func<IPackage, CacheableIcon?> IconFactory { get; set; } = static _ => null;

    public Func<IPackage, IReadOnlyList<Uri>> ScreenshotsFactory { get; set; } = static _ => [];

    public Func<IPackage, string?> InstallLocationFactory { get; set; } = static _ => null;

    protected override void GetDetails_UnSafe(IPackageDetails details)
    {
        if (DetailsFactory?.Invoke(details.Package) is { } source)
        {
            Copy(source, details);
        }

        PopulateDetails?.Invoke(details);
    }

    protected override IReadOnlyList<string> GetInstallableVersions_UnSafe(IPackage package)
    {
        return VersionsFactory(package);
    }

    protected override CacheableIcon? GetIcon_UnSafe(IPackage package)
    {
        return IconFactory(package);
    }

    protected override IReadOnlyList<Uri> GetScreenshots_UnSafe(IPackage package)
    {
        return ScreenshotsFactory(package);
    }

    protected override string? GetInstallLocation_UnSafe(IPackage package)
    {
        return InstallLocationFactory(package);
    }

    private static void Copy(IPackageDetails source, IPackageDetails target)
    {
        target.Description = source.Description;
        target.Publisher = source.Publisher;
        target.Author = source.Author;
        target.HomepageUrl = source.HomepageUrl;
        target.License = source.License;
        target.LicenseUrl = source.LicenseUrl;
        target.InstallerUrl = source.InstallerUrl;
        target.InstallerHash = source.InstallerHash;
        target.InstallerType = source.InstallerType;
        target.InstallerSize = source.InstallerSize;
        target.ManifestUrl = source.ManifestUrl;
        target.UpdateDate = source.UpdateDate;
        target.ReleaseNotes = source.ReleaseNotes;
        target.ReleaseNotesUrl = source.ReleaseNotesUrl;
        target.Tags = source.Tags.ToArray();
        target.Dependencies.Clear();
        target.Dependencies.AddRange(source.Dependencies);
    }
}
