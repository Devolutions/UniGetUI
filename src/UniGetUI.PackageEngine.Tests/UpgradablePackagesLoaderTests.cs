using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Interface.Enums;
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

    [Theory]
    [InlineData("120", 120000)]
    [InlineData("not-a-number", 3600000)]
    public void StartTimer_UsesConfiguredIntervalOrDefault(string configuredValue, double expectedInterval)
    {
        var manager = new PackageManagerBuilder().Build();
        _ = new InstalledPackagesLoader([manager]);
        _ = new DiscoverablePackagesLoader([manager]);
        var loader = new TestUpgradablePackagesLoader([manager]);
        Settings.Set(Settings.K.DisableAutoCheckforUpdates, false);
        Settings.SetValue(Settings.K.UpdatesCheckInterval, configuredValue);

        loader.StartTimer();

        Assert.Equal(expectedInterval, loader.GetTimerIntervalMilliseconds());
    }
}
