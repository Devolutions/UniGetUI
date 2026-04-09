#if WINDOWS
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Managers.Choco;
using UniGetUI.PackageEngine.Managers.ChocolateyManager;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Structs;
using UniGetUI.PackageEngine.Tests.Infrastructure.Assertions;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;
using Architecture = UniGetUI.PackageEngine.Enums.Architecture;

namespace UniGetUI.PackageEngine.Tests;

[CollectionDefinition("Chocolatey manager tests", DisableParallelization = true)]
public sealed class ChocolateyManagerTestCollection
{
    public const string Name = "Chocolatey manager tests";
}

[Collection(ChocolateyManagerTestCollection.Name)]
public sealed class ChocolateyManagerTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        AppContext.BaseDirectory,
        nameof(ChocolateyManagerTests),
        Guid.NewGuid().ToString("N")
    );

    public ChocolateyManagerTests()
    {
        Directory.CreateDirectory(_testRoot);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Settings.ResetSettings();
        Settings.Set(Settings.K.EnableProxy, false);
        Settings.Set(Settings.K.EnableProxyAuth, false);
        Settings.Set(Settings.K.UseSystemChocolatey, false);
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
    public void GetProxyArgumentReturnsConfiguredProxyWhenAuthenticationIsDisabled()
    {
        Settings.Set(Settings.K.EnableProxy, true);
        Settings.Set(Settings.K.EnableProxyAuth, false);
        Settings.SetValue(Settings.K.ProxyURL, "http://proxy.example.test:3128/");

        Assert.Equal("--proxy http://proxy.example.test:3128/", Chocolatey.GetProxyArgument());
    }

    [Fact]
    public void ParseAvailableUpdatesFiltersNoiseAndBuildsPackagesFromFixture()
    {
        var manager = new Chocolatey();

        var packages = manager.ParseAvailableUpdates(
            ReadFixtureLines("Chocolatey\\outdated-output.txt")
        );

        Assert.Collection(
            packages,
            package =>
            {
                Assert.Equal("git", package.Id);
                Assert.Equal("2.47.0", package.VersionString);
                Assert.Equal("2.48.1", package.NewVersionString);
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            },
            package =>
            {
                Assert.Equal("7zip", package.Id);
                Assert.Equal("24.9.0", package.VersionString);
                Assert.Equal("25.0.0", package.NewVersionString);
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            }
        );
    }

    [Fact]
    public void ParseInstalledPackagesFiltersNoiseAndBuildsPackagesFromFixture()
    {
        var manager = new Chocolatey();

        var packages = manager.ParseInstalledPackages(ReadFixtureLines("Chocolatey\\list-output.txt"));

        Assert.Collection(
            packages,
            package =>
            {
                Assert.Equal("git", package.Id);
                Assert.Equal("2.47.0", package.VersionString);
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            },
            package =>
            {
                Assert.Equal("7zip", package.Id);
                Assert.Equal("24.9.0", package.VersionString);
                PackageAssert.BelongsTo(package, manager, manager.DefaultSource);
            }
        );
    }

    [Fact]
    public void ParseSourcesNormalizesCommunityFeedsAndPreservesCustomFeeds()
    {
        var manager = new Chocolatey();
        var helper = Assert.IsType<ChocolateySourceHelper>(manager.SourcesHelper);

        var sources = helper.ParseSources(ReadFixtureLines("Chocolatey\\source-list-output.txt"));

        Assert.Collection(
            sources,
            source =>
            {
                Assert.Equal("community", source.Name);
                Assert.Equal(new Uri("https://community.chocolatey.org/api/v2/"), source.Url);
            },
            source =>
            {
                Assert.Equal("community", source.Name);
                Assert.Equal(new Uri("https://community.chocolatey.org/api/v2/"), source.Url);
            },
            source =>
            {
                Assert.Equal("internal repo", source.Name);
                Assert.Equal(new Uri("https://packages.example.test/api/v2/"), source.Url);
            }
        );
    }

    [Fact]
    public void ParseInstallableVersionsReadsApprovedVersionsFromFixture()
    {
        var versions = ChocolateyDetailsHelper.ParseInstallableVersions(
            ReadFixtureLines("Chocolatey\\search-versions-output.txt")
        );

        Assert.Equal(["2.46.0", "2.47.0", "2.48.1"], versions);
    }

    [Fact]
    public void InstallParametersIncludeChocolateySpecificFlagsAndCustomParameters()
    {
        var manager = new Chocolatey();
        var package = new PackageBuilder().WithManager(manager).WithId("git").Build();
        var options = new InstallOptions
        {
            InteractiveInstallation = true,
            Architecture = Architecture.x86,
            PreRelease = true,
            SkipHashCheck = true,
            Version = "2.48.1",
            CustomParameters_Install = ["--install-arg"],
        };

        var parameters = manager.OperationHelper.GetParameters(package, options, OperationType.Install);

        OperationAssert.HasParameters(
            parameters,
            "install",
            "git",
            "-y",
            "--notsilent",
            "--no-progress",
            "--forcex86",
            "--prerelease",
            "--ignore-checksums",
            "--force",
            "--version=2.48.1",
            "--allow-downgrade",
            "--install-arg"
        );
    }

    [Fact]
    public void UninstallParametersUseUninstallVerbAndOnlyUninstallCustomParameters()
    {
        var manager = new Chocolatey();
        var package = new PackageBuilder().WithManager(manager).WithId("git").Build();
        var options = new InstallOptions
        {
            InteractiveInstallation = true,
            SkipHashCheck = true,
            CustomParameters_Install = ["--install-arg"],
            CustomParameters_Uninstall = ["--remove-arg"],
        };

        var parameters = manager.OperationHelper.GetParameters(package, options, OperationType.Uninstall);

        OperationAssert.HasParameters(
            parameters,
            "uninstall",
            "git",
            "-y",
            "--notsilent",
            "--remove-arg"
        );
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3010)]
    [InlineData(1641)]
    [InlineData(1614)]
    [InlineData(1605)]
    public void OperationResultTreatsChocolateySuccessCodesAsSuccess(int returnCode)
    {
        var manager = new Chocolatey();
        var package = new PackageBuilder().WithManager(manager).Build();

        var veredict = manager.OperationHelper.GetResult(
            package,
            OperationType.Install,
            ["completed"],
            returnCode
        );

        OperationAssert.HasVeredict(veredict, OperationVeredict.Success);
    }

    [Fact]
    public void OperationResultPromotesElevationFailuresToAutoRetry()
    {
        var manager = new Chocolatey();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithOptions(new OverridenInstallationOptions(runAsAdministrator: false))
            .Build();

        var veredict = manager.OperationHelper.GetResult(
            package,
            OperationType.Install,
            ["Access denied while installing package"],
            1
        );

        OperationAssert.HasVeredict(veredict, OperationVeredict.AutoRetry);
        Assert.True(package.OverridenOptions.RunAsAdministrator);
    }

    [Fact]
    public void OperationResultReturnsFailureWhenElevationWasAlreadyRequested()
    {
        var manager = new Chocolatey();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithOptions(new OverridenInstallationOptions(runAsAdministrator: true))
            .Build();

        var veredict = manager.OperationHelper.GetResult(
            package,
            OperationType.Install,
            ["Access denied while installing package"],
            1
        );

        OperationAssert.HasVeredict(veredict, OperationVeredict.Failure);
    }

    private static string[] ReadFixtureLines(string relativePath)
    {
        return PackageEngineFixtureFiles.ReadAllText(relativePath).Replace("\r\n", "\n").Split('\n');
    }
}
#endif
