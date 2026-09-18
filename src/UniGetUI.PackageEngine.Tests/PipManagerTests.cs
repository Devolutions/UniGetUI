using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
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

    [Theory]
    [InlineData("3.13.15", "2.2.0")]
    [InlineData("3.10.2", "2.1.0")]
    [InlineData("3.7.9", "2.0.0")]
    [InlineData("3.6.0", "1.0.0")]
    public void ResolveLatestCompatibleVersionHonoursTheRunningInterpreter(
        string interpreterVersion,
        string expected
    )
    {
        Assert.True(PythonVersion.TryParse(interpreterVersion, out var interpreter));

        var resolved = Pip.ResolveLatestCompatibleVersion(
            "sample-project",
            PackageEngineFixtureFiles.ReadAllText(
                Path.Combine("Pip", "simple-sample-project.json")
            ),
            interpreter
        );

        Assert.Equal(expected, resolved);
    }

    [Theory]
    [InlineData("zope.interface")]
    [InlineData("zope-interface")]
    [InlineData("Zope_Interface")]
    public void ResolveLatestCompatibleVersionMatchesEscapedDistributionFilenames(
        string projectName
    )
    {
        Assert.True(PythonVersion.TryParse("3.11.4", out var interpreter));

        var resolved = Pip.ResolveLatestCompatibleVersion(
            projectName,
            PackageEngineFixtureFiles.ReadAllText(
                Path.Combine("Pip", "simple-zope-interface.json")
            ),
            interpreter
        );

        Assert.Equal("6.0", resolved);
    }

    [Fact]
    public void ResolveLatestCompatibleVersionDoesNotMatchAVersionThatIsOnlyAPrefix()
    {
        Assert.True(PythonVersion.TryParse("3.11.4", out var interpreter));
        const string payload = """
            {
              "versions": ["2.1.0"],
              "files": [{ "filename": "demo-2.1.01-py3-none-any.whl", "yanked": false }]
            }
            """;

        Assert.Null(Pip.ResolveLatestCompatibleVersion("demo", payload, interpreter));
    }

    [Fact]
    public void ResolveLatestCompatibleVersionRejectsAResponseWithoutAVersionList()
    {
        Assert.True(PythonVersion.TryParse("3.11.4", out var interpreter));

        Assert.Throws<InvalidDataException>(
            () => Pip.ResolveLatestCompatibleVersion("demo", """{ "files": [] }""", interpreter)
        );
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData(">=3.8", true)]
    [InlineData(">=3.12", false)]
    [InlineData("not a specifier", false)]
    public void IsInterpreterAllowedTreatsAnAbsentConstraintAsSatisfied(
        string? requiresPython,
        bool expected
    )
    {
        Assert.True(PythonVersion.TryParse("3.11.4", out var interpreter));

        Assert.Equal(expected, Pip.IsInterpreterAllowed(requiresPython, interpreter));
    }

    [Theory]
    [InlineData(
        "pip 26.2.1 from C:\\Python313\\Lib\\site-packages\\pip (python 3.13)",
        "3.13"
    )]
    [InlineData("pip 24.0 from /usr/lib/python3/dist-packages/pip (python 3.11)", "3.11")]
    [InlineData("pip 24.0 from /usr/lib/pip", null)]
    [InlineData(null, null)]
    public void ParseInterpreterVersionReadsThePipVersionBanner(string? banner, string? expected)
    {
        Assert.Equal(expected, Pip.ParseInterpreterVersion(banner));
    }

    [Theory]
    [InlineData("global.index-url='https://mirror.example.test/simple'", true)]
    [InlineData("global.extra-index-url='https://mirror.example.test/simple'", true)]
    [InlineData("global.no-index='true'", true)]
    [InlineData("global.find-links='C:\\\\wheels'", true)]
    [InlineData("install.no-index='true'", true)]
    [InlineData(":env:.config-file='./pip.conf'", false)]
    [InlineData("global.trusted-host='mirror.example.test'", false)]
    [InlineData("global.timeout='60'", false)]
    [InlineData("", false)]
    public void IsIndexConfigurationLineDetectsEverySourceChangingSetting(
        string line,
        bool expected
    )
    {
        Assert.Equal(expected, Pip.IsIndexConfigurationLine(line));
    }

    [Theory]
    [InlineData("zope.interface", "zope-interface")]
    [InlineData("Flask_SQLAlchemy", "flask-sqlalchemy")]
    [InlineData("requests", "requests")]
    public void NormalizeProjectNameForUrlFollowsTheSimpleApiRules(string name, string expected)
    {
        Assert.Equal(expected, Pip.NormalizeProjectNameForUrl(name));
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
                "\"requests==2.31.0\"",
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
