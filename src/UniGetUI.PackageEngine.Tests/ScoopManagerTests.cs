#if WINDOWS
using System.Diagnostics;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.ScoopManager;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Structs;
using UniGetUI.PackageEngine.Tests.Infrastructure.Assertions;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;
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
    private const string LongId =
        "a-scoop-package-whose-manifest-name-is-long-enough-to-overflow-the-default-console-width-by-far";
    private const string LongVersion = "20260727133500-nightly";
    private const string LongNewVersion = "20260820144900-nightly";

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
    public void ParseSourcesKeepsLocalBucketsWhosePathContainsSpaces()
    {
        var manager = new Scoop();
        var helper = Assert.IsType<ScoopSourceHelper>(manager.SourcesHelper);

        var sources = helper.ParseSources(ReadFixtureLines(@"Scoop\bucket-list-output-spaced-path.txt"));

        Assert.Collection(
            sources,
            source =>
            {
                Assert.Equal("main", source.Name);
                Assert.Equal(SpacedBucketUrl("main"), source.Url);
                Assert.Equal(2, source.PackageCount);
                Assert.Equal("2026-09-22 3:46:27", source.UpdateDate);
            },
            source =>
            {
                Assert.Equal("extras", source.Name);
                Assert.Equal(SpacedBucketUrl("extras"), source.Url);
                Assert.Equal(3, source.PackageCount);
                Assert.Equal("2026-09-22 3:46:27", source.UpdateDate);
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
                Assert.Equal(new Uri(@"C:\Users\fixture\scoop\buckets\extras"), source.Url);
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
    public void InstallParametersAppendRequestedVersionToSpec()
    {
        var manager = CreateManagerWithKnownSources("main");
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithSource(manager.SourcesHelper.Factory.GetSourceOrDefault("main"))
            .WithId("jq")
            .Build();

        var install = manager.OperationHelper.GetParameters(
            package,
            new InstallOptions { Version = "1.7.1" },
            OperationType.Install
        );
        var update = manager.OperationHelper.GetParameters(
            package,
            new InstallOptions { Version = "1.7.1" },
            OperationType.Update
        );

        OperationAssert.HasParameters(install, "install", "main/jq@1.7.1");
        OperationAssert.HasParameters(update, "update", "main/jq");
    }

    [Fact]
    public void InstallParametersRefuseVersionsAShellWouldReinterpret()
    {
        var manager = CreateManagerWithKnownSources("main");
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithSource(manager.SourcesHelper.Factory.GetSourceOrDefault("main"))
            .WithId("jq")
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            manager.OperationHelper.GetParameters(
                package,
                new InstallOptions { Version = "1.7.1;calc" },
                OperationType.Install
            )
        );
    }

    [Fact]
    public void PinnedInstallsResolveTheirBucketAndUpdateWithForce()
    {
        var manager = CreateManagerWithKnownSources("main");
        manager.FindBucketManifest = id => id == "jq" ? ("main", "1.8.2") : null;

        var installed = manager.ParseInstalledPackages(PinnedListLines);
        var updates = manager.ParseAvailableUpdates(EmptyStatusLines, installed, includePinnedInstalls: true);

        Assert.Collection(
            installed,
            package =>
            {
                PackageAssert.Matches(package, "Jq", "jq", "1.7.1");
                PackageAssert.BelongsTo(package, manager, manager.SourcesHelper.Factory.GetSourceOrDefault("main"));
            },
            package => Assert.Equal(Scoop.GeneratedManifestSource, package.Source.Name)
        );
        var update = Assert.Single(updates);
        PackageAssert.Matches(update, "Jq", "jq", "1.7.1", "1.8.2");
        OperationAssert.HasParameters(
            manager.OperationHelper.GetParameters(update, new InstallOptions(), OperationType.Update),
            "update",
            "main/jq",
            "--force"
        );
        OperationAssert.HasParameters(
            manager.OperationHelper.GetParameters(installed[1], new InstallOptions(), OperationType.Uninstall),
            "uninstall",
            "orphan"
        );
    }

    [Fact]
    public void PinnedInstallsResolveToTheBucketScoopWouldPick()
    {
        string scoopRoot = Path.Combine(_testRoot, "scoop");
        WriteBucketManifest(scoopRoot, "aaa", nested: false, "jq", "9.9.9");
        WriteBucketManifest(scoopRoot, "main", nested: true, "jq", "1.8.2");
        WriteBucketManifest(scoopRoot, "zzz", nested: false, "orphan", "0.2.0");
        string? previousRoot = Environment.GetEnvironmentVariable("SCOOP");
        Environment.SetEnvironmentVariable("SCOOP", scoopRoot);
        try
        {
            var manager = CreateManagerWithKnownSources("main", "zzz");

            var installed = manager.ParseInstalledPackages(PinnedListLines);
            var updates = manager.ParseAvailableUpdates(EmptyStatusLines, installed, includePinnedInstalls: true);

            Assert.Equal(["main", "zzz"], installed.Select(package => package.Source.Name));
            Assert.Equal(
                [("jq", "1.8.2"), ("orphan", "0.2.0")],
                updates.Select(package => (package.Id, package.NewVersionString))
            );
        }
        finally
        {
            Environment.SetEnvironmentVariable("SCOOP", previousRoot);
        }
    }

    private static void WriteBucketManifest(string scoopRoot, string bucket, bool nested, string id, string version)
    {
        string directory = Path.Combine(scoopRoot, "buckets", bucket, nested ? "bucket" : "");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, id + ".json"), $$"""{ "version": "{{version}}" }""");
    }

    [Fact]
    public void PinnedInstallIsFoundWhenTheOtherScopeHasTheSameVersionUnpinned()
    {
        var manager = CreateManagerWithKnownSources("main");
        manager.FindBucketManifest = id => id == "jq" ? ("main", "1.8.2") : null;

        var installed = manager.ParseInstalledPackages(
        [
            "Name Version Source           Updated             Info",
            "---- ------- ------           -------             ----",
            "jq   1.7.1   main             2026-10-27 15:35:44 64bit",
            "jq   1.7.1   <auto-generated> 2026-10-27 15:35:44 Global install",
        ]);
        var updates = manager.ParseAvailableUpdates(EmptyStatusLines, installed, includePinnedInstalls: true);

        var update = Assert.Single(updates);
        PackageAssert.Matches(update, "Jq", "jq", "1.7.1", "1.8.2");
        Assert.Equal(PackageScope.Global, update.OverridenOptions.Scope);
    }

    [Fact]
    public void PinnedInstallsAreNotOfferedAsUpdatesOnOlderScoop()
    {
        var manager = CreateManagerWithKnownSources("main");
        manager.FindBucketManifest = id => id == "jq" ? ("main", "1.8.2") : null;

        var installed = manager.ParseInstalledPackages(PinnedListLines);

        Assert.Empty(manager.ParseAvailableUpdates(EmptyStatusLines, installed));
    }

    [Fact]
    public void PinnedInstallsAtOrAboveBucketVersionAreNotOfferedAsUpdates()
    {
        var manager = CreateManagerWithKnownSources("main");
        manager.FindBucketManifest = id => id == "jq" ? ("main", "1.7.1") : null;

        var installed = manager.ParseInstalledPackages(PinnedListLines);

        Assert.Empty(manager.ParseAvailableUpdates(EmptyStatusLines, installed, includePinnedInstalls: true));
    }

    [Theory]
    [InlineData("Current Scoop version:\nv0.6.0 - Released at 2026-09-30\n", true)]
    [InlineData("Current Scoop version:\nv0.10.1 - Released at 2027-01-01\n", true)]
    [InlineData("Current Scoop version:\nv0.5.3 - Released at 2025-08-11\n", false)]
    [InlineData("Current Scoop version:\ne6aa3b36 (HEAD -> develop) fix something\n", false)]
    [InlineData("Unknown", false)]
    public void PinnedUpdatesRequireScoopWithForcedPinUpgrade(string versionOutput, bool expected)
    {
        Assert.Equal(expected, Scoop.UpdatesPinnedInstalls(versionOutput));
    }

    [Theory]
    [InlineData("0.1.0", "0.1.1", 1)]
    [InlineData("0.1.1", "0.2.0", 1)]
    [InlineData("0.2.0", "1.0.0", 1)]
    [InlineData("0.4.0", "0.5.0-alpha.1", 1)]
    [InlineData("0.5.0-alpha.1", "0.5.0-alpha.2", 1)]
    [InlineData("0.5.0-alpha.2", "0.5.0-alpha.10", 1)]
    [InlineData("0.5.0-alpha.10", "0.5.0-beta", 1)]
    [InlineData("0.5.0-beta", "0.5.0-alpha.10", -1)]
    [InlineData("0.5.0-beta", "0.5.0-beta.0", 1)]
    [InlineData("0.5.0-rc.1", "0.5.0-z", 1)]
    [InlineData("0.5.0-rc.1", "0.5.0-howdy", -1)]
    [InlineData("0.5.0-howdy", "0.5.0-rc.1", 1)]
    [InlineData("0.0.0.0", "0.0.0.1", 1)]
    [InlineData("0.0.1.2", "0.0.2.0", 1)]
    [InlineData("0.1.1.2", "0.2.0.0", 1)]
    [InlineData("0.2.0.0", "1.0.0.0", 1)]
    [InlineData("1", "1.1", 1)]
    [InlineData("1", "1.0", 1)]
    [InlineData("1.1.0.0", "1.1", -1)]
    [InlineData("1.4", "1.3.0", -1)]
    [InlineData("1.4", "1.3.255.255", -1)]
    [InlineData("1.4", "1.4.4", 1)]
    [InlineData("1.1.1_8", "1.1.1", -1)]
    [InlineData("1.1.1_8", "1.1.1_9", 1)]
    [InlineData("1.1.1_10", "1.1.1_9", -1)]
    [InlineData("1.1.1b", "1.1.1a", -1)]
    [InlineData("1.1.1a", "1.1.1b", 1)]
    [InlineData("1.1a2", "1.1a3", 1)]
    [InlineData("1.1.1a10", "1.1.1b1", 1)]
    [InlineData("1.8.9", "1.8.5-1", -1)]
    [InlineData("7.0.4-9", "7.0.4-10", 1)]
    [InlineData("7.0.4-9", "7.0.4-8", -1)]
    [InlineData("2019-01-01", "2019-01-02", 1)]
    [InlineData("2019-01-02", "2019-01-01", -1)]
    [InlineData("2018-01-01", "2019-01-01", 1)]
    [InlineData("1", "1+hotfix.0", 1)]
    [InlineData("1.0.0", "1.0.0+hotfix.0", 1)]
    [InlineData("1.0.0+hotfix.0", "1.0.0+hotfix.1", 1)]
    [InlineData("1.0.0+hotfix.1", "1.0.1", 1)]
    [InlineData("1.0.0+1.1", "1.0.0+1", -1)]
    [InlineData("latest", "20150405", -1)]
    [InlineData("0.5alpha", "0.5", 1)]
    [InlineData("0.5", "0.5Beta", -1)]
    [InlineData("0.4", "0.5Beta", 1)]
    [InlineData("7.0.4-9", "", -1)]
    [InlineData("12.0", "12.0", 0)]
    [InlineData("7.0.4-9", "7.0.4-9", 0)]
    [InlineData("7.7.0-preview.5", "7.7.0-rc.1", 1)]
    [InlineData("7.7.0-rc.1", "7.7.0", 1)]
    [InlineData("7.7.0-preview.5", "7.7.0", 1)]
    [InlineData("1.0.0-beta5", "1.0.0-beta10", 1)]
    [InlineData("1.0.0-beta.2.1", "1.0.0-rc.1", 1)]
    [InlineData("7.1.2-34", "7.1.2-35", 1)]
    [InlineData("7.1.2", "7.1.2-35", 1)]
    [InlineData("26.09.0", "26.09.0-0", 1)]
    [InlineData("7.7.0-rc", "7.7.0-rc.0", 1)]
    [InlineData("7.7.0-RC.1", "7.7.0-rc.1", 0)]
    public void CompareVersionsAgreesWithScoopCompareVersion(string reference, string difference, int expected)
    {
        Assert.Equal(expected, new Scoop().CompareVersions(difference, reference));
    }

    [Theory]
    [InlineData("nightly-20190801", "nightly")]
    [InlineData("nightly-20190801", "nightly-20200801")]
    [InlineData("2.0.0_rc1", "2.0.0")]
    [InlineData("4.16.1", "4.16_BETA2")]
    [InlineData("1.0.007", "1.0.7")]
    public void CompareVersionsIsUnknownWhereScoopReachesNoVerdict(string reference, string difference)
    {
        Assert.Null(new Scoop().CompareVersions(difference, reference));
    }

    [Fact]
    public void CompareVersionsOrdersSegmentsBeyondInt64()
    {
        Assert.Equal(1, new Scoop().CompareVersions("1.0.100000000000000000000", "1.0.99999999999999999999"));
    }

    [Fact]
    public void CompareVersionsIsUnknownForAMissingVersion()
    {
        var manager = new Scoop();

        Assert.Null(manager.CompareVersions(null!, "1.0"));
        Assert.Null(manager.CompareVersions("1.0", null!));
    }

    [Fact]
    public void PinnedPreReleaseIsOfferedWhenTheBucketMovesToTheNextLabel()
    {
        var manager = CreateManagerWithKnownSources("versions");
        manager.FindBucketManifest = id => id == "pwsh-beta" ? ("versions", "7.7.0-rc.1") : null;

        var installed = manager.ParseInstalledPackages(
            [
                "Name      Version         Source           Updated             Info",
                "----      -------         ------           -------             ----",
                "pwsh-beta 7.7.0-preview.5 <auto-generated> 2026-11-16 13:54:06",
            ]
        );
        var updates = manager.ParseAvailableUpdates(EmptyStatusLines, installed, includePinnedInstalls: true);

        var update = Assert.Single(updates);
        PackageAssert.Matches(update, "Pwsh Beta", "pwsh-beta", "7.7.0-preview.5", "7.7.0-rc.1");
    }

    [Fact]
    public async Task UpdateLoaderKeepsAPreReleaseWhoseLabelChanged()
    {
        Directory.CreateDirectory(CoreData.UniGetUIInstallationOptionsDirectory);
        var manager = CreateManagerWithKnownSources("versions");
        var installed = manager.ParseInstalledPackages(
            [
                "Installed apps:",
                "",
                "Name      Version         Source   Updated             Info",
                "----      -------         ------   -------             ----",
                "pwsh-beta 7.7.0-preview.5 versions 2026-10-27 15:35:44 64bit",
            ]
        );

        var update = Assert.Single(
            manager.ParseAvailableUpdates(
                [
                    "Name      Installed Version Latest Version Missing Dependencies Info",
                    "----      ----------------- -------------- -------------------- ----",
                    "pwsh-beta 7.7.0-preview.5   7.7.0-rc.1",
                ],
                installed
            )
        );

        Assert.Equal("7.7.0-rc.1", update.NewVersionString);

        _ = new InstalledPackagesLoader([manager]);
        _ = new DiscoverablePackagesLoader([manager]);
        var loader = new TestUpgradablePackagesLoader([manager]);
        Assert.True(await loader.EvaluatePackageAsync(update));
    }

    private static readonly string[] PinnedListLines =
    [
        "Installed apps:",
        "",
        "Name   Version Source           Updated             Info",
        "----   ------- ------           -------             ----",
        "jq     1.7.1   <auto-generated> 2026-10-27 15:35:44 64bit",
        "orphan 0.1.0   <auto-generated> 2026-10-27 15:35:44 64bit",
    ];

    private static readonly string[] EmptyStatusLines =
    [
        "Name Installed Version Latest Version Missing Dependencies Info",
        "---- ----------------- -------------- -------------------- ----",
    ];

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

    [Fact]
    public void OperationResultRetriesElevatedOnShimResolutionFailure()
    {
        var manager = new Scoop();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithOptions(new OverridenInstallationOptions(runAsAdministrator: false))
            .Build();

        var retry = manager.OperationHelper.GetResult(
            package,
            OperationType.Update,
            ["Creating shim for 'notepad++'.", "Can't shim 'notepad++.exe': File doesn't exist."],
            1
        );

        OperationAssert.HasVeredict(retry, OperationVeredict.AutoRetry);
        Assert.True(package.OverridenOptions.RunAsAdministrator);

        // Already elevated: the same failure must not loop, it should surface as a plain failure
        var failure = manager.OperationHelper.GetResult(
            package,
            OperationType.Update,
            ["Can't shim 'notepad++.exe': File doesn't exist."],
            1
        );
        OperationAssert.HasVeredict(failure, OperationVeredict.Failure);
    }

    [Fact]
    public void OperationResultDoesNotRetryShimMessageOnSuccess()
    {
        var manager = new Scoop();
        var package = new PackageBuilder().WithManager(manager).Build();

        var veredict = manager.OperationHelper.GetResult(
            package,
            OperationType.Update,
            ["Creating shim for 'tool'.", "Can't shim is mentioned but the operation succeeded"],
            0
        );

        OperationAssert.HasVeredict(veredict, OperationVeredict.Success);
    }

    [Fact]
    public void OperationResultDoesNotElevateShimFailureWhenElevationProhibited()
    {
        Settings.Set(Settings.K.ProhibitElevation, true);
        var manager = new Scoop();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithOptions(new OverridenInstallationOptions(runAsAdministrator: false))
            .Build();

        var veredict = manager.OperationHelper.GetResult(
            package,
            OperationType.Update,
            ["Can't shim 'notepad++.exe': File doesn't exist."],
            1
        );

        OperationAssert.HasVeredict(veredict, OperationVeredict.Failure);
        Assert.False(package.OverridenOptions.RunAsAdministrator);
    }

    [Fact]
    public void ParseInstalledPackagesReadsSourcesThatAreEmptyOrContainSpaces()
    {
        var manager = CreateManagerWithKnownSources("main");

        var packages = manager.ParseInstalledPackages(
            ReadFixtureLines(@"Scoop\list-output-edge-cases.txt")
        );

        Assert.Collection(
            packages,
            package =>
            {
                PackageAssert.Matches(package, "Normal App", "normal-app", "3.0.0");
                Assert.Equal("main", package.Source.Name);
            },
            package =>
            {
                PackageAssert.Matches(package, "Orphan App", "orphan-app", "1.0.0");
                Assert.Same(manager.DefaultSource, package.Source);
            },
            package =>
            {
                PackageAssert.Matches(package, "Spaced App", "spaced-app", "2.0.0");
                Assert.Equal(
                    @"C:\Users\Jane Doe\git\Extras\bucket\spaced-app.json",
                    package.Source.Name
                );
            }
        );
    }

    [Fact]
    public void ParseAvailableUpdatesReadsColumnsThroughAnsiColourCodes()
    {
        var manager = CreateManagerWithKnownSources("main");
        var installedPackages = manager.ParseInstalledPackages(
            ReadFixtureLines(@"Scoop\list-output-not-outdated.txt")
        );

        var packages = manager.ParseAvailableUpdates(
            ReadFixtureLines(@"Scoop\status-output-ansi.txt"),
            installedPackages
        );

        var package = Assert.Single(packages);
        PackageAssert.Matches(package, "Outdated App", "outdated-app", "1.0.0", "2.0.0");
    }

    [Fact]
    public void ParseAvailableUpdatesSkipsRowsListedWithoutANewerVersion()
    {
        var manager = CreateManagerWithKnownSources("main");
        var installedPackages = manager.ParseInstalledPackages(
            ReadFixtureLines(@"Scoop\list-output-not-outdated.txt")
        );

        var packages = manager.ParseAvailableUpdates(
            ReadFixtureLines(@"Scoop\status-output-not-outdated.txt"),
            installedPackages
        );

        var package = Assert.Single(packages);
        PackageAssert.Matches(package, "Outdated App", "outdated-app", "1.0.0", "2.0.0");
    }

    [Fact]
    public void ParseAvailableUpdatesKeepsRowsThatOverflowTheDefaultConsoleWidth()
    {
        var manager = CreateManagerWithKnownSources("main");

        var installedPackages = manager.ParseInstalledPackages(
            RunPowerShellTable(
                $"@(@('{LongId}','{LongVersion}'),@('7zip','26.03')) "
                    + "| ForEach-Object { [PSCustomObject][ordered]@{ Name = $_[0]; "
                    + "Version = $_[1]; Source = 'main' } }"
            )
        );

        var packages = manager.ParseAvailableUpdates(
            RunPowerShellTable(
                $"@(@('{LongId}','{LongVersion}','{LongNewVersion}'),@('7zip','26.03','26.04')) "
                    + "| ForEach-Object { [PSCustomObject][ordered]@{ Name = $_[0]; "
                    + "'Installed Version' = $_[1]; 'Latest Version' = $_[2] } }"
            ),
            installedPackages
        );

        var package = Assert.Single(packages, package => package.Id == LongId);
        Assert.Equal(LongVersion, package.VersionString);
        Assert.Equal(LongNewVersion, package.NewVersionString);
    }

    private static string[] RunPowerShellTable(string script)
    {
        using Process p = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments =
                    "-NoProfile -ExecutionPolicy Bypass -Command \""
                    + script
                    + Scoop.UntruncatedTableOutput
                    + "\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            },
        };

        p.Start();
        return [.. ScoopProcess.ReadLines(p, new TestProcessTaskLogger())];
    }

    private static Uri SpacedBucketUrl(string bucket) =>
        new(
            Path.Join(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                @"AppData\Local\Temp\My Scoop\buckets",
                bucket
            )
        );

    [Theory]
    [InlineData("https://example.test/manifests/app.json", "https://example.test/manifests/app.json")]
    [InlineData(@"C:\manifests\app.json", "file:///C:/manifests/app.json")]
    public void ManifestUrlComesFromTheSourceNameForUrlAndPathInstalls(string sourceName, string expected)
    {
        var manager = new Scoop();
        IManagerSource source = manager.SourcesHelper.Factory.GetSourceOrDefault(sourceName);
        var package = new PackageBuilder().WithManager(manager).WithSource(source).WithId("app").Build();

        Assert.Equal(new Uri(expected), ScoopPkgDetailsHelper.GetManifestUrl(package, manager.SourcesHelper.Factory));
    }

    [Fact]
    public void ManifestUrlIsOmittedForABucketWhoseUrlIsStillUnknown()
    {
        var manager = new Scoop();
        IManagerSource source = manager.SourcesHelper.Factory.GetSourceOrDefault("extras");
        var package = new PackageBuilder().WithManager(manager).WithSource(source).WithId("notepadplusplus").Build();

        Assert.Null(ScoopPkgDetailsHelper.GetManifestUrl(package, manager.SourcesHelper.Factory));
    }

    [Fact]
    public void ManifestUrlPointsIntoTheBucketRepository()
    {
        var manager = CreateManagerWithKnownSources("extras");
        IManagerSource source = manager.SourcesHelper.Factory.GetSourceOrDefault("extras");
        var package = new PackageBuilder().WithManager(manager).WithSource(source).WithId("notepadplusplus").Build();

        Assert.Equal(
            new Uri(source.Url + "/blob/master/bucket/notepadplusplus.json"),
            ScoopPkgDetailsHelper.GetManifestUrl(package, manager.SourcesHelper.Factory)
        );
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

    [Theory]
    [InlineData(0, "", 0, false)]
    [InlineData(1, "WARN  There aren't any apps installed.", 0, false)]
    [InlineData(1, "WARN  There aren't any apps installed.", 2, true)]
    [InlineData(1, "Invoke-Expression: cannot find path", 0, true)]
    [InlineData(-196608, "", 0, true)]
    public void ScoopOnlyAcceptsNoAppsInstalledAsAnEmptyListing(int exitCode, string output, int parsed, bool failed)
    {
        Assert.Equal(failed, Scoop.IsInstalledListingFailure(exitCode, [output], parsed));
    }

    private sealed class ListingScoop : Scoop
    {
        public IReadOnlyList<Package> ListInstalled() => GetInstalledPackages_UnSafe();
    }

    private ManagerStatus ScoopListingStatus(int exitCode, string error, string version)
    {
        string script = Path.Combine(_testRoot, $"listing-{Guid.NewGuid():N}.ps1");
        File.WriteAllLines(script,
        [
            "Write-Output 'Name Version Source Updated Info'",
            "Write-Output '---- ------- ------ ------- ----'",
            $"Write-Output 'git  {version}     main'",
            $"[Console]::Error.WriteLine('{error}')",
            $"exit {exitCode}",
        ]);
        return new()
        {
            Found = true,
            ExecutablePath = CoreData.PowerShell5,
            ExecutableCallArgs = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
        };
    }

    [Fact]
    public void CachedScoopListingReplaysItsFailureAndKeepsPartialRowsForEveryNormalCaller()
    {
        var manager = new ListingScoop { Status = ScoopListingStatus(17, "broken bucket", "1.0") };
        List<Exception> firstFailures = [];
        using (PackageListingScope.TrackFailures(firstFailures.Add))
            Assert.Equal("1.0", Assert.Single(manager.ListInstalled()).VersionString);
        Assert.IsType<InvalidOperationException>(Assert.Single(firstFailures));

        manager.Status = ScoopListingStatus(0, "", "2.0");
        List<Exception> cachedFailures = [];
        using (PackageListingScope.TrackFailures(cachedFailures.Add))
            Assert.Equal("1.0", Assert.Single(manager.ListInstalled()).VersionString);
        Assert.IsType<InvalidOperationException>(Assert.Single(cachedFailures));

        using var scope = PackageListingScope.EnterListingOnly();
        Assert.Equal("2.0", Assert.Single(manager.ListInstalled()).VersionString);
    }

    [Fact]
    public void EveryListingOnlyScoopCallListsAgainInsteadOfReusingAnEarlierSuccessfulCache()
    {
        var manager = new ListingScoop { Status = ScoopListingStatus(0, "", "1.0") };
        Assert.Equal("1.0", Assert.Single(manager.ListInstalled()).VersionString);

        using var scope = PackageListingScope.EnterListingOnly();
        manager.Status = ScoopListingStatus(0, "", "2.0");
        Assert.Equal("2.0", Assert.Single(manager.ListInstalled()).VersionString);
        manager.Status = ScoopListingStatus(17, "broken bucket", "3.0");
        Assert.Throws<InvalidOperationException>(manager.ListInstalled);
    }
}
#endif
