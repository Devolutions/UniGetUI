using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Serializable;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

namespace UniGetUI.PackageEngine.Tests;

public sealed class InstallerFileNameTests : IDisposable
{
    private readonly string _testRoot;

    public InstallerFileNameTests()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            nameof(InstallerFileNameTests),
            Guid.NewGuid().ToString("N")
        );
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        SecureSettings.TEST_SecureSettingsRootOverride = Path.Combine(_testRoot, "SecureSettings");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Settings.ResetSettings();
        Settings.SetValue(
            Settings.K.InstallerFileNameScheme,
            InstallerFileNaming.NameAndVersionValue
        );
    }

    public void Dispose()
    {
        Settings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        SecureSettings.TEST_SecureSettingsRootOverride = null;
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private static TestPackageManager BuildManager(
        bool installerUrlFollowsPackageVersion,
        string? manifestVersion = null
    ) =>
        new PackageManagerBuilder()
            .WithName("PowerShellTest")
            .ConfigureDetails(helper =>
            {
                helper.PopulateDetails = details => details.Version = manifestVersion;
            })
            .ConfigureManager(manager =>
                manager.SetInstallerUrlFollowsPackageVersion(installerUrlFollowsPackageVersion))
            .Build();

    private TestPackageManager BuildManagerReportingManifestVersion(string? manifestVersion) =>
        new PackageManagerBuilder()
            .ConfigureDetails(helper =>
            {
                helper.PopulateDetails = details =>
                {
                    details.InstallerUrl = new Uri(Path.Combine(_testRoot, "ContosoSetup.exe"));
                    details.InstallerType = "exe";
                    details.Version = manifestVersion;
                };
            })
            .Build();

    private static ImportedPackage BuildImportedPackage(TestPackageManager manager) =>
        new(
            new SerializablePackage
            {
                Id = "Contoso.Tool",
                Name = "Contoso Tool",
                Version = "1.0.0",
                ManagerName = manager.Name,
                Source = manager.DefaultSource.Name,
            },
            manager,
            manager.DefaultSource
        );

    private static void InitializeLoaders()
    {
        _ = new DiscoverablePackagesLoader([]);
        _ = new UpgradablePackagesLoader([]);
        _ = new InstalledPackagesLoader([]);
    }

    [Fact]
    public async Task ListedVersionIsUsedWhenTheManagerPinsTheInstallerUrl()
    {
        var manager = BuildManager(installerUrlFollowsPackageVersion: true);
        InitializeLoaders();

        var package = new PackageBuilder()
            .WithManager(manager)
            .WithName("Contoso Tool")
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("2.0.0")
            .Build();

        Assert.Equal("Contoso Tool_1.0.0.nupkg", await package.GetInstallerFileName());
    }

    [Fact]
    public async Task NewVersionIsUsedWhenThePackageIsUpgradable()
    {
        var manager = BuildManager(installerUrlFollowsPackageVersion: false);
        InitializeLoaders();

        var package = new PackageBuilder()
            .WithManager(manager)
            .WithName("Contoso Tool")
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("2.0.0")
            .Build();

        Assert.Equal("Contoso Tool_2.0.0.nupkg", await package.GetInstallerFileName());
    }

    [Fact]
    public async Task NewVersionOfTheUpgradableEquivalentIsUsed()
    {
        var manager = BuildManager(installerUrlFollowsPackageVersion: false);
        InitializeLoaders();

        await UpgradablePackagesLoader.Instance.AddForeign(
            new PackageBuilder()
                .WithManager(manager)
                .WithName("Contoso Tool")
                .WithId("Contoso.Tool")
                .WithVersion("1.0.0")
                .WithNewVersion("3.0.0")
                .Build()
        );

        var installed = new PackageBuilder()
            .WithManager(manager)
            .WithName("Contoso Tool")
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .Build();

        Assert.Equal("Contoso Tool_3.0.0.nupkg", await installed.GetInstallerFileName());
    }

    [Fact]
    public async Task VersionOfTheAvailableEquivalentIsUsed()
    {
        var manager = BuildManager(installerUrlFollowsPackageVersion: false);
        InitializeLoaders();

        await DiscoverablePackagesLoader.Instance.AddForeign(
            new PackageBuilder()
                .WithManager(manager)
                .WithName("Contoso Tool")
                .WithId("Contoso.Tool")
                .WithVersion("4.0.0")
                .Build()
        );

        var installed = new PackageBuilder()
            .WithManager(manager)
            .WithName("Contoso Tool")
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .Build();

        Assert.Equal("Contoso Tool_4.0.0.nupkg", await installed.GetInstallerFileName());
    }

    [Fact]
    public async Task ListedVersionIsUsedWhenNoEquivalentIsLoaded()
    {
        var manager = BuildManager(installerUrlFollowsPackageVersion: false);
        InitializeLoaders();

        var package = new PackageBuilder()
            .WithManager(manager)
            .WithName("Contoso Tool")
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .Build();

        Assert.Equal("Contoso Tool_1.0.0.nupkg", await package.GetInstallerFileName());
    }

    [Fact]
    public async Task TheVersionReportedByTheLoadedManifestIsPreferred()
    {
        var manager = BuildManagerReportingManifestVersion("5.0.0");
        InitializeLoaders();

        var package = new PackageBuilder()
            .WithManager(manager)
            .WithName("Contoso Tool")
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("2.0.0")
            .Build();

        Assert.Equal("Contoso Tool_5.0.0.exe", await package.GetInstallerFileName());
    }

    [Fact]
    public async Task ImportedPackagesAreNamedAfterTheVersionTheManifestOffers()
    {
        var manager = BuildManagerReportingManifestVersion("7.2.1");
        InitializeLoaders();

        Assert.Equal(
            "Contoso Tool_7.2.1.exe",
            await BuildImportedPackage(manager).GetInstallerFileName()
        );
    }

    [Fact]
    public async Task APlaceholderManifestVersionDoesNotSuppressTheFallbackChain()
    {
        var manager = BuildManagerReportingManifestVersion("Unknown");
        InitializeLoaders();

        var package = new PackageBuilder()
            .WithManager(manager)
            .WithName("Contoso Tool")
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("2.0.0")
            .Build();

        Assert.Equal("Contoso Tool_2.0.0.exe", await package.GetInstallerFileName());
    }

    [Fact]
    public async Task ImportedPackagesNeverCarryTheLatestPlaceholderAsAVersion()
    {
        var manager = BuildManagerReportingManifestVersion(null);
        InitializeLoaders();

        Assert.Equal(
            "Contoso Tool.exe",
            await BuildImportedPackage(manager).GetInstallerFileName()
        );
    }

    [Fact]
    public async Task AnUnpinnedImportIsNamedAfterTheVersionThePinnedUrlResolvedTo()
    {
        var manager = BuildManager(
            installerUrlFollowsPackageVersion: true,
            manifestVersion: "9.1.0"
        );
        InitializeLoaders();

        Assert.Equal(
            "Contoso Tool_9.1.0.nupkg",
            await BuildImportedPackage(manager).GetInstallerFileName()
        );
    }

    [Fact]
    public async Task APinnedVersionStillWinsWhenTheManagerPinsTheInstallerUrl()
    {
        var manager = BuildManager(
            installerUrlFollowsPackageVersion: true,
            manifestVersion: "9.1.0"
        );
        InitializeLoaders();

        var package = new PackageBuilder()
            .WithManager(manager)
            .WithName("Contoso Tool")
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .Build();

        Assert.Equal("Contoso Tool_1.0.0.nupkg", await package.GetInstallerFileName());
    }

    [Fact]
    public async Task PublisherNameSchemeIgnoresTheResolvedVersion()
    {
        Settings.SetValue(
            Settings.K.InstallerFileNameScheme,
            InstallerFileNaming.PublisherNameValue
        );
        var manager = BuildManager(installerUrlFollowsPackageVersion: false);
        InitializeLoaders();

        var package = new PackageBuilder()
            .WithManager(manager)
            .WithName("Contoso Tool")
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("2.0.0")
            .Build();

        Assert.Equal("Contoso.Tool.nupkg", await package.GetInstallerFileName());
    }
}
