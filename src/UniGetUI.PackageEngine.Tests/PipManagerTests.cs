using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Managers.PipManager;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Structs;
using UniGetUI.PackageEngine.Tests.Infrastructure.Assertions;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;

namespace UniGetUI.PackageEngine.Tests;

[CollectionDefinition("Pip manager tests", DisableParallelization = true)]
public sealed class PipManagerTestCollection
{
    public const string Name = "Pip manager tests";
}

[Collection(PipManagerTestCollection.Name)]
public sealed class PipManagerTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        AppContext.BaseDirectory,
        nameof(PipManagerTests),
        Guid.NewGuid().ToString("N")
    );

    public PipManagerTests()
    {
        Directory.CreateDirectory(_testRoot);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Settings.ResetSettings();
        Settings.Set(Settings.K.EnableProxy, false);
        Settings.Set(Settings.K.EnableProxyAuth, false);
        Settings.SetValue(Settings.K.ProxyURL, "");
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
    public void SearchHelpersParseSimpleIndexAndRankPrefixMatchesFirst()
    {
        var names = Pip.ParseSimpleIndexProjectNames(
            PackageEngineFixtureFiles.ReadAllText(Path.Combine("Pip", "simple-index.json"))
        );

        var matches = Pip.SelectSearchMatches("req", names);

        Assert.Equal(
            ["req", "requests", "requestium", "requests-cache", "django-reqtools"],
            matches
        );
    }

    [Fact]
    public void ParseAvailableUpdatesBuildsPackagesFromFixture()
    {
        var manager = new Pip();

        var packages = Pip.ParseAvailableUpdates(
            File.ReadLines(PackageEngineFixtureFiles.GetPath(Path.Combine("Pip", "outdated-list.txt"))),
            manager.DefaultSource,
            manager
        );

        Assert.Collection(
            packages,
            package =>
            {
                PackageAssert.Matches(package, "Requests", "requests", "2.31.0", "2.32.3");
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            },
            package =>
            {
                PackageAssert.Matches(
                    package,
                    "Django Reqtools",
                    "django-reqtools",
                    "1.0.0",
                    "1.1.0"
                );
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            }
        );
    }

    [Fact]
    public void ParseInstalledPackagesBuildsPackagesFromFixture()
    {
        var manager = new Pip();

        var packages = Pip.ParseInstalledPackages(
            File.ReadLines(PackageEngineFixtureFiles.GetPath(Path.Combine("Pip", "installed-list.txt"))),
            manager.DefaultSource,
            manager
        );

        Assert.Collection(
            packages,
            package =>
            {
                PackageAssert.Matches(package, "Requests", "requests", "2.31.0");
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            },
            package =>
            {
                PackageAssert.Matches(package, "Django Reqtools", "django-reqtools", "1.0.0");
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            }
        );
    }

    [Fact]
    public void OperationHelperBuildsInstallAndUninstallParameters()
    {
        Settings.Set(Settings.K.EnableProxy, true);
        Settings.Set(Settings.K.EnableProxyAuth, false);
        Settings.SetValue(Settings.K.ProxyURL, "http://proxy.example.test:3128/");
        var manager = new Pip();
        var overridenOptions = new OverridenInstallationOptions();
        overridenOptions.Pip_BreakSystemPackages = true;
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("requests")
            .WithOptions(overridenOptions)
            .Build();
        var installOptions = new InstallOptions
        {
            Version = "2.31.0",
            InstallationScope = PackageScope.User,
            PreRelease = true,
            CustomParameters_Install = ["--quiet"],
        };
        var uninstallOptions = new InstallOptions
        {
            CustomParameters_Uninstall = ["--verbose"],
        };

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
                "requests==2.31.0",
                "--no-input",
                "--no-color",
                "--no-cache",
                "--pre",
                "--user",
                "--break-system-packages",
                "--proxy http://proxy.example.test:3128/",
                "--quiet",
            ],
            installParameters
        );
        Assert.Equal(
            [
                "uninstall",
                "requests",
                "--no-input",
                "--no-color",
                "--no-cache",
                "--yes",
                "--break-system-packages",
                "--proxy http://proxy.example.test:3128/",
                "--verbose",
            ],
            uninstallParameters
        );
    }

    [Fact]
    public void OperationHelperUsesAutoRetryMutationsForKnownPipFailures()
    {
        var manager = new Pip();
        var breakSystemPackage = new PackageBuilder().WithManager(manager).WithId("requests").Build();
        var userScopedPackage = new PackageBuilder().WithManager(manager).WithId("requests").Build();

        var externallyManagedResult = manager.OperationHelper.GetResult(
            breakSystemPackage,
            OperationType.Install,
            ["error: externally-managed-environment"],
            1
        );
        var userScopeResult = manager.OperationHelper.GetResult(
            userScopedPackage,
            OperationType.Install,
            ["hint: try again with --user"],
            1
        );
        var failureResult = manager.OperationHelper.GetResult(
            new PackageBuilder().WithManager(manager).WithId("requests").Build(),
            OperationType.Install,
            ["boom"],
            1
        );

        Assert.Equal(OperationVeredict.AutoRetry, externallyManagedResult);
        Assert.True(breakSystemPackage.OverridenOptions.Pip_BreakSystemPackages);
        Assert.Equal(OperationVeredict.AutoRetry, userScopeResult);
        Assert.Equal(PackageScope.User, userScopedPackage.OverridenOptions.Scope);
        Assert.Equal(OperationVeredict.Failure, failureResult);
    }
}
