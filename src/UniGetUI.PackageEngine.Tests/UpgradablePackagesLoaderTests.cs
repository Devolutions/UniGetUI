using System.Collections.Concurrent;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Structs;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

namespace UniGetUI.PackageEngine.Tests;

public sealed class UpgradablePackagesLoaderTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        nameof(UpgradablePackagesLoaderTests),
        Guid.NewGuid().ToString("N")
    );

    public UpgradablePackagesLoaderTests()
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
    public async Task EvaluatePackageAsync_SkipsMinorUpdatesWhenConfigured()
    {
        var manager = new PackageManagerBuilder().Build();
        _ = new InstalledPackagesLoader([manager]);
        _ = new DiscoverablePackagesLoader([manager]);
        var loader = new TestUpgradablePackagesLoader([manager]);
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("1.0.1")
            .Build();

        InstallOptionsFactory.SaveForPackage(
            new InstallOptions
            {
                OverridesNextLevelOpts = true,
                SkipMinorUpdates = true,
            },
            package
        );

        Assert.False(await loader.EvaluatePackageAsync(package));
    }

    [Theory]
    [InlineData("5.2.0", "5.3.1", true)]
    [InlineData("5.2.0", "5.2.1", true)]
    [InlineData("5.2.0", "6.0.0", false)]
    [InlineData("v5.2", "v12.0", false)]
    [InlineData("5.2.0.1", "5.9.0.0", true)]
    [InlineData("Unknown", "6.0.0", true)]
    [InlineData("5.2.0", "latest", true)]
    public async Task EvaluatePackageAsync_SkipMajorUpdatesHidesOnlyMajorUpdates(
        string installedVersion,
        string newVersion,
        bool offered
    )
    {
        var manager = new PackageManagerBuilder().Build();
        _ = new InstalledPackagesLoader([manager]);
        _ = new DiscoverablePackagesLoader([manager]);
        var loader = new TestUpgradablePackagesLoader([manager]);
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Licensed")
            .WithVersion(installedVersion)
            .WithNewVersion(newVersion)
            .Build();

        InstallOptionsFactory.SaveForPackage(
            new InstallOptions { OverridesNextLevelOpts = true, SkipMajorUpdates = true },
            package
        );

        Assert.Equal(offered, await loader.EvaluatePackageAsync(package));
    }

    [Fact]
    public async Task EvaluatePackageAsync_OffersMajorUpdatesUnlessSkipMajorIsSet()
    {
        var manager = new PackageManagerBuilder().Build();
        _ = new InstalledPackagesLoader([manager]);
        _ = new DiscoverablePackagesLoader([manager]);
        var loader = new TestUpgradablePackagesLoader([manager]);
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Licensed")
            .WithVersion("5.2.0")
            .WithNewVersion("6.0.0")
            .Build();

        InstallOptionsFactory.SaveForPackage(
            new InstallOptions { OverridesNextLevelOpts = true, SkipMinorUpdates = true, SkipMinorUpdatesLevel = 2 },
            package
        );

        Assert.True(await loader.EvaluatePackageAsync(package));
    }

    [Fact]
    public async Task EvaluatePackageAsync_RespectsSkipMinorLevel()
    {
        var manager = new PackageManagerBuilder().Build();
        _ = new InstalledPackagesLoader([manager]);
        _ = new DiscoverablePackagesLoader([manager]);
        var loader = new TestUpgradablePackagesLoader([manager]);

        // A 3rd-position (patch) change on a four-part version.
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.FourPart")
            .WithVersion("3.4.5.6")
            .WithNewVersion("3.4.6.6")
            .Build();

        // Level 4 => only 4th-position changes count as minor, so this patch change must NOT be skipped.
        InstallOptionsFactory.SaveForPackage(
            new InstallOptions
            {
                OverridesNextLevelOpts = true,
                SkipMinorUpdates = true,
                SkipMinorUpdatesLevel = 4,
            },
            package
        );
        Assert.True(await loader.EvaluatePackageAsync(package));

        // Default level (3) => a 3rd-position change is minor and must be skipped.
        InstallOptionsFactory.SaveForPackage(
            new InstallOptions
            {
                OverridesNextLevelOpts = true,
                SkipMinorUpdates = true,
                SkipMinorUpdatesLevel = InstallOptions.DefaultSkipMinorLevel,
            },
            package
        );
        Assert.False(await loader.EvaluatePackageAsync(package));
    }

    [Fact]
    public async Task EvaluatePackageAsync_SkipsIgnoredAndSupersededPackages()
    {
        var manager = new PackageManagerBuilder().Build();
        var installedLoader = new InstalledPackagesLoader([manager]);
        _ = new DiscoverablePackagesLoader([manager]);
        var loader = new TestUpgradablePackagesLoader([manager]);
        var ignoredPackage = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Ignored")
            .WithVersion("1.0.0")
            .WithNewVersion("2.0.0")
            .Build();
        var supersededPackage = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Superseded")
            .WithVersion("1.0.0")
            .WithNewVersion("2.0.0")
            .Build();

        await ignoredPackage.AddToIgnoredUpdatesAsync("2.0.0");
        await installedLoader.AddForeign(
            new PackageBuilder()
                .WithManager(manager)
                .WithId("Contoso.Superseded")
                .WithVersion("3.0.0")
                .Build()
        );

        Assert.False(await loader.EvaluatePackageAsync(ignoredPackage));
        Assert.Contains("Contoso.Ignored", loader.IgnoredPackages.Keys);
        Assert.False(await loader.EvaluatePackageAsync(supersededPackage));
    }

    // Regression test for issue #5293: the installed version carried a Homebrew build revision
    // ("18.4_1"), which the version parser read as 18.41. That compared as greater than the
    // available 18.6, so the loader discarded the update and it never reached the UI.
    [Fact]
    public async Task EvaluatePackageAsync_KeepsUpdateWhenInstalledVersionHasBuildRevision()
    {
        var manager = new PackageManagerBuilder().Build();
        var installedLoader = new InstalledPackagesLoader([manager]);
        _ = new DiscoverablePackagesLoader([manager]);
        var loader = new TestUpgradablePackagesLoader([manager]);

        await installedLoader.AddForeign(
            new PackageBuilder()
                .WithManager(manager)
                .WithId("postgresql@18")
                .WithVersion("18.4_1")
                .Build()
        );

        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("postgresql@18")
            .WithVersion("18.4_1")
            .WithNewVersion("18.6")
            .Build();

        Assert.True(await loader.EvaluatePackageAsync(package));
    }

    [Fact]
    public async Task EvaluatePackageAsync_RejectsOlderOfferedVersionWithoutTheInstalledList()
    {
        var manager = new PackageManagerBuilder().Build();
        _ = new InstalledPackagesLoader([manager]);
        _ = new DiscoverablePackagesLoader([manager]);
        var loader = new TestUpgradablePackagesLoader([manager]);

        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Daum.PotPlayer")
            .WithVersion("260819")
            .WithNewVersion("210428")
            .Build();

        Assert.False(await loader.EvaluatePackageAsync(package));
    }

    [Theory]
    [InlineData("stable")]
    [InlineData("latest")]
    [InlineData("2.0.0_rc1")]
    public async Task EvaluatePackageAsync_KeepsUpdatesWhoseOfferedVersionCannotBeParsed(string newVersion)
    {
        var manager = new PackageManagerBuilder().Build();
        _ = new InstalledPackagesLoader([manager]);
        _ = new DiscoverablePackagesLoader([manager]);
        var loader = new TestUpgradablePackagesLoader([manager]);

        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("org.contoso.Flatpak")
            .WithVersion("3.2.1")
            .WithNewVersion(newVersion)
            .Build();

        Assert.True(await loader.EvaluatePackageAsync(package));
    }

    [Fact]
    public async Task EvaluatePackageAsync_KeepsUpdatesWhenTheInstalledVersionIsUnknown()
    {
        var manager = new PackageManagerBuilder().Build();
        _ = new InstalledPackagesLoader([manager]);
        _ = new DiscoverablePackagesLoader([manager]);
        var loader = new TestUpgradablePackagesLoader([manager]);

        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Unversioned")
            .WithVersion("Unknown")
            .WithNewVersion("210428")
            .Build();

        Assert.True(await loader.EvaluatePackageAsync(package));
    }

    [Fact]
    public async Task ApplyWhenAddingPackageAsync_UpdatesDiscoverableAndInstalledTags()
    {
        var manager = new PackageManagerBuilder().Build();
        var installedLoader = new InstalledPackagesLoader([manager]);
        var discoverableLoader = new DiscoverablePackagesLoader([manager]);
        var loader = new TestUpgradablePackagesLoader([manager]);
        var availablePackage = new PackageBuilder().WithManager(manager).WithId("Contoso.Tagged").Build();
        var installedPackage = new PackageBuilder().WithManager(manager).WithId("Contoso.Tagged").Build();
        var upgradablePackage = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tagged")
            .WithVersion("1.0.0")
            .WithNewVersion("2.0.0")
            .Build();

        await discoverableLoader.AddForeign(availablePackage);
        await installedLoader.AddForeign(installedPackage);

        await loader.ApplyWhenAddingPackageAsync(upgradablePackage);

        Assert.Equal(PackageTag.IsUpgradable, availablePackage.Tag);
        Assert.Equal(PackageTag.IsUpgradable, installedPackage.Tag);
    }

    [Fact]
    public void GetAvailableUpdate_MatchesTheIgnoredIdOfTheSameManagerOnly()
    {
        var manager = new PackageManagerBuilder().WithName("Alpha").Build();
        var otherManager = new PackageManagerBuilder().WithName("Beta").Build();
        _ = new InstalledPackagesLoader([manager, otherManager]);
        var loader = new TestUpgradablePackagesLoader([manager, otherManager]);
        var update = new PackageBuilder()
            .WithManager(manager)
            .WithId("MSIX\\Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("2.0.0")
            .Build();
        loader.IgnoredPackages[update.Id] = update;

        Assert.Same(update, IgnoredUpdatesDatabase.GetAvailableUpdate("alpha\\MSIX\\Contoso.Tool"));
        Assert.Null(IgnoredUpdatesDatabase.GetAvailableUpdate("beta\\MSIX\\Contoso.Tool"));
        Assert.Null(IgnoredUpdatesDatabase.GetAvailableUpdate("alpha\\Contoso.Tool"));
    }

    [Fact]
    public void GetAvailableUpdate_IgnoresCachedEntriesWithoutANewVersion()
    {
        var manager = new PackageManagerBuilder().WithName("Alpha").Build();
        _ = new InstalledPackagesLoader([manager]);
        var loader = new TestUpgradablePackagesLoader([manager]);
        var current = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("2.0.0")
            .WithNewVersion("2.0.0")
            .Build();
        loader.IgnoredPackages[current.Id] = current;

        Assert.Null(IgnoredUpdatesDatabase.GetAvailableUpdate("alpha\\Contoso.Tool"));
    }

    [Fact]
    public async Task GetAvailableUpdate_IgnoresUpdatesAlreadyInstalledElsewhere()
    {
        var manager = new PackageManagerBuilder().WithName("Alpha").Build();
        var installedLoader = new InstalledPackagesLoader([manager]);
        var loader = new TestUpgradablePackagesLoader([manager]);
        var update = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("2.0.0")
            .Build();
        loader.IgnoredPackages[update.Id] = update;
        await installedLoader.AddForeign(
            new PackageBuilder().WithManager(manager).WithId("Contoso.Tool").WithVersion("2.0.0").Build()
        );

        Assert.Null(IgnoredUpdatesDatabase.GetAvailableUpdate("alpha\\Contoso.Tool"));
    }

    [Theory]
    [InlineData("2.0.0", false)]
    [InlineData("2.1.0", false)]
    [InlineData("1.5.0", true)]
    public void ForgetAvailableUpdate_KeepsTheUpdateOnlyWhileItIsStillNewer(string installedVersion, bool kept)
    {
        var manager = new PackageManagerBuilder().WithName("Alpha").Build();
        var loader = new TestUpgradablePackagesLoader([manager]);
        var update = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("2.0.0")
            .Build();
        loader.IgnoredPackages[update.Id] = update;

        IgnoredUpdatesDatabase.ForgetAvailableUpdate(update, installedVersion);

        Assert.Equal(kept, loader.IgnoredPackages.ContainsKey(update.Id));
    }

    [Fact]
    public void ForgetAvailableUpdate_LeavesAnotherManagersUpdateAlone()
    {
        var manager = new PackageManagerBuilder().WithName("Alpha").Build();
        var otherManager = new PackageManagerBuilder().WithName("Beta").Build();
        var loader = new TestUpgradablePackagesLoader([manager, otherManager]);
        var otherUpdate = new PackageBuilder()
            .WithManager(otherManager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("2.0.0")
            .Build();
        var updated = new PackageBuilder()
            .WithManager(manager)
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithNewVersion("2.0.0")
            .Build();
        loader.IgnoredPackages[otherUpdate.Id] = otherUpdate;

        IgnoredUpdatesDatabase.ForgetAvailableUpdate(updated, "2.0.0");

        Assert.Same(otherUpdate, loader.IgnoredPackages[otherUpdate.Id]);
    }

    [Fact]
    public async Task ReloadPackages_ChecksEachManagersUpdatesOnlyAfterItsInstalledListing()
    {
        Settings.Set(Settings.K.DisableWaitForInternetConnection, true);
        var calls = new ConcurrentQueue<string>();
        using var slowGate = new ManualResetEventSlim(false);
        var slow = new PackageManagerBuilder()
            .WithName("Slow")
            .WithInstalledPackages(_ =>
            {
                slowGate.Wait(TimeSpan.FromSeconds(30));
                calls.Enqueue("Slow:installed");
                return [];
            })
            .WithAvailableUpdates(_ =>
            {
                calls.Enqueue("Slow:updates");
                return [];
            })
            .Build();
        var fast = new PackageManagerBuilder()
            .WithName("Fast")
            .WithInstalledPackages(_ =>
            {
                calls.Enqueue("Fast:installed");
                return [];
            })
            .WithAvailableUpdates(_ =>
            {
                calls.Enqueue("Fast:updates");
                return [];
            })
            .Build();
        var installedLoader = new InstalledPackagesLoader([slow, fast]);
        _ = new DiscoverablePackagesLoader([slow, fast]);
        var updatesLoader = new TestUpgradablePackagesLoader([slow, fast]);

        Task installedLoad;
        Task updatesLoad;
        try
        {
            installedLoad = installedLoader.ReloadPackages();
            updatesLoad = updatesLoader.ReloadPackages();

            await WaitUntilAsync(() => calls.Contains("Fast:updates"));
            Assert.DoesNotContain("Slow:updates", calls);
        }
        finally
        {
            slowGate.Set();
        }

        await Task.WhenAll(installedLoad, updatesLoad).WaitAsync(TimeSpan.FromSeconds(30));

        string[] order = [.. calls];
        Assert.True(Array.IndexOf(order, "Fast:installed") < Array.IndexOf(order, "Fast:updates"));
        Assert.True(Array.IndexOf(order, "Slow:installed") < Array.IndexOf(order, "Slow:updates"));
    }

    [Fact]
    public async Task ReloadPackages_ChecksUpdatesAnywayWhenTheInstalledListingOutlastsTheWaitLimit()
    {
        Settings.Set(Settings.K.DisableWaitForInternetConnection, true);
        int updateChecks = 0;
        using var installedGate = new ManualResetEventSlim(false);
        var manager = new PackageManagerBuilder()
            .WithInstalledPackages(_ =>
            {
                installedGate.Wait(TimeSpan.FromSeconds(30));
                return [];
            })
            .WithAvailableUpdates(_ =>
            {
                Interlocked.Increment(ref updateChecks);
                return [];
            })
            .Build();
        var installedLoader = new InstalledPackagesLoader([manager]);
        _ = new DiscoverablePackagesLoader([manager]);
        var updatesLoader = new TestUpgradablePackagesLoader([manager])
        {
            WaitLimit = TimeSpan.FromMilliseconds(200),
        };

        Task installedLoad;
        try
        {
            installedLoad = installedLoader.ReloadPackages();
            await updatesLoader.ReloadPackages().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(installedLoader.IsLoading);
            Assert.Equal(1, updateChecks);
            Assert.True(updatesLoader.IsLoaded);
        }
        finally
        {
            installedGate.Set();
        }

        await installedLoad.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "The condition was not met in time");
            await Task.Delay(20);
        }
    }
}
