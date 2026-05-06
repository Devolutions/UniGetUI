#if WINDOWS
using Devolutions.Pinget.Core;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.WingetManager;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
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
        Settings.SetValue(Settings.K.WinGetCliToolPreference, "");
        Settings.SetValue(Settings.K.WinGetComApiPolicy, "");
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

    [Fact]
    public void NativeWinGetHelperPrefersSystemComBeforeBundledActivation()
    {
        Assert.Equal(
            ["packaged COM registration", "lower-trust COM registration"],
            NativeWinGetHelper.PreferredActivationModes
        );
    }

    [Fact]
    public void GetBundledPingetExecutablePathPrefersRootExecutable()
    {
        const string installDir = @"C:\Program Files\UniGetUI";
        string rootPinget = Path.Join(installDir, "pinget.exe");
        string avaloniaPinget = Path.Join(installDir, "Avalonia", "pinget.exe");

        string path = WinGet.GetBundledPingetExecutablePath(
            installDir,
            filePath => filePath == rootPinget || filePath == avaloniaPinget
        );

        Assert.Equal(rootPinget, path);
    }

    [Fact]
    public void GetBundledPingetExecutablePathFallsBackToAvaloniaExecutable()
    {
        const string installDir = @"C:\Program Files\UniGetUI";
        string avaloniaPinget = Path.Join(installDir, "Avalonia", "pinget.exe");

        string path = WinGet.GetBundledPingetExecutablePath(
            installDir,
            filePath => filePath == avaloniaPinget
        );

        Assert.Equal(avaloniaPinget, path);
    }

    [Fact]
    public void GetBundledPingetExecutablePathReturnsRootPathWhenNoExecutableExists()
    {
        const string installDir = @"C:\Program Files\UniGetUI";
        string rootPinget = Path.Join(installDir, "pinget.exe");

        string path = WinGet.GetBundledPingetExecutablePath(installDir, static _ => false);

        Assert.Equal(rootPinget, path);
    }

    [Fact]
    public void FindCandidateExecutableFilesPrefersSystemWinGetBeforeBundledPinget()
    {
        const string systemWinGet = @"C:\WindowsApps\winget.exe";
        const string bundledPinget = @"C:\Program Files\UniGetUI\pinget.exe";

        var candidates = WinGet.FindCandidateExecutableFiles(
            static executableName => executableName == "winget.exe" ? [systemWinGet] : [],
            path => path == bundledPinget,
            bundledPinget
        );

        Assert.Equal([systemWinGet, bundledPinget], candidates);
    }

    [Fact]
    public void FindCandidateExecutableFilesUsesBundledPingetWhenSystemWinGetIsMissing()
    {
        const string bundledPinget = @"C:\Program Files\UniGetUI\pinget.exe";

        var candidates = WinGet.FindCandidateExecutableFiles(
            static _ => [],
            path => path == bundledPinget,
            bundledPinget
        );

        Assert.Equal([bundledPinget], candidates);
    }

    [Fact]
    public void FindCandidateExecutableFilesCanUseSystemWinGetWithoutBundledPingetFallback()
    {
        const string systemWinGet = @"C:\WindowsApps\winget.exe";
        const string bundledPinget = @"C:\Program Files\UniGetUI\pinget.exe";

        var candidates = WinGet.FindCandidateExecutableFiles(
            static executableName => executableName == "winget.exe" ? [systemWinGet] : [],
            path => path == bundledPinget,
            bundledPinget,
            WinGetCliToolPreference.SystemWinGet
        );

        Assert.Equal([systemWinGet], candidates);
    }

    [Fact]
    public void FindCandidateExecutableFilesCanUseBundledPingetWithoutSystemWinGetFallback()
    {
        const string systemWinGet = @"C:\WindowsApps\winget.exe";
        const string bundledPinget = @"C:\Program Files\UniGetUI\pinget.exe";

        var candidates = WinGet.FindCandidateExecutableFiles(
            static executableName => executableName == "winget.exe" ? [systemWinGet] : [],
            path => path == bundledPinget,
            bundledPinget,
            WinGetCliToolPreference.BundledPinget
        );

        Assert.Equal([bundledPinget], candidates);
    }

    [Fact]
    public void FindCandidateExecutableFilesCanUsePathPingetWithoutSystemWinGetFallback()
    {
        const string bundledPinget = @"C:\Program Files\UniGetUI\pinget.exe";
        const string pathPinget = @"C:\Tools\pinget.exe";

        var candidates = WinGet.FindCandidateExecutableFiles(
            static executableName =>
                executableName switch
                {
                    "winget.exe" => throw new InvalidOperationException(
                        "System WinGet should not be queried in Pinget mode."
                    ),
                    "pinget.exe" => [pathPinget],
                    _ => [],
                },
            static _ => false,
            bundledPinget,
            WinGetCliToolPreference.BundledPinget
        );

        Assert.Equal([pathPinget], candidates);
    }

    [Fact]
    public void FindCandidateExecutableFilesReturnsEmptyWhenNoCliToolExists()
    {
        var candidates = WinGet.FindCandidateExecutableFiles(
            static _ => [],
            static _ => false,
            @"C:\Program Files\UniGetUI\pinget.exe"
        );

        Assert.Empty(candidates);
    }

    [Fact]
    public void PingetCliHelperDeserializesListResponsesWithGeneratedContext()
    {
        const string json = """
            {
                "matches": [
                    {
                        "name": "Contoso Tool",
                        "id": "Contoso.Tool",
                        "localId": null,
                        "installedVersion": "1.2.3",
                        "availableVersion": "2.0.0",
                        "sourceName": "winget",
                        "publisher": null,
                        "scope": null,
                        "installerCategory": null,
                        "installLocation": null,
                        "packageFamilyNames": [],
                        "productCodes": [],
                        "upgradeCodes": []
                    }
                ],
                "warnings": [],
                "truncated": false
            }
            """;

        ListResponse response = PingetCliHelper.DeserializeJson<ListResponse>(json);

        ListMatch match = Assert.Single(response.Matches);
        Assert.Equal("Contoso Tool", match.Name);
        Assert.Equal("Contoso.Tool", match.Id);
        Assert.Equal("1.2.3", match.InstalledVersion);
        Assert.Equal("2.0.0", match.AvailableVersion);
        Assert.Equal("winget", match.SourceName);
    }

    [Fact]
    public void GetCliToolPreferenceUsesEnvironmentBeforeSettings()
    {
        var preference = WinGet.GetCliToolPreference(
            name => name == WinGet.CliToolPreferenceEnvironmentVariable ? "pinget" : null,
            key => key == Settings.K.WinGetCliToolPreference ? "winget" : ""
        );

        Assert.Equal(WinGetCliToolPreference.BundledPinget, preference);
    }

    [Fact]
    public void GetCliToolPreferenceFallsBackToSettings()
    {
        var preference = WinGet.GetCliToolPreference(
            static _ => null,
            key => key == Settings.K.WinGetCliToolPreference ? "pinget" : ""
        );

        Assert.Equal(WinGetCliToolPreference.BundledPinget, preference);
    }

    [Theory]
    [InlineData("default", 0)]
    [InlineData("winget", 1)]
    [InlineData("pinget", 2)]
    public void GetCliToolPreferenceAcceptsSupportedValues(
        string value,
        int expectedPreference
    )
    {
        var preference = WinGet.GetCliToolPreference(
            static _ => null,
            key => key == Settings.K.WinGetCliToolPreference ? value : ""
        );

        Assert.Equal((WinGetCliToolPreference)expectedPreference, preference);
    }

    [Theory]
    [InlineData(@"C:\Program Files\UniGetUI\pinget.exe", 1)]
    [InlineData(@"C:\Tools\pinget.exe", 1)]
    [InlineData(@"C:\WindowsApps\winget.exe", 0)]
    public void GetCliToolKindRecognizesPingetExecutableName(
        string executablePath,
        int expectedKind
    )
    {
        Assert.Equal((WinGetCliToolKind)expectedKind, WinGet.GetCliToolKind(executablePath));
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("system")]
    [InlineData("bundled-pinget")]
    [InlineData("pinget-only")]
    public void GetCliToolPreferenceIgnoresUnsupportedValues(string value)
    {
        var preference = WinGet.GetCliToolPreference(
            static _ => null,
            key => key == Settings.K.WinGetCliToolPreference ? value : ""
        );

        Assert.Equal(WinGetCliToolPreference.Default, preference);
    }

    [Fact]
    public void GetComApiPolicyUsesEnvironmentBeforeSettings()
    {
        var policy = WinGet.GetComApiPolicy(
            name => name == WinGet.ComApiPolicyEnvironmentVariable ? "disabled" : null,
            key => key == Settings.K.WinGetComApiPolicy ? "enabled" : ""
        );

        Assert.Equal(WinGetComApiPolicy.Disabled, policy);
    }

    [Fact]
    public void GetComApiPolicyAcceptsDefaultValue()
    {
        var policy = WinGet.GetComApiPolicy(
            static _ => null,
            key => key == Settings.K.WinGetComApiPolicy ? "default" : ""
        );

        Assert.Equal(WinGetComApiPolicy.Default, policy);
    }

    [Fact]
    public void ShouldUseWinGetComApiAllowsSystemCliToolWhenPolicyAllowsComApi()
    {
        Assert.True(
            WinGet.ShouldUseWinGetComApi(
                WinGetCliToolKind.SystemWinGet,
                WinGetComApiPolicy.Default
            )
        );
        Assert.True(
            WinGet.ShouldUseWinGetComApi(
                WinGetCliToolKind.SystemWinGet,
                WinGetComApiPolicy.Enabled
            )
        );
    }

    [Fact]
    public void ShouldUseWinGetComApiCanDisableComForSystemCliTool()
    {
        Assert.False(
            WinGet.ShouldUseWinGetComApi(
                WinGetCliToolKind.SystemWinGet,
                WinGetComApiPolicy.Disabled
            )
        );
    }

    [Fact]
    public void ShouldUseWinGetComApiNeverUsesComForBundledPingetCliTool()
    {
        Assert.False(
            WinGet.ShouldUseWinGetComApi(
                WinGetCliToolKind.BundledPinget,
                WinGetComApiPolicy.Default
            )
        );
        Assert.False(
            WinGet.ShouldUseWinGetComApi(
                WinGetCliToolKind.BundledPinget,
                WinGetComApiPolicy.Enabled
            )
        );
    }

    [Fact]
    public void NativeWinGetHelperUsesSystemCliFallbackForInstalledPackagesWhenCompositeCatalogFails()
    {
        var manager = new TestableWinGet();
        var expectedPackage = new PackageBuilder()
            .WithManager(manager)
            .WithName("Contoso Tool")
            .WithId("Contoso.Tool")
            .WithVersion("1.2.3")
            .Build();
        var systemCliFallbackHelper = new TestWinGetManagerHelper
        {
            GetInstalledPackagesHandler = () => [expectedPackage],
        };
        var helper = new NativeWinGetHelper(
            manager,
            systemCliHelperFactory: _ => systemCliFallbackHelper,
            skipInitialization: true,
            localPackagesProvider: () =>
                throw new InvalidOperationException("WinGet: Failed to connect to composite catalog.")
        );

        var packages = helper.GetInstalledPackages_UnSafe();

        PackageAssert.Matches(Assert.Single(packages), "Contoso Tool", "Contoso.Tool", "1.2.3");
    }

    [Fact]
    public void NativeWinGetHelperUsesSystemCliFallbackForUpdatesWhenCompositeCatalogFails()
    {
        var manager = new TestableWinGet();
        var expectedPackage = new PackageBuilder()
            .WithManager(manager)
            .WithName("Contoso Tool")
            .WithId("Contoso.Tool")
            .WithVersion("1.2.3")
            .WithNewVersion("2.0.0")
            .Build();
        var systemCliFallbackHelper = new TestWinGetManagerHelper
        {
            GetAvailableUpdatesHandler = () => [expectedPackage],
        };
        var helper = new NativeWinGetHelper(
            manager,
            systemCliHelperFactory: _ => systemCliFallbackHelper,
            skipInitialization: true,
            localPackagesProvider: () =>
                throw new InvalidOperationException("WinGet: Failed to connect to composite catalog.")
        );

        var packages = helper.GetAvailableUpdates_UnSafe();

        var package = Assert.Single(packages);
        Assert.Equal("Contoso.Tool", package.Id);
        Assert.Equal("2.0.0", package.NewVersionString);
    }

    [Fact]
    public void NativeWinGetHelperSelectReachableCatalogsSkipsUnavailableSources()
    {
        var reachableCatalogs = NativeWinGetHelper.SelectReachableCatalogs(
            ["winget", "offline", "msstore"],
            static catalog => catalog,
            static catalog => catalog != "offline"
        );

        Assert.Equal(["winget", "msstore"], reachableCatalogs);
    }

    [Fact]
    public void NativeWinGetHelperSelectReachableCatalogsThrowsWhenAllSourcesAreUnavailable()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            NativeWinGetHelper.SelectReachableCatalogs(
                ["offline-a", "offline-b"],
                static catalog => catalog,
                static _ => false
            )
        );

        Assert.Equal("WinGet: Failed to connect to composite catalog.", exception.Message);
    }

    [Fact]
    public void AttemptFastRepairKeepsNativeHelperWhileLocalPackageEnumerationIsStillRunning()
    {
        var manager = new TestableWinGet();
        var helper = new NativeWinGetHelper(
            manager,
            systemCliHelperFactory: null,
            skipInitialization: true,
            localPackagesProvider: null
        );
        helper.SetLocalPackageQueryInProgressForTesting(true);
        WinGetHelper.Instance = helper;

        manager.AttemptFastRepair();

        Assert.Same(helper, WinGetHelper.Instance);
    }

    [Fact]
    public void PingetPackageDetailsProviderMapsShowResultToPackageDetails()
    {
        var manager = new WinGet();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithName("Contoso Tool")
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .Build();
        var details = new PackageDetails(package);
        PackageQuery? capturedQuery = null;
        var provider = new PingetPackageDetailsProvider(
            query =>
            {
                capturedQuery = query;
                return CreatePingetShowResult();
            },
            installerSizeResolver: _ => 1234
        );

        provider.LoadPackageDetails(details, new TestNativeTaskLogger());

        Assert.NotNull(capturedQuery);
        Assert.Equal("Contoso.Tool", capturedQuery.Id);
        Assert.Equal("winget", capturedQuery.Source);
        Assert.True(capturedQuery.Exact);
        Assert.Equal("https://example.test/installer.exe", details.InstallerUrl?.ToString());
        Assert.Equal("ABC123", details.InstallerHash);
        Assert.Equal("exe", details.InstallerType);
        Assert.Equal("2026-04-27", details.UpdateDate);
        Assert.Equal(1234, details.InstallerSize);
        Assert.Contains(details.Dependencies, dependency =>
            dependency.Name == "Contoso.Dependency" && dependency.Version == "2.0"
        );
        Assert.Contains("utility", details.Tags);
    }

    [Fact]
    public void PingetPackageDetailsProviderKeepsExistingDetailsWhenShowFails()
    {
        var manager = new WinGet();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithName("Contoso Tool")
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .Build();
        var details = new PackageDetailsBuilder()
            .WithDescription("Native description")
            .WithPublisher("Native publisher")
            .Build(package);
        var provider = new PingetPackageDetailsProvider(
            _ => throw new InvalidOperationException("source cache missing")
        );

        provider.LoadPackageDetails(details, new TestNativeTaskLogger());

        Assert.Equal("Native description", details.Description);
        Assert.Equal("Native publisher", details.Publisher);
        Assert.Null(details.InstallerUrl);
        Assert.Empty(details.Dependencies);
    }

    [Fact]
    public void PingetPackageDetailsProviderUsesShortDescriptionWhenDescriptionIsMissing()
    {
        var manager = new WinGet();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithName("tessl")
            .WithId("tessl.tessl")
            .WithVersion("0.77.0")
            .Build();
        var details = new PackageDetails(package);
        var provider = new PingetPackageDetailsProvider(
            _ => CreatePingetShowResult(description: null, shortDescription: "The package manager for agent skills and context"),
            installerSizeResolver: _ => 1234
        );

        provider.LoadPackageDetails(details, new TestNativeTaskLogger());

        Assert.Equal("The package manager for agent skills and context", details.Description);
    }

    [Fact]
    public void PingetPackageDetailsProviderOmitsEllipsizedSourceFromQuery()
    {
        var manager = new WinGet();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithName("Contoso Tool")
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .WithSource(new ManagerSource(manager, "winge…", new Uri("https://example.test")))
            .Build();

        PackageQuery query = PingetPackageDetailsProvider.CreateQuery(package);

        Assert.Null(query.Source);
    }

    [Fact]
    public void PingetPackageDetailsProviderUsesSystemWingetRepositorySources()
    {
        RepositoryOptions options = PingetPackageDetailsProvider.CreateRepositoryOptions();

        Assert.Null(options.AppRoot);
        Assert.False(string.IsNullOrWhiteSpace(options.UserAgent));
    }

    [Fact]
    public void WinGetCliHelperUsesPingetProviderForPackageDetails()
    {
        var manager = new WinGet();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithName("Contoso Tool")
            .WithId("Contoso.Tool")
            .WithVersion("1.0.0")
            .Build();
        var details = new PackageDetails(package);
        int providerCallCount = 0;
        var provider = new TestPingetPackageDetailsProvider(packageDetails =>
        {
            providerCallCount++;
            packageDetails.Publisher = "Pinget publisher";
            packageDetails.InstallerHash = "PINGET-HASH";
        });
        var helper = new WinGetCliHelper(manager, @"C:\missing\winget.exe", provider);

        helper.GetPackageDetails_UnSafe(details);

        Assert.Equal(1, providerCallCount);
        Assert.Equal("Pinget publisher", details.Publisher);
        Assert.Equal("PINGET-HASH", details.InstallerHash);
        Assert.Equal(
            "https://github.com/microsoft/winget-pkgs/tree/master/manifests/c/Contoso/Tool",
            details.ManifestUrl?.ToString()
        );
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

    private static ShowResult CreatePingetShowResult(
        string? description = "Contoso description",
        string? shortDescription = null
    )
    {
        var package = new SearchMatch
        {
            SourceName = "winget",
            SourceKind = SourceKind.PreIndexed,
            Id = "Contoso.Tool",
            Name = "Contoso Tool",
            Version = "1.2.3",
        };
        var installer = new Installer
        {
            Architecture = "x64",
            InstallerType = "exe",
            Url = "https://example.test/installer.exe",
            Sha256 = "ABC123",
            ReleaseDate = "2026-04-27",
            PackageDependencies = ["Contoso.Dependency [2.0]"],
        };

        return new ShowResult
        {
            Package = package,
            Manifest = new Manifest
            {
                Id = "Contoso.Tool",
                Name = "Contoso Tool",
                Version = "1.2.3",
                Author = "Contoso",
                Description = description,
                ShortDescription = shortDescription,
                License = "MIT",
                PackageUrl = "https://example.test/tool",
                Publisher = "Contoso Ltd.",
                ReleaseNotes = "Release notes",
                Tags = ["utility"],
                PackageDependencies = ["Contoso.Runtime"],
                Installers = [installer],
            },
            SelectedInstaller = installer,
            StructuredDocument = new Dictionary<string, object?>(),
        };
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

    private sealed class TestPingetPackageDetailsProvider(Action<IPackageDetails> handler)
        : IPingetPackageDetailsProvider
    {
        public bool LoadPackageDetails(IPackageDetails details, INativeTaskLogger logger)
        {
            handler(details);
            return true;
        }
    }

    private sealed class TestNativeTaskLogger : INativeTaskLogger
    {
        public List<string> Lines { get; } = [];
        public int? ReturnCode { get; private set; }

        public IReadOnlyList<string> AsColoredString(bool verbose = false) => Lines;

        public void Close(int returnCode) => ReturnCode = returnCode;

        public void Error(Exception? e) => Lines.Add(e?.Message ?? "");

        public void Error(IReadOnlyList<string> lines) => Lines.AddRange(lines);

        public void Error(string? line) => Lines.Add(line ?? "");

        public void Log(IReadOnlyList<string> lines) => Lines.AddRange(lines);

        public void Log(string? line) => Lines.Add(line ?? "");
    }
}
#endif
