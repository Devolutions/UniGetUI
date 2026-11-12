using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

namespace UniGetUI.PackageEngine.Tests;

public sealed class SkipMajorUpdatesTests : IDisposable
{
    private static readonly string[] Feed =
    [
        "6.0.0",
        "5.12.0rc1",
        "5.11.0-beta",
        "5.10.0",
        "5.3.1",
        "5.2.0",
        "5.0.0",
        "4.9.0",
    ];

    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        nameof(SkipMajorUpdatesTests),
        Guid.NewGuid().ToString("N")
    );

    public SkipMajorUpdatesTests()
    {
        Directory.CreateDirectory(_testRoot);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        SecureSettings.TEST_SecureSettingsRootOverride = Path.Combine(_testRoot, "SecureSettings");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Directory.CreateDirectory(CoreData.UniGetUIInstallationOptionsDirectory);
        Settings.ResetSettings();
    }

    public void Dispose()
    {
        Settings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        SecureSettings.TEST_SecureSettingsRootOverride = null;
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task GetAvailableUpdates_OffersTheNewestStableVersionWithinTheInstalledMajor()
    {
        var (manager, _) = CreateManager(new InstallOptions { OverridesNextLevelOpts = true, SkipMajorUpdates = true });

        IPackage update = Assert.Single(manager.GetAvailableUpdates());

        Assert.Equal("Contoso.Licensed", update.Id);
        Assert.Equal("5.2.0", update.VersionString);
        Assert.Equal("5.10.0", update.NewVersionString);
        Assert.True(update.OverridenOptions.PinUpdateToNewVersion);

        _ = new InstalledPackagesLoader([manager]);
        _ = new DiscoverablePackagesLoader([manager]);
        var loader = new TestUpgradablePackagesLoader([manager]);
        Assert.True(await loader.EvaluatePackageAsync(update));
    }

    [Fact]
    public void GetAvailableUpdates_IncludesPreReleasesOnlyWhenThePackageAllowsThem()
    {
        var (manager, _) = CreateManager(
            new InstallOptions { OverridesNextLevelOpts = true, SkipMajorUpdates = true, PreRelease = true }
        );

        Assert.Equal("5.11.0-beta", Assert.Single(manager.GetAvailableUpdates()).NewVersionString);
    }

    [Fact]
    public void GetAvailableUpdates_LeavesTheUpdateAloneWithoutSkipMajorUpdates()
    {
        int lookups = 0;
        var (manager, _) = CreateManager(
            new InstallOptions { OverridesNextLevelOpts = true, SkipMinorUpdates = true },
            onLookup: () => lookups++
        );

        IPackage update = Assert.Single(manager.GetAvailableUpdates());

        Assert.Equal("6.0.0", update.NewVersionString);
        Assert.False(update.OverridenOptions.PinUpdateToNewVersion);
        Assert.Equal(0, lookups);
    }

    [Fact]
    public void GetAvailableUpdates_LeavesTheUpdateAloneWhenTheManagerCannotUpdateToAVersion()
    {
        int lookups = 0;
        var (manager, _) = CreateManager(
            new InstallOptions { OverridesNextLevelOpts = true, SkipMajorUpdates = true },
            onLookup: () => lookups++,
            supportsVersionsOnUpdate: false
        );

        IPackage update = Assert.Single(manager.GetAvailableUpdates());

        Assert.Equal("6.0.0", update.NewVersionString);
        Assert.False(update.OverridenOptions.PinUpdateToNewVersion);
        Assert.Equal(0, lookups);
    }

    [Theory]
    [InlineData("5.10.0")]
    [InlineData("5.12.0")]
    public void GetAvailableUpdates_KeepsTheMajorUpdateWhenNothingNewerExistsWithinTheMajor(string installed)
    {
        var (manager, _) = CreateManager(
            new InstallOptions { OverridesNextLevelOpts = true, SkipMajorUpdates = true },
            installedVersion: installed
        );

        IPackage update = Assert.Single(manager.GetAvailableUpdates());

        Assert.Equal("6.0.0", update.NewVersionString);
        Assert.False(update.OverridenOptions.PinUpdateToNewVersion);
    }

    [Theory]
    [InlineData(true, true, "", "5.10.0", true)]
    [InlineData(false, true, "", "", false)]
    [InlineData(true, false, "", "", false)]
    [InlineData(true, true, "5.3.0", "5.3.0", true)]
    [InlineData(true, true, "6.0.0", "6.0.0", true)]
    public void UpdateOperation_PinsARowPinnedWithinTheMajorWhileMajorUpdatesAreSkipped(
        bool skipsMajorUpdates,
        bool pinnedWithinMajor,
        string requestedVersion,
        string expectedVersion,
        bool expectedExplicit
    )
    {
        var manager = new PackageManagerBuilder().Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Licensed")
            .WithVersion("5.2.0")
            .WithNewVersion("5.10.0")
            .Build();
        package.OverridenOptions.PinUpdateToNewVersion = pinnedWithinMajor;
        var options = new InstallOptions { Version = "4.0.0", SkipMajorUpdates = skipsMajorUpdates };
        if (requestedVersion.Length > 0)
        {
            options.Version = requestedVersion;
            options.VersionIsExplicit = true;
        }

        using var operation = new UpdatePackageOperation(package, options, IgnoreParallelInstalls: true);

        Assert.Equal(expectedVersion, operation.Options.Version);
        Assert.Equal(expectedExplicit, operation.Options.VersionIsExplicit);
    }

    [Theory]
    [InlineData(true, "5.10.0")]
    [InlineData(false, "")]
    public void StandaloneUpdateCommandsTargetThePinnedVersion(bool skipsMajorUpdates, string version)
    {
        string? built = null;
        var manager = new PackageManagerBuilder()
            .ConfigureOperation(helper =>
                helper.ParametersFactory = (_, options, _) =>
                {
                    built = options.Version;
                    return [];
                }
            )
            .Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Licensed")
            .WithVersion("5.2.0")
            .WithNewVersion("5.10.0")
            .Build();
        package.OverridenOptions.PinUpdateToNewVersion = true;

        manager.OperationHelper.GetStandaloneParameters(
            package,
            new InstallOptions { SkipMajorUpdates = skipsMajorUpdates },
            OperationType.Update
        );

        Assert.Equal(version, built);
    }

    [Fact]
    public async Task ResolveSkippedMajorUpdateAsync_ReevaluatesAPinnedRowWithTheCurrentOptions()
    {
        var (manager, _) = CreateManager(new InstallOptions { OverridesNextLevelOpts = true, SkipMajorUpdates = true });
        var loader = CreateLoaders(manager);
        var pinnedBeta = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Licensed")
            .WithVersion("5.2.0")
            .WithNewVersion("5.11.0-beta")
            .Build();
        pinnedBeta.OverridenOptions.PinUpdateToNewVersion = true;
        await loader.AddForeign(pinnedBeta);

        IPackage? resolved = await loader.ResolveSkippedMajorUpdateAsync(pinnedBeta);

        Assert.Equal("5.10.0", resolved?.NewVersionString);
        Assert.Equal("5.10.0", Assert.Single(loader.Packages).NewVersionString);
    }

    [Fact]
    public void HistoryRetryKeepsTheRecordedTargetWhileMajorUpdatesAreSkipped()
    {
        var manager = new PackageManagerBuilder().Build();
        var rebuilt = UpdateVersionPin.KeepRecordedTarget(
            new PackageBuilder()
                .WithManager(manager)
                .WithId("Contoso.Licensed")
                .WithVersion("5.2.0")
                .WithNewVersion("5.10.0")
                .Build(),
            new InstallOptions { SkipMajorUpdates = true, Version = "5.10.0" }
        );

        using var operation = new UpdatePackageOperation(
            rebuilt,
            new InstallOptions { SkipMajorUpdates = true, Version = "5.10.0" },
            IgnoreParallelInstalls: true
        );

        Assert.Equal("5.10.0", operation.Options.Version);
        Assert.True(operation.Options.VersionIsExplicit);
    }

    [Fact]
    public void GetAvailableUpdates_OffersNothingWithinTheMajorWhenAReleaseStatusIsUnknown()
    {
        var (manager, _) = CreateManager(new InstallOptions { OverridesNextLevelOpts = true, SkipMajorUpdates = true });
        manager.UpdateTargetEligibility = (_, version) => version == "5.10.0" ? null : true;

        IPackage update = Assert.Single(manager.GetAvailableUpdates());

        Assert.Equal("6.0.0", update.NewVersionString);
        Assert.False(update.OverridenOptions.PinUpdateToNewVersion);
    }

    [Fact]
    public void GetAvailableUpdates_ReusesEligibilityChecks()
    {
        int checks = 0;
        var (manager, _) = CreateManager(new InstallOptions { OverridesNextLevelOpts = true, SkipMajorUpdates = true });
        manager.UpdateTargetEligibility = (_, _) =>
        {
            checks++;
            return true;
        };

        manager.GetAvailableUpdates();
        manager.GetAvailableUpdates();

        Assert.Equal(1, checks);
    }

    [Fact]
    public void GetAvailableUpdates_SkipsWithdrawnReleasesWithinTheMajor()
    {
        var (manager, _) = CreateManager(new InstallOptions { OverridesNextLevelOpts = true, SkipMajorUpdates = true });
        manager.UpdateTargetEligibility = (_, version) => version != "5.10.0";

        Assert.Equal("5.3.1", Assert.Single(manager.GetAvailableUpdates()).NewVersionString);
    }

    [Fact]
    public async Task AMajorUpdateFromAnInstalledPreReleaseIsStillSkipped()
    {
        var (manager, reported) = CreateManager(
            new InstallOptions { OverridesNextLevelOpts = true, SkipMajorUpdates = true },
            installedVersion: "5.2.0rc1"
        );
        var loader = CreateLoaders(manager);

        Assert.True(reported.IsUpdateMajor());
        Assert.False(await loader.EvaluatePackageAsync(reported));
        Assert.Equal("5", CoreTools.MajorVersionOf("5.2.0rc1")?.ToString());
    }

    [Fact]
    public async Task ResolveSkippedMajorUpdateAsync_ReplacesTheRowWithTheUpdateWithinTheMajor()
    {
        var (manager, reported) = CreateManager(new InstallOptions { OverridesNextLevelOpts = true, SkipMajorUpdates = true });
        var loader = CreateLoaders(manager);
        await loader.AddForeign(reported);

        IPackage? resolved = await loader.ResolveSkippedMajorUpdateAsync(reported);

        Assert.Equal("5.10.0", resolved?.NewVersionString);
        Assert.Equal("5.10.0", Assert.Single(loader.Packages).NewVersionString);
    }

    [Fact]
    public async Task ResolveSkippedMajorUpdateAsync_AppliesTheLoadersFiltersToTheReplacement()
    {
        var (manager, reported) = CreateManager(new InstallOptions { OverridesNextLevelOpts = true, SkipMajorUpdates = true });
        var loader = CreateLoaders(manager);
        await loader.AddForeign(reported);
        await reported.AddToIgnoredUpdatesAsync("5.10.0");

        Assert.Null(await loader.ResolveSkippedMajorUpdateAsync(reported));
        Assert.Empty(loader.Packages);
    }

    [Fact]
    public async Task ResolveSkippedMajorUpdateAsync_DropsAPinnedRowOnceMajorUpdatesAreNoLongerSkipped()
    {
        var (manager, _) = CreateManager(new InstallOptions { OverridesNextLevelOpts = true, SkipMinorUpdates = true });
        var loader = CreateLoaders(manager);
        var pinned = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Licensed")
            .WithVersion("5.2.0")
            .WithNewVersion("5.10.0")
            .Build();
        pinned.OverridenOptions.PinUpdateToNewVersion = true;
        await loader.AddForeign(pinned);

        Assert.Same(pinned, await loader.ResolveSkippedMajorUpdateAsync(pinned));
        Assert.Empty(loader.Packages);
    }

    [Theory]
    [InlineData(PackageTag.OnQueue)]
    [InlineData(PackageTag.BeingProcessed)]
    public async Task ResolveSkippedMajorUpdateAsync_LeavesARowWithAPendingOperationAlone(PackageTag tag)
    {
        var (manager, _) = CreateManager(new InstallOptions { OverridesNextLevelOpts = true, SkipMajorUpdates = true });
        var loader = CreateLoaders(manager);
        var pinnedBeta = PinnedRow(manager, "5.11.0-beta");
        await loader.AddForeign(pinnedBeta);
        pinnedBeta.Tag = tag;

        Assert.Same(pinnedBeta, await loader.ResolveSkippedMajorUpdateAsync(pinnedBeta));
        Assert.Same(pinnedBeta, Assert.Single(loader.Packages));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ResolveSkippedMajorUpdateAsync_KeepsTheRowsSelection(bool isChecked)
    {
        var (manager, _) = CreateManager(new InstallOptions { OverridesNextLevelOpts = true, SkipMajorUpdates = true });
        var loader = CreateLoaders(manager);
        var pinnedBeta = PinnedRow(manager, "5.11.0-beta");
        await loader.AddForeign(pinnedBeta);
        pinnedBeta.IsChecked = isChecked;

        IPackage? resolved = await loader.ResolveSkippedMajorUpdateAsync(pinnedBeta);

        Assert.NotSame(pinnedBeta, resolved);
        Assert.Equal(isChecked, resolved!.IsChecked);
    }

    [Fact]
    public async Task ResolveSkippedMajorUpdateAsync_DropsAPinnedRowWhoseTargetNoLongerQualifies()
    {
        var (manager, _) = CreateManager(new InstallOptions { OverridesNextLevelOpts = true, SkipMajorUpdates = true });
        manager.UpdateTargetEligibility = (_, _) => false;
        var loader = CreateLoaders(manager);
        var pinnedBeta = PinnedRow(manager, "5.11.0-beta");
        await loader.AddForeign(pinnedBeta);

        Assert.Null(await loader.ResolveSkippedMajorUpdateAsync(pinnedBeta));
        Assert.Empty(loader.Packages);
    }

    [Fact]
    public void UpdateOperation_KeepsAnExplicitVersionWhenItReinstallsOnAManagerThatPinsThroughItsCommand()
    {
        var manager = new PackageManagerBuilder()
            .ConfigureCapabilities(capabilities =>
            {
                capabilities.SupportsCustomVersionsOnUpdate = false;
                return capabilities;
            })
            .Build();
        manager.CanUpdateToExactVersionOverride = true;
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Licensed")
            .WithVersion("5.2.0")
            .WithNewVersion("6.0.0")
            .Build();

        using var operation = new UpdatePackageOperation(
            package,
            new InstallOptions
            {
                SkipMajorUpdates = true,
                UninstallBeforeUpdate = true,
                Version = "5.3.0",
                VersionIsExplicit = true,
            },
            IgnoreParallelInstalls: true
        );

        Assert.Equal("5.3.0", operation.Options.Version);
    }

    [Fact]
    public async Task ResolveSkippedMajorUpdateAsync_KeepsAReplacementAnotherLauncherAlreadyQueued()
    {
        var (manager, _) = CreateManager(new InstallOptions { OverridesNextLevelOpts = true, SkipMajorUpdates = true });
        var loader = CreateLoaders(manager);
        var staleRow = PinnedRow(manager, "5.11.0-beta");
        await loader.AddForeign(staleRow);

        IPackage? first = await loader.ResolveSkippedMajorUpdateAsync(staleRow);
        first!.Tag = PackageTag.OnQueue;
        IPackage? second = await loader.ResolveSkippedMajorUpdateAsync(staleRow);

        Assert.Same(first, second);
        Assert.Same(first, Assert.Single(loader.Packages));
    }

    [Fact]
    public void GetAvailableUpdates_KeepsTheNeedToReinstall()
    {
        var (manager, _) = CreateManager(
            new InstallOptions { OverridesNextLevelOpts = true, SkipMajorUpdates = true },
            requiresReinstallToUpdate: true
        );

        IPackage update = Assert.Single(manager.GetAvailableUpdates());

        Assert.Equal("5.10.0", update.NewVersionString);
        Assert.True(update.RequiresReinstallToUpdate);
    }

    [Fact]
    public async Task ResolveSkippedMajorUpdateAsync_UsesTheOptionsItIsGiven()
    {
        var (manager, _) = CreateManager(
            new InstallOptions { OverridesNextLevelOpts = true, SkipMajorUpdates = true, PreRelease = true }
        );
        var loader = CreateLoaders(manager);
        var pinnedBeta = PinnedRow(manager, "5.11.0-beta");
        await loader.AddForeign(pinnedBeta);

        IPackage? resolved = await loader.ResolveSkippedMajorUpdateAsync(
            pinnedBeta,
            new InstallOptions { SkipMajorUpdates = true, PreRelease = false }
        );

        Assert.Equal("5.10.0", resolved?.NewVersionString);
    }

    [Fact]
    public void GetAvailableUpdates_ListsVersionsAgainWhenPreReleasesAreToggled()
    {
        int lookups = 0;
        var (manager, reported) = CreateManager(
            new InstallOptions { OverridesNextLevelOpts = true, SkipMajorUpdates = true },
            onLookup: () => lookups++
        );

        manager.GetAvailableUpdates();
        InstallOptionsFactory.SaveForPackage(
            new InstallOptions { OverridesNextLevelOpts = true, SkipMajorUpdates = true, PreRelease = true },
            reported
        );
        IPackage update = Assert.Single(manager.GetAvailableUpdates());

        Assert.Equal(2, lookups);
        Assert.Equal("5.11.0-beta", update.NewVersionString);
    }

    private static Package PinnedRow(TestPackageManager manager, string newVersion)
    {
        var row = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Licensed")
            .WithVersion("5.2.0")
            .WithNewVersion(newVersion)
            .Build();
        row.OverridenOptions.PinUpdateToNewVersion = true;
        return row;
    }

    [Fact]
    public async Task ResolveSkippedMajorUpdateAsync_LeavesOtherUpdatesAlone()
    {
        var (manager, _) = CreateManager(new InstallOptions { OverridesNextLevelOpts = true, SkipMajorUpdates = true });
        var loader = CreateLoaders(manager);
        var minor = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Licensed")
            .WithVersion("5.2.0")
            .WithNewVersion("5.3.1")
            .Build();
        await loader.AddForeign(minor);

        Assert.Same(minor, await loader.ResolveSkippedMajorUpdateAsync(minor));
        Assert.Single(loader.Packages);
    }

    [Fact]
    public void GetAvailableUpdates_KeepsSkippingTheMajorUpdateWhenTheVersionListTimesOut()
    {
        var original = PackageManager.VersionLookupTimeout;
        PackageManager.VersionLookupTimeout = TimeSpan.FromMilliseconds(200);
        try
        {
            int lookups = 0;
            var (manager, _) = CreateManager(
                new InstallOptions { OverridesNextLevelOpts = true, SkipMajorUpdates = true },
                onLookup: () =>
                {
                    Interlocked.Increment(ref lookups);
                    Thread.Sleep(TimeSpan.FromSeconds(5));
                }
            );

            var timer = System.Diagnostics.Stopwatch.StartNew();
            IPackage update = Assert.Single(manager.GetAvailableUpdates());

            Assert.Equal("6.0.0", update.NewVersionString);
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(4));
            Assert.Equal(1, lookups);

            manager.GetAvailableUpdates();
            Assert.Equal(1, lookups);
        }
        finally
        {
            PackageManager.VersionLookupTimeout = original;
        }
    }

    [Fact]
    public void GetAvailableUpdates_ReusesTheVersionListUntilTheReportedVersionChanges()
    {
        int lookups = 0;
        string reportedVersion = "6.0.0";
        TestPackageManager? manager = null;
        manager = new PackageManagerBuilder()
            .ConfigureDetails(details =>
                details.VersionsFactory = _ =>
                {
                    lookups++;
                    return Feed;
                }
            )
            .WithAvailableUpdates(m =>
                [
                    new PackageBuilder()
                        .WithManager(m)
                        .WithId("Contoso.Licensed")
                        .WithVersion("5.2.0")
                        .WithNewVersion(reportedVersion)
                        .Build(),
                ]
            )
            .Build();
        InstallOptionsFactory.SaveForPackage(
            new InstallOptions { OverridesNextLevelOpts = true, SkipMajorUpdates = true },
            new PackageBuilder().WithManager(manager).WithId("Contoso.Licensed").WithVersion("5.2.0").Build()
        );

        manager.GetAvailableUpdates();
        manager.GetAvailableUpdates();
        Assert.Equal(1, lookups);

        reportedVersion = "6.1.0";
        Assert.Equal("5.10.0", Assert.Single(manager.GetAvailableUpdates()).NewVersionString);
        Assert.Equal(2, lookups);
    }

    [Theory]
    [InlineData("5.12.0rc1", true)]
    [InlineData("5.11.0-beta", true)]
    [InlineData("2.0.0-preview.3", true)]
    [InlineData("1.0a1", true)]
    [InlineData("3.1.0.dev4", true)]
    [InlineData("v2.0.0-1", true)]
    [InlineData("5.10.0", false)]
    [InlineData("1.1.1w", false)]
    [InlineData("1.0.post1", false)]
    [InlineData("2024a", false)]
    [InlineData("v5.3", false)]
    public void PreReleaseVersion_RecognizesPreReleaseMarkersOnly(string version, bool preRelease)
    {
        Assert.Equal(preRelease, PreReleaseVersion.IsPreRelease(version));
    }

    [Fact]
    public void UpdateOperation_LeavesAnUnpinnedUpdateUnversioned()
    {
        var manager = new PackageManagerBuilder().Build();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Licensed")
            .WithVersion("5.2.0")
            .WithNewVersion("6.0.0")
            .Build();

        using var operation = new UpdatePackageOperation(package, new InstallOptions(), IgnoreParallelInstalls: true);

        Assert.Equal("", operation.Options.Version);
    }

    private static TestUpgradablePackagesLoader CreateLoaders(TestPackageManager manager)
    {
        _ = new InstalledPackagesLoader([manager]);
        _ = new DiscoverablePackagesLoader([manager]);
        return new TestUpgradablePackagesLoader([manager]);
    }

    private static (TestPackageManager Manager, Package Reported) CreateManager(
        InstallOptions options,
        Action? onLookup = null,
        bool supportsVersionsOnUpdate = true,
        string installedVersion = "5.2.0",
        bool requiresReinstallToUpdate = false
    )
    {
        Package? reported = null;
        var manager = new PackageManagerBuilder()
            .ConfigureCapabilities(capabilities =>
            {
                capabilities.SupportsCustomVersionsOnUpdate = supportsVersionsOnUpdate;
                return capabilities;
            })
            .ConfigureDetails(details =>
                details.VersionsFactory = _ =>
                {
                    onLookup?.Invoke();
                    return Feed;
                }
            )
            .WithAvailableUpdates(m => [reported!])
            .Build();

        var builder = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Licensed")
            .WithVersion(installedVersion)
            .WithNewVersion("6.0.0");
        if (requiresReinstallToUpdate)
            builder = builder.RequiringReinstallToUpdate();
        reported = builder.Build();
        InstallOptionsFactory.SaveForPackage(options, reported);
        return (manager, reported);
    }
}
