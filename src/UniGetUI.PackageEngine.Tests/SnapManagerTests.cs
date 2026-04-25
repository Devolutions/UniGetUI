using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Managers.SnapManager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Tests.Infrastructure.Assertions;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;

namespace UniGetUI.PackageEngine.Tests;

[CollectionDefinition("Snap manager tests", DisableParallelization = true)]
public sealed class SnapManagerTestCollection
{
    public const string Name = "Snap manager tests";
}

[Collection(SnapManagerTestCollection.Name)]
public sealed class SnapManagerTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        AppContext.BaseDirectory,
        nameof(SnapManagerTests),
        Guid.NewGuid().ToString("N")
    );

    public SnapManagerTests()
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
        var manager = new Snap();

        var packages = Snap.ParseInstalledPackages(
            File.ReadLines(PackageEngineFixtureFiles.GetPath(Path.Combine("Snap", "installed-list.txt"))),
            manager.DefaultSource,
            manager
        );

        Assert.Collection(
            packages,
            package =>
            {
                PackageAssert.Matches(package, "Aspnetcore Runtime 100", "aspnetcore-runtime-100", "10.0.7");
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            },
            package =>
            {
                PackageAssert.Matches(package, "Bare", "bare", "1.0");
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            },
            package =>
            {
                PackageAssert.Matches(package, "Code", "code", "10c8e557");
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            },
            package =>
            {
                PackageAssert.Matches(package, "Core18", "core18", "20260204");
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            },
            package =>
            {
                PackageAssert.Matches(package, "Core20", "core20", "20260211");
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            },
            package =>
            {
                PackageAssert.Matches(package, "Core22", "core22", "20260225");
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            }
        );
    }

    [Fact]
    public void ParseAvailableUpdatesBuildsPackagesFromFixture()
    {
        var manager = new Snap();

        var installedPackages = new List<Package>
        {
            new("Discord", "discord", "0.0.130", manager.DefaultSource, manager),
            new("Firefox", "firefox", "149.0", manager.DefaultSource, manager),
        };

        var packages = Snap.ParseAvailableUpdates(
            File.ReadLines(PackageEngineFixtureFiles.GetPath(Path.Combine("Snap", "updates-list.txt"))),
            manager.DefaultSource,
            manager,
            installedPackages
        );

        Assert.Collection(
            packages,
            package =>
            {
                PackageAssert.Matches(package, "Discord", "discord", "0.0.130", "0.0.134");
                Assert.True(package.IsUpgradable);
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            },
            package =>
            {
                PackageAssert.Matches(package, "Firefox", "firefox", "149.0", "150.0-1");
                Assert.True(package.IsUpgradable);
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            }
        );
    }

    [Fact]
    public void ParseSearchResultsBuildsPackagesFromFixture()
    {
        var manager = new Snap();

        var packages = Snap.ParseSearchResults(
            File.ReadLines(PackageEngineFixtureFiles.GetPath(Path.Combine("Snap", "find-signal.txt"))),
            manager.DefaultSource,
            manager
        );

        Assert.Collection(
            packages,
            package =>
            {
                PackageAssert.Matches(package, "Signal Desktop", "signal-desktop", "8.5.0");
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            }
        );
    }

    [Fact]
    public void ParseInstalledPackagesSkipsHeaderAndEmptyLines()
    {
        var manager = new Snap();
        var lines = new[]
        {
            "Name                       Version                         Rev    Tracking            Publisher      Notes",
            "",
            "   ",
            "code                       10c8e557                        235    latest/stable       vscode✓        classic",
        };

        var packages = Snap.ParseInstalledPackages(lines, manager.DefaultSource, manager);

        Assert.Single(packages);
        Assert.Equal("code", packages[0].Id);
    }

    [Fact]
    public void ParseAvailableUpdatesSkipsHeaderAndEmptyLines()
    {
        var manager = new Snap();
        var lines = new[]
        {
            "Name     Version  Rev   Size   Publisher      Notes",
            "",
            "firefox  150.0-1  8191  288MB  mozilla✓       -",
        };

        var installedPackages = new List<Package>
        {
            new Package("Firefox", "firefox", "149.0", manager.DefaultSource, manager),
        };

        var packages = Snap.ParseAvailableUpdates(lines, manager.DefaultSource, manager, installedPackages);

        Assert.Single(packages);
        Assert.Equal("firefox", packages[0].Id);
        Assert.Equal("149.0", packages[0].VersionString);
        Assert.Equal("150.0-1", packages[0].NewVersionString);
    }

    [Fact]
    public void ParseSearchResultsSkipsHeaderAndEmptyLines()
    {
        var manager = new Snap();
        var lines = new[]
        {
            "Name            Version  Publisher      Notes  Summary",
            "",
            "signal-desktop  8.5.0    snapcrafters✪  -      Speak Freely - Private Messenger",
        };

        var packages = Snap.ParseSearchResults(lines, manager.DefaultSource, manager);

        Assert.Single(packages);
        Assert.Equal("signal-desktop", packages[0].Id);
    }

    [Fact]
    public void ParseInstalledPackagesReturnsEmptyForNoResults()
    {
        var manager = new Snap();
        var lines = new[]
        {
            "Name                       Version                         Rev    Tracking            Publisher      Notes",
        };

        var packages = Snap.ParseInstalledPackages(lines, manager.DefaultSource, manager);

        Assert.Empty(packages);
    }

    [Fact]
    public void ParseAvailableUpdatesReturnsEmptyForNoResults()
    {
        var manager = new Snap();
        var lines = new[]
        {
            "Name     Version  Rev   Size   Publisher      Notes",
        };

        var packages = Snap.ParseAvailableUpdates(lines, manager.DefaultSource, manager, []);
        Assert.Empty(packages);
    }

    [Fact]
    public void ParseSearchResultsReturnsEmptyForNoResults()
    {
        var manager = new Snap();
        var lines = new[]
        {
            "Name            Version  Publisher      Notes  Summary",
        };

        var packages = Snap.ParseSearchResults(lines, manager.DefaultSource, manager);

        Assert.Empty(packages);
    }

    [Fact]
    public void OperationHelperBuildsInstallAndUninstallParameters()
    {
        var manager = new Snap();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("signal-desktop")
            .Build();
        var installOptions = new InstallOptions
        {
            CustomParameters_Install = ["--classic"],
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
                "signal-desktop",
                "--classic",
            ],
            installParameters
        );
        Assert.Equal(
            [
                "remove",
                "signal-desktop",
                "--purge",
            ],
            uninstallParameters
        );
    }

    [Fact]
    public void OperationHelperForcesAdministratorForAllOperations()
    {
        var manager = new Snap();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("firefox")
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
        var manager = new Snap();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("firefox")
            .Build();

        var result = manager.OperationHelper.GetResult(
            package,
            OperationType.Install,
            ["Setting up firefox..."],
            0
        );

        Assert.Equal(OperationVeredict.Success, result);
    }

    [Fact]
    public void OperationHelperReturnsFailureOnNonZeroExitCode()
    {
        var manager = new Snap();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("firefox")
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
        var manager = new Snap();

        Assert.True(manager.Capabilities.CanRunAsAdmin);
        Assert.True(manager.Capabilities.CanSkipIntegrityChecks);
        Assert.False(manager.Capabilities.SupportsCustomSources);
    }

    [Fact]
    public void ManagerHasCorrectProperties()
    {
        var manager = new Snap();

        Assert.Equal("Snap", manager.Name);
        Assert.Equal("snap", manager.Properties.ExecutableFriendlyName);
        Assert.Equal("install", manager.Properties.InstallVerb);
        Assert.Equal("refresh", manager.Properties.UpdateVerb);
        Assert.Equal("remove", manager.Properties.UninstallVerb);
        Assert.Equal("snapcraft", manager.DefaultSource.Name);
        Assert.Equal("https://snapcraft.io/", manager.DefaultSource.Url.ToString());
    }

    [Fact]
    public void GetInstallableVersionsReturnsEmptyList()
    {
        var manager = new Snap();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("signal-desktop")
            .Build();

        var versions = manager.DetailsHelper.GetVersions(package);

        Assert.Empty(versions);
    }
}
