using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

namespace UniGetUI.PackageEngine.Tests;

public sealed class DiscoverablePackagesLoaderTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        nameof(DiscoverablePackagesLoaderTests),
        Guid.NewGuid().ToString("N")
    );

    public DiscoverablePackagesLoaderTests()
    {
        Directory.CreateDirectory(_testRoot);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        SecureSettings.TEST_SecureSettingsRootOverride = Path.Combine(_testRoot, "SecureSettings");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Directory.CreateDirectory(CoreData.UniGetUIInstallationOptionsDirectory);
        Settings.ResetSettings();
        Settings.Set(Settings.K.DisableWaitForInternetConnection, true);
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

    private static string SearchScopeFile => Path.Combine(
        CoreData.UniGetUIUserConfigurationDirectory, "ExcludedSearchManagers.json");

    private sealed class ProbeLoader(IReadOnlyList<IPackageManager> managers)
        : DiscoverablePackagesLoader(managers)
    {
        public bool WillQuery() => WillQueryAnyManager();
    }

    [Fact]
    public void ConnectivityWaitIsSkippedOnlyWhenNothingIsSelected()
    {
        var alpha = new PackageManagerBuilder().WithName("Alpha").WithDisplayName("Alpha").Build();
        var beta = new PackageManagerBuilder().WithName("Beta").WithDisplayName("Beta").Build();
        var loader = new ProbeLoader([alpha, beta]);

        Assert.True(loader.WillQuery());

        loader.SetManagerSearched(alpha, false);
        Assert.True(loader.WillQuery());

        loader.SetManagerSearched(beta, false);
        Assert.False(loader.WillQuery());

        loader.SetManagerSearched(alpha, true);
        Assert.True(loader.WillQuery());
    }

    [Fact]
    public async Task ExcludedManagersAreNeverQueried()
    {
        int alphaCalls = 0;
        int betaCalls = 0;

        var alpha = new PackageManagerBuilder()
            .WithName("Alpha")
            .WithDisplayName("Alpha")
            .WithFindPackages((m, _) =>
            {
                alphaCalls++;
                return [new PackageBuilder().WithManager(m).WithId("Alpha.Pkg").Build()];
            })
            .Build();

        var beta = new PackageManagerBuilder()
            .WithName("Beta")
            .WithDisplayName("Beta")
            .WithFindPackages((m, _) =>
            {
                betaCalls++;
                return [new PackageBuilder().WithManager(m).WithId("Beta.Pkg").Build()];
            })
            .Build();

        _ = new InstalledPackagesLoader([alpha, beta]);
        _ = new UpgradablePackagesLoader([alpha, beta]);
        var loader = new DiscoverablePackagesLoader([alpha, beta]);

        Assert.True(loader.IsManagerSearched(alpha));
        Assert.True(loader.IsManagerSearched(beta));

        await loader.ReloadPackages("tool");
        Assert.Equal(1, alphaCalls);
        Assert.Equal(1, betaCalls);
        Assert.Equal(2, loader.Packages.Count);

        loader.SetManagerSearched(beta, false);
        await loader.ReloadPackages("tool");
        Assert.Equal(2, alphaCalls);
        Assert.Equal(1, betaCalls);
        Assert.Equal("Alpha.Pkg", Assert.Single(loader.Packages).Id);

        loader.SetManagerSearched(beta, true);
        await loader.ReloadPackages("tool");
        Assert.Equal(3, alphaCalls);
        Assert.Equal(2, betaCalls);
        Assert.Equal(2, loader.Packages.Count);
    }

    [Fact]
    public async Task NoSelectedManagerYieldsNoPackagesWithoutQuerying()
    {
        int alphaCalls = 0;
        var alpha = new PackageManagerBuilder()
            .WithName("Alpha")
            .WithDisplayName("Alpha")
            .WithFindPackages((m, _) =>
            {
                alphaCalls++;
                return [new PackageBuilder().WithManager(m).WithId("Alpha.Pkg").Build()];
            })
            .Build();

        _ = new InstalledPackagesLoader([alpha]);
        _ = new UpgradablePackagesLoader([alpha]);
        var loader = new DiscoverablePackagesLoader([alpha]);

        loader.SetManagerSearched(alpha, false);
        await loader.ReloadPackages("tool");

        Assert.Equal(0, alphaCalls);
        Assert.Empty(loader.Packages);
        Assert.True(loader.IsLoaded);
        Assert.False(loader.IsLoading);
    }

    [Fact]
    public void SearchScopeIsReadFromAndWrittenToDisk()
    {
        var alpha = new PackageManagerBuilder().WithName("Alpha").WithDisplayName("Alpha").Build();
        var beta = new PackageManagerBuilder().WithName("Beta").WithDisplayName("Beta").Build();

        File.WriteAllText(SearchScopeFile, "{\"Beta\":true}");

        var loader = new DiscoverablePackagesLoader([alpha, beta]);
        Assert.True(loader.IsManagerSearched(alpha));
        Assert.False(loader.IsManagerSearched(beta));

        loader.SetManagerSearched(alpha, false);
        loader.SetManagerSearched(beta, true);

        Assert.Equal(
            new Dictionary<string, bool> { ["Alpha"] = true, ["Beta"] = false },
            System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, bool>>(
                File.ReadAllText(SearchScopeFile)));
    }
}
