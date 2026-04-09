using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

public sealed class PackageDetailsBuilder
{
    private string? _description;
    private string? _publisher;
    private string? _author;
    private Uri? _homepageUrl;
    private string? _license;
    private Uri? _licenseUrl;
    private Uri? _installerUrl;
    private string? _installerHash;
    private string? _installerType;
    private long _installerSize;
    private Uri? _manifestUrl;
    private string? _updateDate;
    private string? _releaseNotes;
    private Uri? _releaseNotesUrl;
    private readonly List<string> _tags = [];
    private readonly List<IPackageDetails.Dependency> _dependencies = [];

    public PackageDetailsBuilder WithDescription(string description)
    {
        _description = description;
        return this;
    }

    public PackageDetailsBuilder WithPublisher(string publisher)
    {
        _publisher = publisher;
        return this;
    }

    public PackageDetailsBuilder WithAuthor(string author)
    {
        _author = author;
        return this;
    }

    public PackageDetailsBuilder WithHomepage(string homepageUrl)
    {
        _homepageUrl = new Uri(homepageUrl);
        return this;
    }

    public PackageDetailsBuilder WithLicense(string license, string? licenseUrl = null)
    {
        _license = license;
        _licenseUrl = licenseUrl is null ? null : new Uri(licenseUrl);
        return this;
    }

    public PackageDetailsBuilder WithInstaller(string installerUrl, string hash, string installerType, long installerSize)
    {
        _installerUrl = new Uri(installerUrl);
        _installerHash = hash;
        _installerType = installerType;
        _installerSize = installerSize;
        return this;
    }

    public PackageDetailsBuilder WithManifest(string manifestUrl)
    {
        _manifestUrl = new Uri(manifestUrl);
        return this;
    }

    public PackageDetailsBuilder WithReleaseNotes(string releaseNotes, string? releaseNotesUrl = null)
    {
        _releaseNotes = releaseNotes;
        _releaseNotesUrl = releaseNotesUrl is null ? null : new Uri(releaseNotesUrl);
        return this;
    }

    public PackageDetailsBuilder WithUpdateDate(string updateDate)
    {
        _updateDate = updateDate;
        return this;
    }

    public PackageDetailsBuilder WithTag(string tag)
    {
        _tags.Add(tag);
        return this;
    }

    public PackageDetailsBuilder WithDependency(string name, string version, bool mandatory = true)
    {
        _dependencies.Add(
            new IPackageDetails.Dependency
            {
                Name = name,
                Version = version,
                Mandatory = mandatory,
            }
        );
        return this;
    }

    public PackageDetails Build(IPackage package)
    {
        return new PackageDetails(package)
        {
            Description = _description,
            Publisher = _publisher,
            Author = _author,
            HomepageUrl = _homepageUrl,
            License = _license,
            LicenseUrl = _licenseUrl,
            InstallerUrl = _installerUrl,
            InstallerHash = _installerHash,
            InstallerType = _installerType,
            InstallerSize = _installerSize,
            ManifestUrl = _manifestUrl,
            UpdateDate = _updateDate,
            ReleaseNotes = _releaseNotes,
            ReleaseNotesUrl = _releaseNotesUrl,
            Tags = _tags.ToArray(),
            Dependencies = _dependencies.ToList(),
        };
    }
}
