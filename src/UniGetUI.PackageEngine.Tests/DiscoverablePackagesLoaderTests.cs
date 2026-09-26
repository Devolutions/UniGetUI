using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

namespace UniGetUI.PackageEngine.Tests;

/// <summary>
/// Searches on the Discover page's loader, and browsing: an empty search, which lists every
/// package of the managers that can list them all.
/// </summary>
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
        Settings.ResetSettings();
        Settings.Set(Settings.K.DisableWaitForInternetConnection, true);
    }

    public void Dispose()
    {
        Settings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        SecureSettings.TEST_SecureSettingsRootOverride = null;
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    /// <summary>A manager that finds <paramref name="packageCount"/> packages for any query, recording each query.</summary>
    private static TestPackageManager CreateManager(
        string name,
        bool canListAllPackages,
        int packageCount,
        List<string> queries
    ) =>
        new PackageManagerBuilder()
            .WithName(name)
            .ConfigureCapabilities(capabilities =>
            {
                capabilities.CanListAllPackages = canListAllPackages;
                return capabilities;
            })
            .WithFindPackages(
                (manager, query) =>
                {
                    lock (queries)
                        queries.Add(query);

                    return Enumerable
                        .Range(0, packageCount)
                        .Select(index => new PackageBuilder().WithManager(manager).WithId($"{name}.Package{index}").Build())
                        .ToList();
                }
            )
            .Build();

    private static DiscoverablePackagesLoader CreateLoader(params IPackageManager[] managers)
    {
        // Packages are tagged against the installed and upgradable ones as they are added
        _ = new InstalledPackagesLoader(managers);
        _ = new UpgradablePackagesLoader(managers);
        return new DiscoverablePackagesLoader(managers);
    }

    [Fact]
    public async Task AnEmptySearchListsEveryPackageOfTheManagersThatCanListThemAll()
    {
        List<string> listableQueries = [];
        List<string> otherQueries = [];
        var listable = CreateManager("Listable", canListAllPackages: true, packageCount: 150, listableQueries);
        var other = CreateManager("Other", canListAllPackages: false, packageCount: 5, otherQueries);
        var loader = CreateLoader(listable, other);

        await loader.ReloadPackages("");

        // All of them, past the cap that guards broad queries across vast catalogs
        Assert.Equal(150, loader.Packages.Count);
        Assert.All(loader.Packages, package => Assert.Same(listable, package.Manager));
        Assert.Equal([""], listableQueries);
        Assert.Empty(otherQueries);
    }

    [Fact]
    public async Task ASearchStillCapsTheResultsOfEachManager()
    {
        List<string> queries = [];
        var loader = CreateLoader(CreateManager("Listable", canListAllPackages: true, packageCount: 150, queries));

        await loader.ReloadPackages("tool");

        Assert.Equal(100, loader.Packages.Count);
        Assert.Equal(["tool"], queries);
    }

    [Fact]
    public async Task NothingIsLoadedBeforeTheFirstSearch()
    {
        List<string> queries = [];
        var loader = CreateLoader(CreateManager("Listable", canListAllPackages: true, packageCount: 3, queries));

        await loader.ReloadPackages();

        Assert.Empty(loader.Packages);
        Assert.Empty(queries);
    }

    [Fact]
    public void OnlyManagersThatCanListAllTheirPackagesHaveSourcesToBrowse()
    {
        static TestPackageManager Create(bool canListAllPackages) =>
            new PackageManagerBuilder()
                .ConfigureCapabilities(capabilities =>
                {
                    capabilities.CanListAllPackages = canListAllPackages;
                    return capabilities;
                })
                .WithSources(manager =>
                [
                    new SourceBuilder().WithManager(manager).WithName("community").WithUrl("https://example.test/community").Build(),
                ])
                .Build();

        var listable = Create(canListAllPackages: true);
        var other = Create(canListAllPackages: false);
        listable.SourcesHelper.GetSources();
        other.SourcesHelper.GetSources();

        Assert.Equal(["community"], listable.GetBrowsableSources().Select(source => source.Name));
        Assert.Empty(other.GetBrowsableSources());
    }

    [Fact]
    public async Task AQueryOfOnlyUnsafeCharactersDoesNotBrowse()
    {
        List<string> queries = [];
        var loader = CreateLoader(CreateManager("Listable", canListAllPackages: true, packageCount: 3, queries));

        await loader.ReloadPackages(";|&");

        Assert.Empty(loader.Packages);
        Assert.Empty(queries);
    }
}
