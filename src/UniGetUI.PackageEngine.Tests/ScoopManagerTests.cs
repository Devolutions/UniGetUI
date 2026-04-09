#if WINDOWS
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.ScoopManager;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Structs;
using UniGetUI.PackageEngine.Tests.Infrastructure.Assertions;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;
using Architecture = UniGetUI.PackageEngine.Enums.Architecture;

namespace UniGetUI.PackageEngine.Tests;

[CollectionDefinition("Scoop manager tests", DisableParallelization = true)]
public sealed class ScoopManagerTestCollection
{
    public const string Name = "Scoop manager tests";
}

[Collection(ScoopManagerTestCollection.Name)]
public sealed class ScoopManagerTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        AppContext.BaseDirectory,
        nameof(ScoopManagerTests),
        Guid.NewGuid().ToString("N")
    );

    public ScoopManagerTests()
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
    public void ParseSearchOutputBuildsPackagesFromBucketSections()
    {
        var manager = CreateManagerWithKnownSources("main", "versions");

        var packages = manager.ParseSearchOutput(ReadFixtureLines(@"Scoop\search-output.txt"));

        Assert.Collection(
            packages,
            package =>
            {
                PackageAssert.Matches(package, "7zip", "7zip", "24.09");
                PackageAssert.BelongsTo(package, manager, manager.SourcesHelper.Factory.GetSourceOrDefault("main"));
            },
            package =>
            {
                PackageAssert.Matches(package, "Python310", "python310", "3.10.11");
                PackageAssert.BelongsTo(
                    package,
                    manager,
                    manager.SourcesHelper.Factory.GetSourceOrDefault("versions")
                );
            }
        );
    }

    [Fact]
    public void ParseInstalledPackagesBuildsPackagesAndScopesFromFixture()
    {
        var manager = CreateManagerWithKnownSources("main", "versions");

        var packages = manager.ParseInstalledPackages(ReadFixtureLines(@"Scoop\list-output.txt"));

        Assert.Collection(
            packages,
            package =>
            {
                PackageAssert.Matches(package, "Git", "git", "2.47.1");
                PackageAssert.BelongsTo(package, manager, manager.SourcesHelper.Factory.GetSourceOrDefault("main"));
                Assert.Equal(PackageScope.User, package.OverridenOptions.Scope);
            },
            package =>
            {
                PackageAssert.Matches(package, "Pwsh", "pwsh", "7.4.6");
                PackageAssert.BelongsTo(
                    package,
                    manager,
                    manager.SourcesHelper.Factory.GetSourceOrDefault("versions")
                );
                Assert.Equal(PackageScope.Global, package.OverridenOptions.Scope);
            }
        );
    }

    [Fact]
    public void ParseAvailableUpdatesPreservesInstalledSourceAndScope()
    {
        var manager = CreateManagerWithKnownSources("main", "versions");
        var installedPackages = manager.ParseInstalledPackages(ReadFixtureLines(@"Scoop\list-output.txt"));

        var packages = manager.ParseAvailableUpdates(
            ReadFixtureLines(@"Scoop\status-output.txt"),
            installedPackages
        );

        Assert.Collection(
            packages,
            package =>
            {
                PackageAssert.Matches(package, "Git", "git", "2.47.1", "2.48.1");
                PackageAssert.BelongsTo(package, manager, manager.SourcesHelper.Factory.GetSourceOrDefault("main"));
                Assert.Equal(PackageScope.User, package.OverridenOptions.Scope);
            },
            package =>
            {
                PackageAssert.Matches(package, "Pwsh", "pwsh", "7.4.6", "7.5.0");
                PackageAssert.BelongsTo(
                    package,
                    manager,
                    manager.SourcesHelper.Factory.GetSourceOrDefault("versions")
                );
                Assert.Equal(PackageScope.Global, package.OverridenOptions.Scope);
            }
        );
    }

    [Fact]
    public void ParseSourcesNormalizesGitUrlsAndLocalBuckets()
    {
        var manager = new Scoop();
        var helper = Assert.IsType<ScoopSourceHelper>(manager.SourcesHelper);

        var sources = helper.ParseSources(ReadFixtureLines(@"Scoop\bucket-list-output.txt"));

        Assert.Collection(
            sources,
            source =>
            {
                Assert.Equal("main", source.Name);
                Assert.Equal(new Uri("https://github.com/ScoopInstaller/Main"), source.Url);
                Assert.Equal(1234, source.PackageCount);
                Assert.Equal("2024-02-01 12:34:56", source.UpdateDate);
            },
            source =>
            {
                Assert.Equal("extras", source.Name);
                Assert.Equal(
                    new Uri(
                        Path.Join(
                            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                            "scoop",
                            "buckets",
                            "extras"
                        )
                    ),
                    source.Url
                );
                Assert.Equal(321, source.PackageCount);
                Assert.Equal("2024-02-02 09:08:07", source.UpdateDate);
            }
        );
    }

    [Fact]
    public void SourceHelperBuildsBucketCommandsAndMapsExitCodes()
    {
        var manager = new Scoop();
        var source = new SourceBuilder()
            .WithManager(manager)
            .WithName("extras")
            .WithUrl("https://github.com/ScoopInstaller/Extras")
            .Build();

        Assert.Equal(
            ["bucket", "add", "extras", "https://github.com/ScoopInstaller/Extras"],
            manager.SourcesHelper.GetAddSourceParameters(source)
        );
        Assert.Equal(["bucket", "rm", "extras"], manager.SourcesHelper.GetRemoveSourceParameters(source));
        Assert.Equal(
            OperationVeredict.Success,
            manager.SourcesHelper.GetAddOperationVeredict(source, 0, [])
        );
        Assert.Equal(
            OperationVeredict.Failure,
            manager.SourcesHelper.GetRemoveOperationVeredict(source, 1, [])
        );
    }

    [Fact]
    public void InstallParametersIncludeSourceScopeArchitectureAndHashFlags()
    {
        var manager = new Scoop();
        var source = new SourceBuilder()
            .WithManager(manager)
            .WithName("extras")
            .WithUrl("https://github.com/ScoopInstaller/Extras")
            .Build();
        manager.SourcesHelper.Factory.AddSource(source);
        var package = new PackageBuilder().WithManager(manager).WithSource(source).WithId("git").Build();
        var options = new InstallOptions
        {
            InstallationScope = PackageScope.Global,
            Architecture = Architecture.x64,
            SkipHashCheck = true,
            CustomParameters_Install = ["--no-cache"],
        };

        var parameters = manager.OperationHelper.GetParameters(package, options, OperationType.Install);

        OperationAssert.HasParameters(
            parameters,
            "install",
            "extras/git",
            "--global",
            "--no-cache",
            "--skip-hash-check",
            "--arch",
            "64bit"
        );
        Assert.True(package.OverridenOptions.RunAsAdministrator);
    }

    [Fact]
    public void UninstallParametersOmitLocalSourcePrefixAndAppendPurge()
    {
        var manager = new Scoop();
        var source = new SourceBuilder()
            .WithManager(manager)
            .WithName(@"C:\Buckets\custom")
            .WithUrl("https://example.test/custom")
            .Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithSource(source)
            .WithId("custom-tool")
            .Build();
        var options = new InstallOptions
        {
            RemoveDataOnUninstall = true,
            CustomParameters_Uninstall = ["--verbose"],
        };

        var parameters = manager.OperationHelper.GetParameters(package, options, OperationType.Uninstall);

        OperationAssert.HasParameters(
            parameters,
            "uninstall",
            "custom-tool",
            "--verbose",
            "--purge"
        );
    }

    [Fact]
    public void OperationResultPromotesGlobalRetryWhenScoopRequestsGlobalFlag()
    {
        var manager = new Scoop();
        var package = new PackageBuilder().WithManager(manager).Build();

        var veredict = manager.OperationHelper.GetResult(
            package,
            OperationType.Install,
            ["Try again with the --global (or -g) flag instead"],
            1
        );

        OperationAssert.HasVeredict(veredict, OperationVeredict.AutoRetry);
        Assert.Equal(PackageScope.Global, package.OverridenOptions.Scope);
        Assert.True(package.OverridenOptions.RunAsAdministrator);
    }

    [Fact]
    public void OperationResultPromotesElevationRetryBeforeReturningFailure()
    {
        var manager = new Scoop();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithOptions(new OverridenInstallationOptions(runAsAdministrator: false))
            .Build();

        var retry = manager.OperationHelper.GetResult(
            package,
            OperationType.Install,
            ["package requires administrator rights"],
            1
        );
        var failure = manager.OperationHelper.GetResult(package, OperationType.Install, ["ERROR: failed"], 1);
        var success = manager.OperationHelper.GetResult(package, OperationType.Install, ["done"], 0);

        OperationAssert.HasVeredict(retry, OperationVeredict.AutoRetry);
        Assert.True(package.OverridenOptions.RunAsAdministrator);
        OperationAssert.HasVeredict(failure, OperationVeredict.Failure);
        OperationAssert.HasVeredict(success, OperationVeredict.Success);
    }

    private static Scoop CreateManagerWithKnownSources(params string[] sourceNames)
    {
        var manager = new Scoop();
        manager.SourcesHelper.Factory.AddSource(manager.DefaultSource);
        foreach (string sourceName in sourceNames)
        {
            IManagerSource source =
                manager.Properties.KnownSources.FirstOrDefault(source => source.Name == sourceName)
                ?? new SourceBuilder()
                    .WithManager(manager)
                    .WithName(sourceName)
                    .WithUrl($"https://example.test/{sourceName}")
                    .Build();
            manager.SourcesHelper.Factory.AddSource(source);
        }

        return manager;
    }

    private static string[] ReadFixtureLines(string relativePath)
    {
        return PackageEngineFixtureFiles.ReadAllText(relativePath).Replace("\r\n", "\n").Split('\n');
    }
}
#endif
