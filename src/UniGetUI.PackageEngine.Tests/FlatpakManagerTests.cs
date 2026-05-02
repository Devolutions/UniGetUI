using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Managers.FlatpakManager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Tests.Infrastructure.Assertions;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;

namespace UniGetUI.PackageEngine.Tests;

[CollectionDefinition("Flatpak manager tests", DisableParallelization = true)]
public sealed class FlatpakManagerTestCollection
{
    public const string Name = "Flatpak manager tests";
}

[Collection(FlatpakManagerTestCollection.Name)]
public sealed class FlatpakManagerTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        AppContext.BaseDirectory,
        nameof(FlatpakManagerTests),
        Guid.NewGuid().ToString("N")
    );

    public FlatpakManagerTests()
    {
        Directory.CreateDirectory(_testRoot);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Settings.ResetSettings();
    }

    public void Dispose()
    {
        Settings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    [Fact]
    public void ParseInstalledPackagesBuildsPackagesFromFixture()
    {
        var manager = new Flatpak();

        var packages = Flatpak.ParseInstalledPackages(
            File.ReadLines(PackageEngineFixtureFiles.GetPath(Path.Combine("Flatpak", "installed-list.txt"))),
            manager.DefaultSource,
            manager
        );

        Assert.Collection(
            packages,
            package =>
            {
                PackageAssert.Matches(package, "Flatseal", "com.github.tchx84.Flatseal", "2.4.0");
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            },
            package =>
            {
                PackageAssert.Matches(package, "Heroic", "com.heroicgameslauncher.hgl", "v2.21.0");
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            },
            package =>
            {
                PackageAssert.Matches(package, "High Tide", "io.github.nokse22.high-tide", "1.3.1");
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            }
        );
    }

    [Fact]
    public void ParseAvailableUpdatesBuildsPackagesFromFixture()
    {
        var manager = new Flatpak();

        var installedPackages = new List<Package>
        {
            new("Podman Desktop", "io.podman_desktop.PodmanDesktop", "1.26.0", manager.DefaultSource, manager),
            new("Mesa", "org.freedesktop.Platform.GL.default", "26.0.3", manager.DefaultSource, manager),
        };

        var packages = Flatpak.ParseAvailableUpdates(
            File.ReadLines(PackageEngineFixtureFiles.GetPath(Path.Combine("Flatpak", "updates-list.txt"))),
            manager.DefaultSource,
            manager,
            installedPackages
        );

        Assert.Collection(
            packages,
            package =>
            {
                PackageAssert.Matches(package, "Podman Desktop", "io.podman_desktop.PodmanDesktop", "1.26.0", "1.27.1");
                Assert.True(package.IsUpgradable);
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            },
            package =>
            {
                PackageAssert.Matches(package, "Mesa", "org.freedesktop.Platform.GL.default", "26.0.3", "26.0.4");
                Assert.True(package.IsUpgradable);
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            }
        );
    }

    [Fact]
    public void ParseSearchResultsBuildsPackagesFromFixture()
    {
        var manager = new Flatpak();

        var packages = Flatpak.ParseSearchResults(
            File.ReadLines(PackageEngineFixtureFiles.GetPath(Path.Combine("Flatpak", "search-sqlitebrowser.txt"))),
            manager.DefaultSource,
            manager
        );

        Assert.Collection(
            packages,
            package =>
            {
                PackageAssert.Matches(package, "DB Browser For SQLite", "org.sqlitebrowser.sqlitebrowser", "3.13.1");
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            }
        );
    }

    [Fact]
    public void ParseInstalledPackagesFiltersEmptyLines()
    {
        var manager = new Flatpak();
        var lines = new[]
        {
            "",
            "   ",
            "com.github.tchx84.Flatseal\t2.4.0\tstable\tflathub\tFlatseal",
        };

        var packages = Flatpak.ParseInstalledPackages(lines, manager.DefaultSource, manager);

        Assert.Single(packages);
        Assert.Equal("com.github.tchx84.Flatseal", packages[0].Id);
    }

    [Fact]
    public void ParseAvailableUpdatesFiltersEmptyLines()
    {
        var manager = new Flatpak();
        var lines = new[]
        {
            "",
            "io.podman_desktop.PodmanDesktop\t1.27.1\tstable\tflathub\tPodman Desktop",
        };

        var installedPackages = new List<Package>
        {
            new Package("Podman Desktop", "io.podman_desktop.PodmanDesktop", "1.26.0", manager.DefaultSource, manager),
        };

        var packages = Flatpak.ParseAvailableUpdates(lines, manager.DefaultSource, manager, installedPackages);

        Assert.Single(packages);
        Assert.Equal("io.podman_desktop.PodmanDesktop", packages[0].Id);
        Assert.Equal("1.26.0", packages[0].VersionString);
        Assert.Equal("1.27.1", packages[0].NewVersionString);
    }

    [Fact]
    public void ParseSearchResultsHandlesTabSeparatedOutputWithoutHeader()
    {
        var manager = new Flatpak();

        var packages = Flatpak.ParseSearchResults(
            File.ReadLines(PackageEngineFixtureFiles.GetPath(Path.Combine("Flatpak", "search-sqlitebrowser-tab.txt"))),
            manager.DefaultSource,
            manager
        );

        Assert.Collection(
            packages,
            package =>
            {
                PackageAssert.Matches(package, "DB Browser For SQLite", "org.sqlitebrowser.sqlitebrowser", "3.13.1");
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            }
        );
    }

    [Fact]
    public void ParseSearchResultsSkipsHeaderAndEmptyLines()
    {
        var manager = new Flatpak();
        var lines = new[]
        {
            "Name                            Description                                     Application ID                            Version          Branch          Remotes",
            "",
            "DB Browser for SQLite           light GUI editor for SQLite databases           org.sqlitebrowser.sqlitebrowser           3.13.1           stable          flathub",
        };

        var packages = Flatpak.ParseSearchResults(lines, manager.DefaultSource, manager);

        Assert.Single(packages);
        Assert.Equal("org.sqlitebrowser.sqlitebrowser", packages[0].Id);
    }

    [Fact]
    public void ParseInstalledPackagesReturnsEmptyForNoResults()
    {
        var manager = new Flatpak();

        var packages = Flatpak.ParseInstalledPackages([], manager.DefaultSource, manager);

        Assert.Empty(packages);
    }

    [Fact]
    public void ParseAvailableUpdatesReturnsEmptyForNoResults()
    {
        var manager = new Flatpak();

        var packages = Flatpak.ParseAvailableUpdates([], manager.DefaultSource, manager, []);
        Assert.Empty(packages);
    }

    [Fact]
    public void ParseSearchResultsReturnsEmptyForNoResults()
    {
        var manager = new Flatpak();
        var lines = new[]
        {
            "Name                            Description                                     Application ID                            Version          Branch          Remotes",
        };

        var packages = Flatpak.ParseSearchResults(lines, manager.DefaultSource, manager);

        Assert.Empty(packages);
    }

    [Fact]
    public void OperationHelperBuildsInstallAndUninstallParameters()
    {
        var manager = new Flatpak();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("org.sqlitebrowser.sqlitebrowser")
            .Build();
        var installOptions = new InstallOptions
        {
            CustomParameters_Install = [],
        };
        var uninstallOptions = new InstallOptions();

        var installParameters = manager.OperationHelper.GetParameters(
            package,
            installOptions,
            OperationType.Install
        );
        var uninstallParameters = manager.OperationHelper.GetParameters(
            package,
            uninstallOptions,
            OperationType.Uninstall
        );

        Assert.Equal(
            [
                "install",
                "--noninteractive",
                "-y",
                "org.sqlitebrowser.sqlitebrowser",
            ],
            installParameters
        );
        Assert.Equal(
            [
                "uninstall",
                "--noninteractive",
                "-y",
                "org.sqlitebrowser.sqlitebrowser",
            ],
            uninstallParameters
        );
    }

    [Fact]
    public void OperationHelperForcesAdministratorForAllOperations()
    {
        var manager = new Flatpak();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("com.github.tchx84.Flatseal")
            .Build();
        var options = new InstallOptions();

        _ = manager.OperationHelper.GetParameters(package, options, OperationType.Install);
        Assert.True(options.RunAsAdministrator);

        options = new InstallOptions();
        _ = manager.OperationHelper.GetParameters(package, options, OperationType.Update);
        Assert.True(options.RunAsAdministrator);

        options = new InstallOptions();
        _ = manager.OperationHelper.GetParameters(package, options, OperationType.Uninstall);
        Assert.True(options.RunAsAdministrator);
    }

    [Fact]
    public void OperationHelperReturnsSuccessOnZeroExitCode()
    {
        var manager = new Flatpak();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("com.github.tchx84.Flatseal")
            .Build();

        var result = manager.OperationHelper.GetResult(
            package,
            OperationType.Install,
            ["Installing..."],
            0
        );

        Assert.Equal(OperationVeredict.Success, result);
    }

    [Fact]
    public void OperationHelperReturnsFailureOnNonZeroExitCode()
    {
        var manager = new Flatpak();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("com.github.tchx84.Flatseal")
            .Build();

        var result = manager.OperationHelper.GetResult(
            package,
            OperationType.Install,
            ["error: something went wrong"],
            1
        );

        Assert.Equal(OperationVeredict.Failure, result);
    }

    [Fact]
    public void ManagerHasCorrectCapabilities()
    {
        var manager = new Flatpak();

        Assert.True(manager.Capabilities.CanRunAsAdmin);
        Assert.True(manager.Capabilities.CanSkipIntegrityChecks);
        Assert.True(manager.Capabilities.SupportsCustomSources);
    }

    [Fact]
    public void ManagerHasCorrectProperties()
    {
        var manager = new Flatpak();

        Assert.Equal("Flatpak", manager.Name);
        Assert.Equal("flatpak", manager.Properties.ExecutableFriendlyName);
        Assert.Equal("install", manager.Properties.InstallVerb);
        Assert.Equal("update", manager.Properties.UpdateVerb);
        Assert.Equal("uninstall", manager.Properties.UninstallVerb);
        Assert.Equal("flathub", manager.DefaultSource.Name);
        Assert.Equal("https://dl.flathub.org/repo/", manager.DefaultSource.Url.ToString());
    }

    [Fact]
    public void GetInstallableVersionsReturnsEmptyList()
    {
        var manager = new Flatpak();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("org.sqlitebrowser.sqlitebrowser")
            .Build();

        var versions = manager.DetailsHelper.GetVersions(package);
        Assert.Empty(versions);
    }
}
