#if WINDOWS
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.WingetManager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Tests.Infrastructure.Assertions;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

namespace UniGetUI.PackageEngine.Tests;

[CollectionDefinition("WinGet manager tests", DisableParallelization = true)]
public sealed class WinGetManagerTestCollection
{
    public const string Name = "WinGet manager tests";
}

[Collection(WinGetManagerTestCollection.Name)]
public sealed class WinGetManagerTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        AppContext.BaseDirectory,
        "WinGetManagerTests",
        Guid.NewGuid().ToString("N")
    );

    public WinGetManagerTests()
    {
        Directory.CreateDirectory(_testRoot);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        SetNoPackagesHaveBeenLoaded(false);
        Settings.Set(Settings.K.EnableProxy, false);
        Settings.Set(Settings.K.EnableProxyAuth, false);
        Settings.SetValue(Settings.K.ProxyURL, "");
    }

    public void Dispose()
    {
        SetNoPackagesHaveBeenLoaded(false);
        WinGetHelper.Instance = null!;
        CoreData.TEST_DataDirectoryOverride = null;
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    [Fact]
    public void GetProxyArgumentReturnsEmptyStringWhenProxyIsDisabled()
    {
        Settings.Set(Settings.K.EnableProxy, false);
        Settings.SetValue(Settings.K.ProxyURL, "http://proxy.example.test:3128/");

        Assert.Equal("", WinGet.GetProxyArgument());
    }

    [Fact]
    public void GetProxyArgumentReturnsTrimmedProxyArgumentWhenProxyIsEnabled()
    {
        Settings.Set(Settings.K.EnableProxy, true);
        Settings.Set(Settings.K.EnableProxyAuth, false);
        Settings.SetValue(Settings.K.ProxyURL, "http://proxy.example.test:3128/");

        Assert.Equal("--proxy http://proxy.example.test:3128", WinGet.GetProxyArgument());
    }

    [Fact]
    public void GetProxyArgumentReturnsEmptyStringWhenProxyAuthIsEnabled()
    {
        Settings.Set(Settings.K.EnableProxy, true);
        Settings.Set(Settings.K.EnableProxyAuth, true);
        Settings.SetValue(Settings.K.ProxyURL, "http://proxy.example.test:3128/");

        Assert.Equal("", WinGet.GetProxyArgument());
    }

    [Theory]
    [MemberData(nameof(LocalSourceCases))]
    public void GetLocalSourceClassifiesKnownSourceFamilies(
        string id,
        LocalSourceKind expectedSourceKind
    )
    {
        var manager = new WinGet();

        var source = manager.GetLocalSource(id);

        Assert.Same(GetExpectedSource(manager, expectedSourceKind), source);
    }

    public static TheoryData<string, LocalSourceKind> LocalSourceCases =>
        new()
        {
            { "MSIX\\Microsoft.WindowsStore_8wekyb3d8bbwe", LocalSourceKind.MicrosoftStore },
            { "Programs\\{12345678-1234-1234-1234-123456789ABC}", LocalSourceKind.LocalPc },
            { "Apps\\com.example.android.app", LocalSourceKind.Android },
            { "Games\\Steam", LocalSourceKind.Steam },
            { "Games\\Steam App 12345", LocalSourceKind.Steam },
            { "Games\\Uplay", LocalSourceKind.Ubisoft },
            { "Games\\Uplay Install 12345", LocalSourceKind.Ubisoft },
            { "Games\\123456789_is1", LocalSourceKind.Gog },
            { "Programs\\Contoso.App", LocalSourceKind.LocalPc },
        };

    [Fact]
    public void GetInstalledPackagesUpdatesNoPackagesFlagForFailureAndRecovery()
    {
        var manager = new TestableWinGet();
        var expectedPackage = new PackageBuilder()
            .WithManager(manager)
            .WithName("Contoso Tool")
            .WithId("Contoso.Tool")
            .WithVersion("1.2.3")
            .Build();
        var helper = new TestWinGetManagerHelper
        {
            GetInstalledPackagesHandler = () => throw new InvalidOperationException("boom"),
        };
        WinGetHelper.Instance = helper;

        Assert.Throws<InvalidOperationException>(manager.InvokeGetInstalledPackages);
        Assert.True(WinGet.NO_PACKAGES_HAVE_BEEN_LOADED);

        helper.GetInstalledPackagesHandler = () => [expectedPackage];

        var packages = manager.InvokeGetInstalledPackages();

        Assert.False(WinGet.NO_PACKAGES_HAVE_BEEN_LOADED);
        PackageAssert.Matches(Assert.Single(packages), "Contoso Tool", "Contoso.Tool", "1.2.3");
    }

    private sealed class TestableWinGet : WinGet
    {
        public IReadOnlyList<Package> InvokeGetInstalledPackages() => base.GetInstalledPackages_UnSafe();
    }

    private static IManagerSource GetExpectedSource(WinGet manager, LocalSourceKind expectedSourceKind) =>
        expectedSourceKind switch
        {
            LocalSourceKind.MicrosoftStore => manager.MicrosoftStoreSource,
            LocalSourceKind.LocalPc => manager.LocalPcSource,
            LocalSourceKind.Android => manager.AndroidSubsystemSource,
            LocalSourceKind.Steam => manager.SteamSource,
            LocalSourceKind.Ubisoft => manager.UbisoftConnectSource,
            LocalSourceKind.Gog => manager.GOGSource,
            _ => throw new ArgumentOutOfRangeException(nameof(expectedSourceKind)),
        };

    private static void SetNoPackagesHaveBeenLoaded(bool value)
    {
        typeof(WinGet)
            .GetProperty(nameof(WinGet.NO_PACKAGES_HAVE_BEEN_LOADED))!
            .GetSetMethod(nonPublic: true)!
            .Invoke(null, [value]);
    }

    public enum LocalSourceKind
    {
        MicrosoftStore,
        LocalPc,
        Android,
        Steam,
        Ubisoft,
        Gog,
    }

    private sealed class TestWinGetManagerHelper : IWinGetManagerHelper
    {
        public Func<IReadOnlyList<Package>> GetAvailableUpdatesHandler { get; set; } = static () => [];
        public Func<IReadOnlyList<Package>> GetInstalledPackagesHandler { get; set; } = static () => [];
        public Func<string, IReadOnlyList<Package>> FindPackagesHandler { get; set; } = static _ => [];
        public Func<IReadOnlyList<IManagerSource>> GetSourcesHandler { get; set; } = static () => [];
        public Func<IPackage, IReadOnlyList<string>> GetInstallableVersionsHandler { get; set; } =
            static _ => [];
        public Action<IPackageDetails> GetPackageDetailsHandler { get; set; } = static _ => { };

        public IReadOnlyList<Package> GetAvailableUpdates_UnSafe() => GetAvailableUpdatesHandler();

        public IReadOnlyList<Package> GetInstalledPackages_UnSafe() => GetInstalledPackagesHandler();

        public IReadOnlyList<Package> FindPackages_UnSafe(string query) => FindPackagesHandler(query);

        public IReadOnlyList<IManagerSource> GetSources_UnSafe() => GetSourcesHandler();

        public IReadOnlyList<string> GetInstallableVersions_Unsafe(IPackage package) =>
            GetInstallableVersionsHandler(package);

        public void GetPackageDetails_UnSafe(IPackageDetails details) => GetPackageDetailsHandler(details);
    }
}
#endif
