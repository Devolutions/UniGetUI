using System.Diagnostics;
using System.Text.Json.Nodes;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.Managers.MacAppStoreManager;
using UniGetUI.PackageEngine.Managers.MacAppStoreManager.Internal;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;

namespace UniGetUI.PackageEngine.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class MacAppStoreManagerTestCollection
{
    public const string Name = "Mac App Store manager tests";
}

[Collection(MacAppStoreManagerTestCollection.Name)]
public sealed class MacAppStoreManagerTests : IDisposable
{
    private const string Amphetamine = "937984704";
    private const string Keynote = "409183694";
    private const string FinalCut = "424389933";
    private const string HelperExecutable = "/app/UniGetUIMasHelper";

    private readonly string _testRoot = Path.Combine(
        AppContext.BaseDirectory,
        nameof(MacAppStoreManagerTests),
        Guid.NewGuid().ToString("N")
    );

    private readonly Func<ProcessStartInfo, Action<string>, CancellationToken, Task<int>> _runProcess =
        ElevatedMas.RunProcess;
    private readonly Func<bool> _isRoot = MacAppStorePkgOperationHelper.IsRoot;
    private readonly Func<(uint UserId, uint GroupId)> _userIds = ElevatedMas.UserIds;
    private readonly Func<bool> _touchIdSudoEnabled = ElevatedMas.TouchIdSudoEnabled;
    private readonly Func<string?> _nativeHelperPath = NativeMas.HelperPath;
    private readonly Func<string, Task<bool>> _canBootstrapWithHelper = NativeMas.CanBootstrapWithHelper;
    private readonly Func<bool> _hasInstalledDaemonFiles = NativeMas.HasInstalledDaemonFiles;

    public MacAppStoreManagerTests()
    {
        Directory.CreateDirectory(_testRoot);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Settings.ResetSettings();
        ElevatedMas.RunProcess = (_, _, _) => throw new InvalidOperationException("No process may run in this test.");
        ElevatedMas.UserIds = () => (501, 20);
        ElevatedMas.TouchIdSudoEnabled = () => false;
        NativeMas.HelperPath = () => null;
        NativeMas.CanBootstrapWithHelper = _ => Task.FromResult(false);
        NativeMas.HasInstalledDaemonFiles = () => false;
    }

    public void Dispose()
    {
        ElevatedMas.RunProcess = _runProcess;
        ElevatedMas.UserIds = _userIds;
        ElevatedMas.TouchIdSudoEnabled = _touchIdSudoEnabled;
        NativeMas.HelperPath = _nativeHelperPath;
        NativeMas.CanBootstrapWithHelper = _canBootstrapWithHelper;
        NativeMas.HasInstalledDaemonFiles = _hasInstalledDaemonFiles;
        MacAppStorePkgOperationHelper.IsRoot = _isRoot;
        Settings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private static string Fixture(string name) =>
        PackageEngineFixtureFiles.ReadAllText(Path.Combine("MacAppStore", name));

    private static string[] FixtureLines(string name) => Fixture(name).Split('\n');

    private static JsonObject SearchRecord(string id) =>
        MasOutput.ParseApps(FixtureLines("search.jsonl"), outdated: false).Single(app => app.Id == id).Catalog!;

    private static JsonObject LookupRecord(string id) =>
        AppStoreCatalog.ParseLookup(Fixture("lookup.json")).Single(app => AppStoreRecord.Id(app) == id);

    /// <summary>A manager whose catalog answers from the lookup fixture and counts the requests made.</summary>
    private static (ListingMacAppStore Manager, List<Uri> Requests) NewManager(
        string storefront = "CA",
        bool isAppleSilicon = true,
        Version? currentMacOSVersion = null)
    {
        var manager = new ListingMacAppStore
        {
            Storefront = storefront,
            IsAppleSilicon = isAppleSilicon,
            CurrentMacOSVersion = currentMacOSVersion ?? new Version(26, 7, 3),
            Status = new ManagerStatus { Found = true, ExecutablePath = HelperExecutable, ExecutableCallArgs = "", Version = "macOS 26.7.3" },
        };
        List<Uri> requests = [];
        manager.Catalog.Download = url =>
        {
            requests.Add(url);
            return Fixture("lookup.json");
        };
        return (manager, requests);
    }

    private static Package App(MacAppStore manager, string id = Amphetamine, string name = "Amphetamine") =>
        new PackageBuilder().WithManager(manager).WithId(id).WithName(name).WithVersion("5.3.2").Build();

    [Fact]
    public void ManagerAndSourcesUseTheMacAppStoreIcon()
    {
        var (manager, _) = NewManager();
        Assert.Equal(IconType.MacAppStore, manager.Properties.IconId);
        Assert.Equal("mas_color", manager.Properties.ColorIconId);
        Assert.Equal(IconType.MacAppStore, manager.Properties.DefaultSource.IconId);
        Assert.All(manager.Properties.KnownSources, source => Assert.Equal(IconType.MacAppStore, source.IconId));
    }

    // ── mas output ─────────────────────────────────────────────────────────

    [Fact]
    public void ParsesInstalledAppsFromJsonLines()
    {
        var apps = MasOutput.ParseApps(FixtureLines("list.jsonl"), outdated: false);

        Assert.Equal([Amphetamine, Keynote, "6450432292"], apps.Select(app => app.Id));
        MasApp print = apps[2];
        Assert.Equal("Universal Print", print.Name);
        Assert.Equal("1.0", print.Version);
        Assert.Null(print.NewVersion);
        Assert.Equal("/Applications/Universal Print.app", print.Path);
        // The first copy of a duplicated app wins.
        Assert.Equal("/Applications/Amphetamine.app", apps[0].Path);
    }

    [Fact]
    public void ParsesInstalledAppsFromTheTable()
    {
        var apps = MasOutput.ParseApps(FixtureLines("list.txt"), outdated: false);

        Assert.Equal(
            [
                (Amphetamine, "Amphetamine", "5.3.2"),
                (Keynote, "Keynote", "14.4"),
                ("6450432292", "Universal Print", "1.0"),
                ("1176895641", "Notes (Classic)", "2.1 beta"),
            ],
            apps.Select(app => (app.Id, app.Name, app.Version)));
        Assert.All(apps, app => Assert.Null(app.Path));
    }

    [Fact]
    public void ParsesSearchResultsWithAndWithoutPrices()
    {
        var table = MasOutput.ParseApps(FixtureLines("search.txt"), outdated: false);
        Assert.Equal(4, table.Count);
        Assert.Equal(("NEVER SLEEP - Even Lid Closed!", "1.1"), (table[2].Name, table[2].Version));
        Assert.Equal(("Caffeinated: Anti Sleep App", "3.3.0"), (table[3].Name, table[3].Version));

        var json = MasOutput.ParseApps(FixtureLines("search.jsonl"), outdated: false);
        Assert.Equal([Amphetamine, FinalCut], json.Select(app => app.Id));
        Assert.All(json, app => Assert.True(AppStoreRecord.IsCatalogRecord(app.Catalog!)));
    }

    [Theory]
    [InlineData("outdated.jsonl")]
    [InlineData("outdated.txt")]
    public void ParsesOutdatedApps(string fixture)
    {
        var apps = MasOutput.ParseApps(FixtureLines(fixture), outdated: true);

        Assert.Equal(
            [("6450432292", "Universal Print", "1.0", "1.1.0"), ("1295203466", "Windows App", "11.3.6", "11.4.4")],
            apps.Select(app => (app.Id, app.Name, app.Version, app.NewVersion!)));
    }

    [Fact]
    public void AnOutdatedListingIgnoresAppsWithoutANewVersion()
    {
        Assert.Empty(MasOutput.ParseApps(FixtureLines("list.jsonl"), outdated: true));
        Assert.Empty(MasOutput.ParseApps(FixtureLines("list.txt"), outdated: true));
    }

    [Fact]
    public void IgnoresMessagesAndMalformedLines()
    {
        string[] lines =
        [
            "No apps found",
            "Warning: Search for Keynote failed",
            "==> Downloading Amphetamine",
            "{\"adamID\":\"not-a-number\",\"name\":\"Fake\",\"version\":\"1\"}",
            "{\"adamID\":42,\"version\":\"1\"}",
            "{ broken json",
            "abc  Name  (1.0)",
        ];

        Assert.Empty(MasOutput.ParseApps(lines, outdated: false));
    }

    [Theory]
    [InlineData("config.json", "CA")]
    [InlineData("config.txt", "CA")]
    public void TheStorefrontWinsOverTheRegion(string fixture, string expected) =>
        Assert.Equal(expected, MasOutput.ParseStorefront(FixtureLines(fixture)));

    [Theory]
    [InlineData("143455-6,13", "CA")]
    [InlineData("143441-1,29", "US")]
    [InlineData("143462-9,29", "JP")]
    [InlineData("pr", "US")]
    [InlineData("mc", "FR")]
    [InlineData("gi", "GB")]
    [InlineData("li", "CH")]
    [InlineData("gl", "DK")]
    [InlineData("cw", "NL")]
    [InlineData("{\"region\":\"fr\"}", "FR")]
    [InlineData("{\"store\":\"CAN\",\"region\":\"de\"}", "DE")]
    [InlineData("region ▁ jp", "JP")]
    [InlineData("{\"mas\":\"7.0.0\"}", null)]
    [InlineData("Error: unknown command", null)]
    [InlineData("", null)]
    public void ReadsAndNormalizesTheStorefront(string output, string? expected) =>
        Assert.Equal(expected, MasOutput.ParseStorefront(output.Split('\n')));

    [Theory]
    [InlineData("7.0.0", 7)]
    [InlineData("3.1.0", 3)]
    [InlineData("mas 1.8.6", 1)]
    [InlineData("", 0)]
    [InlineData("unknown", 0)]
    public void ReadsTheMajorVersion(string version, int expected) =>
        Assert.Equal(expected, MasOutput.ParseMajorVersion(version));

    [Theory]
    [InlineData("amphetamine", "amphetamine")]
    [InlineData("final  cut pro", "final cut pro")]
    [InlineData("--help", "help")]
    [InlineData("keynote -h", "keynote h")]
    [InlineData("x; rm -rf ~ $(id)", "x rm rf id")]
    [InlineData("---", "")]
    public void SearchTermsCannotBeReadAsOptions(string query, string expected) =>
        Assert.Equal(expected, MacAppStore.SearchTerms(query));

    // ── App Store records ──────────────────────────────────────────────────

    [Fact]
    public void ReadsTheRecordsOfMasAndOfTheLookupApi()
    {
        JsonObject mas = SearchRecord(FinalCut);
        Assert.Equal(FinalCut, AppStoreRecord.Id(mas));
        Assert.Equal(399.99m, AppStoreRecord.Price(mas));
        Assert.Equal(6244311040L, AppStoreRecord.SizeInBytes(mas));
        Assert.Equal("https://is1-ssl.mzstatic.com/image/thumb/finalcut/512x512bb.png", AppStoreRecord.Icon(mas)!.ToString());
        Assert.Empty(AppStoreRecord.Screenshots(mas));

        JsonObject lookup = LookupRecord(Amphetamine);
        Assert.Equal(Amphetamine, AppStoreRecord.Id(lookup));
        Assert.Equal(0m, AppStoreRecord.Price(lookup));
        Assert.Equal(6671277L, AppStoreRecord.SizeInBytes(lookup));
        Assert.Equal("https://is1-ssl.mzstatic.com/image/thumb/amphetamine/512x512bb.png", AppStoreRecord.Icon(lookup)!.ToString());
        Assert.Equal(["https://is1-ssl.mzstatic.com/image/thumb/amphetamine/shot1.jpg"],
            AppStoreRecord.Screenshots(lookup).Select(url => url.ToString()));
    }

    [Fact]
    public void AnInstalledAppIsNotACatalogRecord()
    {
        JsonObject installed = MasOutput.ParseApps(FixtureLines("list.jsonl"), outdated: false)[0].Catalog!;

        Assert.False(AppStoreRecord.IsCatalogRecord(installed));
        Assert.Null(AppStoreRecord.Price(installed));
        Assert.Equal(0, AppStoreRecord.SizeInBytes(installed));
    }

    [Fact]
    public void OnlyWebAddressesAreKept()
    {
        var record = new JsonObject
        {
            ["trackId"] = 1,
            ["artworkUrl512"] = "file:///etc/passwd",
            ["artworkUrl100"] = "https://example.com/100.png",
            ["screenshotUrls"] = new JsonArray("javascript:alert(1)", "https://example.com/1.png", 3),
        };

        Assert.Equal("https://example.com/100.png", AppStoreRecord.Icon(record)!.ToString());
        Assert.Equal(["https://example.com/1.png"], AppStoreRecord.Screenshots(record).Select(url => url.ToString()));
    }

    [Fact]
    public void FillsDetailsFromALookupRecord()
    {
        var (manager, _) = NewManager();
        var details = new PackageDetails(App(manager, Keynote, "Keynote"));

        MacAppStorePkgDetailsHelper.Fill(details, LookupRecord(Keynote));

        Assert.Equal("Build stunning presentations.", details.Description);
        Assert.Equal("Apple", details.Author);
        Assert.Equal("Apple Inc.", details.Publisher);
        Assert.Equal("https://www.apple.com/keynote/", details.HomepageUrl!.ToString());
        Assert.Equal("https://apps.apple.com/ca/app/keynote/id409183694?mt=12&uo=4", details.ManifestUrl!.ToString());
        Assert.Equal(details.ManifestUrl, details.ReleaseNotesUrl);
        Assert.Equal(642351104L, details.InstallerSize);
        Assert.Equal(["Productivity"], details.Tags);
        Assert.Equal("2026-09-15", details.UpdateDate);
        Assert.Equal("Mac App Store", details.InstallerType);
    }

    [Fact]
    public void FillsDetailsFromAMasRecord()
    {
        var (manager, _) = NewManager();
        var details = new PackageDetails(App(manager));

        MacAppStorePkgDetailsHelper.Fill(details, SearchRecord(Amphetamine));

        Assert.Equal("Keeps your Mac awake.", details.Description);
        Assert.Equal("William Gustafson", details.Author);
        Assert.Equal("William Gustafson", details.Publisher);
        // No seller site: the App Store page stands in for it.
        Assert.Equal(details.ManifestUrl, details.HomepageUrl);
        Assert.Equal("Adds Power Protect.", details.ReleaseNotes);
        Assert.Equal(["Utilities", "Productivity"], details.Tags);
        Assert.Equal("2023-11-10", details.UpdateDate);
    }

    // ── Catalog ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("CA")]
    [InlineData("US")]
    [InlineData("JP")]
    public void OneLookupFetchesEveryAppTheListingsShowed(string storefront)
    {
        var (manager, requests) = NewManager(storefront: storefront);
        manager.Catalog.Want([Amphetamine, Keynote]);

        Assert.NotNull(manager.Catalog.Get(Keynote));
        Assert.NotNull(manager.Catalog.Get(Amphetamine));

        Uri request = Assert.Single(requests);
        Assert.Equal("itunes.apple.com", request.Host);
        Assert.Contains($"country={storefront.ToLowerInvariant()}", request.Query);
        string ids = request.Query.Split('&').Single(part => part.TrimStart('?').StartsWith("id=")).Split('=')[1];
        // The app asked for comes first; the others ride along.
        Assert.Equal([Keynote, Amphetamine], ids.Split(','));
    }

    [Fact]
    public void ALookupNeverAsksForMoreThanItsLimit()
    {
        var (manager, requests) = NewManager();
        manager.Catalog.Want(Enumerable.Range(1, 250).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)));

        manager.Catalog.Get(Amphetamine);

        string ids = Assert.Single(requests).Query.TrimStart('?').Split('&')[0]["id=".Length..];
        Assert.Equal(AppStoreCatalog.MaxIdsPerLookup, ids.Split(',').Length);
    }

    [Fact]
    public void AnAppTheStoreDoesNotHaveIsNotAskedForAgain()
    {
        var (manager, requests) = NewManager();

        Assert.Null(manager.Catalog.Get("1"));
        Assert.Null(manager.Catalog.Get("1"));

        Assert.Single(requests);
    }

    [Fact]
    public void AFailedLookupHoldsOffFurtherLookups()
    {
        var (manager, _) = NewManager();
        int attempts = 0;
        manager.Catalog.Download = _ =>
        {
            attempts++;
            throw new HttpRequestException("Too many requests");
        };

        Assert.Null(manager.Catalog.Get(Amphetamine));
        Assert.Null(manager.Catalog.Get(Keynote));

        Assert.Equal(1, attempts);
    }

    [Fact]
    public void SearchRecordsSaveALookupAndPeekNeverDownloads()
    {
        var (manager, requests) = NewManager();

        Assert.Null(manager.Catalog.Peek(FinalCut));
        manager.Catalog.Remember(SearchRecord(FinalCut));
        manager.Catalog.Remember(MasOutput.ParseApps(FixtureLines("list.jsonl"), outdated: false)[1].Catalog);

        Assert.NotNull(manager.Catalog.Peek(FinalCut));
        Assert.Same(manager.Catalog.Peek(FinalCut), manager.Catalog.Get(FinalCut));
        Assert.Null(manager.Catalog.Peek(Keynote));
        Assert.Empty(requests);
    }

    [Fact]
    public void AnUnreadableLookupYieldsNothing()
    {
        Assert.Empty(AppStoreCatalog.ParseLookup("<html>Service unavailable</html>"));
        Assert.Empty(AppStoreCatalog.ParseLookup("{\"resultCount\":0}"));
        Assert.Equal(2, AppStoreCatalog.ParseLookup(Fixture("lookup.json")).Count);
    }

    // ── Operations ─────────────────────────────────────────────────────────

    private static MacAppStorePkgOperationHelper Operations(MacAppStore manager) =>
        Assert.IsType<MacAppStorePkgOperationHelper>(manager.OperationHelper);

    [Theory]
    [InlineData(OperationType.Install, "get")]
    [InlineData(OperationType.Update, "update")]
    [InlineData(OperationType.Uninstall, "uninstall")]
    public void ChoosesTheOperationVerb(OperationType operation, string verb)
    {
        var (manager, _) = NewManager();

        Assert.Equal(verb, Operations(manager).Verb(App(manager), operation));
    }

    [Fact]
    public void APaidAppIsReinstalledRatherThanBought()
    {
        var (manager, _) = NewManager();
        manager.Catalog.Remember(SearchRecord(FinalCut));

        Assert.Equal("install", Operations(manager).Verb(App(manager, FinalCut, "Final Cut Pro"), OperationType.Install));
    }

    [Fact]
    public void BuildsParametersAndRefusesStandaloneCliCommandLine()
    {
        var (manager, _) = NewManager();

        Assert.Equal(["get", Amphetamine],
            Operations(manager).GetParameters(App(manager), new InstallOptions(), OperationType.Install));
        Assert.Equal(["update", Amphetamine],
            Operations(manager).GetParameters(App(manager), new InstallOptions(), OperationType.Update));
        Assert.Equal(["uninstall", Amphetamine],
            Operations(manager).GetParameters(App(manager), new InstallOptions(), OperationType.Uninstall));
        Assert.Throws<InvalidOperationException>(() =>
            Operations(manager).GetStandaloneParameters(App(manager), new InstallOptions(), OperationType.Install));
    }

    [Fact]
    public void ReinstallingAnAlreadyInstalledAppPassesForceAndUninstallForgetsInstallPath()
    {
        var (manager, _) = NewManager();
        var operations = Operations(manager);
        string appDir = Path.Combine(_testRoot, "Amphetamine.app");
        Directory.CreateDirectory(appDir);

        Package fresh = App(manager);
        Assert.Equal(["get", Amphetamine], operations.GetParameters(fresh, new InstallOptions(), OperationType.Install));

        manager.RecordInstallPath(Amphetamine, appDir);
        Assert.Equal(["get", "--force", Amphetamine], operations.GetParameters(fresh, new InstallOptions(), OperationType.Install));

        Assert.Equal(OperationVeredict.Success, operations.Verdict(fresh, OperationType.Uninstall, 0, [], _ => { }));
        Assert.False(manager.HasRecordedInstallPath(fresh));
        Assert.Equal(["get", Amphetamine], operations.GetParameters(fresh, new InstallOptions(), OperationType.Install));

        Package taggedInstalled = App(manager);
        taggedInstalled.Tag = PackageTag.AlreadyInstalled;
        Assert.Equal(["get", "--force", Amphetamine], operations.GetParameters(taggedInstalled, new InstallOptions(), OperationType.Install));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public void NeedsRootUnlessUniGetUIAlreadyRunsAsRoot(bool isRoot, bool expected)
    {
        MacAppStorePkgOperationHelper.IsRoot = () => isRoot;
        Assert.Equal(expected, MacAppStorePkgOperationHelper.NeedsRoot());
    }

    [Fact]
    public async Task RunningAlreadyAsRootInvokesPerformRootDirectlyAndStreamsProgress()
    {
        MacAppStorePkgOperationHelper.IsRoot = () => true;
        var (manager, requests) = NewManager();
        ProcessStartInfo? started = null;
        ElevatedMas.RunProcess = (info, onLine, _) =>
        {
            started = info;
            onLine("@@UNIGETUI_PROGRESS@@ downloading 50 5242880 10485760");
            onLine("@@UNIGETUI_PROGRESS@@ installing -1 0 0");
            onLine("Installed Keynote (14.4)");
            return Task.FromResult(0);
        };
        List<string> log = [];
        List<OperationProgress> progresses = [];

        var verdict = await Operations(manager).TryPerformAsync(
            App(manager, Keynote, "Keynote"),
            new InstallOptions(),
            OperationType.Install,
            log.Add,
            progresses.Add,
            CancellationToken.None);

        Assert.Equal(OperationVeredict.Success, verdict);
        Assert.Single(requests);
        Assert.NotNull(started);
        Assert.Equal(HelperExecutable, started.FileName);
        Assert.Equal(["perform-root", "501", "get", Keynote], started.ArgumentList);
        Assert.Equal(["Installed Keynote (14.4)"], log);
        Assert.Equal(2, progresses.Count);
        Assert.Equal(OperationProgressStage.Downloading, progresses[0].Stage);
        Assert.Equal(50.0, progresses[0].Percentage);
        Assert.Equal(5242880UL, progresses[0].BytesDownloaded);
        Assert.Equal(10485760UL, progresses[0].BytesTotal);
        Assert.Equal(OperationProgressStage.Installing, progresses[1].Stage);
        Assert.Null(progresses[1].Percentage);
    }

    [Fact]
    public async Task RunsHelperWithNativeAuthorizationAndTheRequestingUsersIdentity()
    {
        MacAppStorePkgOperationHelper.IsRoot = () => false;
        var (manager, _) = NewManager();
        ProcessStartInfo? started = null;
        ElevatedMas.RunProcess = (startInfo, onLine, _) =>
        {
            started = startInfo;
            onLine("Installed Amphetamine (5.3.2)");
            return Task.FromResult(0);
        };
        List<string> log = [];

        var verdict = await Operations(manager).TryPerformAsync(App(manager), new InstallOptions(),
            OperationType.Install, log.Add, _ => { }, CancellationToken.None);

        Assert.Equal(OperationVeredict.Success, verdict);
        Assert.NotNull(started);
        Assert.Equal(ElevatedMas.OsaScriptPath, started.FileName);
        Assert.False(started.UseShellExecute);
        Assert.Equal(
            ["-e", ElevatedMas.AuthorizationScript, "--",
                $"'/bin/launchctl' 'asuser' '501' '{HelperExecutable}' 'perform-root' '501' 'get' '{Amphetamine}' 2>&1",
                "UniGetUI wants to install Amphetamine from the Mac App Store."],
            started.ArgumentList);
        Assert.Contains("Command output will appear", log[0]);
        Assert.Equal("Installed Amphetamine (5.3.2)", log[1]);
        Assert.False(File.Exists(Path.Combine(CoreData.UniGetUIDataDirectory, "mac-askpass.sh")));
    }

    [Theory]
    [InlineData("auth include sudo_local", "auth sufficient pam_tid.so", true)]
    [InlineData("auth include sudo_local", "#auth sufficient pam_tid.so", false)]
    [InlineData("#auth include sudo_local", "auth sufficient pam_tid.so", false)]
    [InlineData("auth required pam_opendirectory.so", "auth sufficient pam_tid.so", false)]
    [InlineData("auth sufficient pam_tid.so", "", true)]
    [InlineData("auth\tsufficient\t/usr/lib/pam/pam_tid.so # comment\r\n", "", true)]
    [InlineData(" auth include sudo_local # comment", "\n# comment\n auth sufficient pam_tid.so\n", true)]
    [InlineData("auth include sudo_local", "auth required pam_tid.so", false)]
    [InlineData("auth include sudo_local", "account sufficient pam_tid.so", false)]
    [InlineData("auth include sudo_local", "auth sufficient not_pam_tid.so", false)]
    [InlineData("", "", false)]
    public void DetectsOnlyActiveStandardSudoTouchIdConfiguration(string sudo, string local, bool expected)
    {
        Assert.Equal(expected, ElevatedMas.HasTouchIdConfiguration(sudo, local));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(70, 70)]
    [InlineData(128, -128)]
    public async Task NativeHelperResultsNeverRepeatAnOperation(int helperExit, int expected)
    {
        NativeMas.HelperPath = () => HelperExecutable;
        ElevatedMas.TouchIdSudoEnabled = () => true;
        int calls = 0;
        ElevatedMas.RunProcess = (info, onLine, token) =>
        {
            calls++;
            Assert.False(token.CanBeCanceled);
            Assert.Equal(HelperExecutable, info.FileName);
            Assert.Equal(["run", "get", Amphetamine, "Install app"], info.ArgumentList);
            Assert.True(info.RedirectStandardInput);
            Assert.False(info.UseShellExecute);
            onLine("native output");
            return Task.FromResult(helperExit);
        };
        var result = await ElevatedMas.RunAsync(HelperExecutable, ["get", Amphetamine], "Install app", _ => { }, CancellationToken.None);
        Assert.Equal(expected, result.ExitCode);
        Assert.Equal(["native output"], result.Output);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task NativeHelperFallsBackOnlyWhenItExplicitlyDidNotStartMas()
    {
        NativeMas.HelperPath = () => HelperExecutable;
        List<string> calls = [];
        ElevatedMas.RunProcess = (info, _, _) =>
        {
            calls.Add(info.FileName);
            return Task.FromResult(info.FileName == HelperExecutable ? NativeMas.Unavailable : 0);
        };
        var result = await ElevatedMas.RunAsync(HelperExecutable, ["get", Amphetamine], "Install app", _ => { }, CancellationToken.None);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal([HelperExecutable, ElevatedMas.OsaScriptPath], calls);
    }

    [Fact]
    public async Task PasswordFallbackBootstrapsHelperWhenTouchIdIsAvailableAndNotDisabled()
    {
        NativeMas.HelperPath = () => HelperExecutable;
        NativeMas.CanBootstrapWithHelper = _ => Task.FromResult(true);
        ProcessStartInfo? osaScript = null;
        ElevatedMas.RunProcess = (info, _, _) =>
        {
            if (info.FileName == HelperExecutable)
                return Task.FromResult(NativeMas.Unavailable);
            osaScript = info;
            return Task.FromResult(0);
        };

        var result = await ElevatedMas.RunAsync(HelperExecutable, ["get", "--force", Amphetamine], "Reinstall app", _ => { }, CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(osaScript);
        Assert.StartsWith($"'{HelperExecutable}' install-daemon >/dev/null 2>&1 || true; ", osaScript.ArgumentList[3]);
        Assert.Contains("'perform-root' '501' 'get' '--force'", osaScript.ArgumentList[3]);

        Settings.Set(Settings.K.DisableMasTouchIdHelper, true);
        Assert.Null(NativeMas.StartInfo(["get", Amphetamine], "Install app"));
        Assert.Null(await NativeMas.GetBootstrapHelperPathAsync(["get", Amphetamine]));
    }

    [Fact]
    public async Task NativeHelperCancellationPreventsPasswordFallback()
    {
        NativeMas.HelperPath = () => HelperExecutable;
        using var cancel = new CancellationTokenSource();
        int calls = 0;
        ElevatedMas.RunProcess = (_, _, _) =>
        {
            calls++;
            cancel.Cancel();
            return Task.FromResult(NativeMas.Unavailable);
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ElevatedMas.RunAsync(HelperExecutable, ["get", Amphetamine], "Install app", _ => { }, cancel.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public void NativeHelperRejectsUnsupportedVerbsAndSwitches()
    {
        NativeMas.HelperPath = () => HelperExecutable;
        Assert.NotNull(NativeMas.StartInfo(["get", "--force", Amphetamine], "Reinstall app"));
        Assert.Null(NativeMas.StartInfo(["uninstall", "--force", Amphetamine], "Uninstall app"));
        Assert.Null(NativeMas.StartInfo(["install", Amphetamine, "--anything"], "Install app"));
        Assert.Null(NativeMas.StartInfo(["install", "--anything", Amphetamine], "Install app"));
        Assert.Null(NativeMas.StartInfo(["exec", Amphetamine], "Install app"));
        Assert.Null(NativeMas.StartInfo([], "Install app"));
    }

    [Fact]
    public async Task NativeHelperPolicyAndStagingAreChecked()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        using var process = Process.Start(new ProcessStartInfo(Path.Join(AppContext.BaseDirectory, "MasHelperNativeTests"))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, await stdout + "\n" + await stderr);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    [InlineData(127)]
    public async Task SudoTouchIdRunsTheCommandOnlyOnceEvenWhenHelperFails(int exitCode)
    {
        Settings.Set(Settings.K.DisableMasTouchIdHelper, true);
        ElevatedMas.TouchIdSudoEnabled = () => true;
        ProcessStartInfo? started = null;
        int calls = 0;
        ElevatedMas.RunProcess = (info, onLine, token) =>
        {
            calls++;
            started = info;
            Assert.False(token.CanBeCanceled);
            onLine(info.ArgumentList[8]);
            onLine("helper output");
            return Task.FromResult(exitCode);
        };
        List<string> log = [];

        var result = await ElevatedMas.RunAsync(HelperExecutable, ["update", Amphetamine], "Update app", log.Add, CancellationToken.None);

        Assert.Equal(1, calls);
        Assert.NotNull(started);
        Assert.Equal(ElevatedMas.SudoPath, started.FileName);
        Assert.Equal(["-S", "-p", "", "--", "/bin/sh", "-c", ElevatedMas.SudoCommandScript, "unigetui-mas"],
            started.ArgumentList.Take(8));
        Assert.StartsWith("unigetui-mas-start-", started.ArgumentList[8]);
        Assert.Equal([HelperExecutable, "perform-root", "501", "update", Amphetamine],
            started.ArgumentList.Skip(9));
        Assert.True(started.RedirectStandardInput);
        Assert.False(started.UseShellExecute);
        Assert.False(started.Environment.ContainsKey("SUDO_ASKPASS"));
        Assert.Equal(exitCode, result.ExitCode);
        Assert.Equal(["helper output"], result.Output);
        Assert.Contains("Update app", log);
        Assert.DoesNotContain(log, line => line.Contains("unigetui-mas-start-"));
    }

    [Fact]
    public async Task UnavailableSudoTouchIdFallsBackOnlyBeforeTheCommandStarts()
    {
        Settings.Set(Settings.K.DisableMasTouchIdHelper, true);
        ElevatedMas.TouchIdSudoEnabled = () => true;
        List<string> processes = [];
        ElevatedMas.RunProcess = (info, onLine, _) =>
        {
            processes.Add(info.FileName);
            if (info.FileName == ElevatedMas.SudoPath)
            {
                onLine("sudo: a password is required");
                return Task.FromResult(1);
            }
            Assert.Equal(ElevatedMas.OsaScriptPath, info.FileName);
            Assert.Contains("'perform-root' '501'", info.ArgumentList[3]);
            onLine("installed");
            return Task.FromResult(0);
        };
        List<string> log = [];

        var result = await ElevatedMas.RunAsync(HelperExecutable, ["get", Amphetamine], "Install app", log.Add, CancellationToken.None);

        Assert.Equal([ElevatedMas.SudoPath, ElevatedMas.OsaScriptPath], processes);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("installed", result.Output[^1]);
        Assert.Contains(log, line => line.Contains("Falling back"));
    }

    [Fact]
    public async Task CancellationAfterTouchIdFailureDoesNotOpenAnotherDialog()
    {
        Settings.Set(Settings.K.DisableMasTouchIdHelper, true);
        ElevatedMas.TouchIdSudoEnabled = () => true;
        using var cancellation = new CancellationTokenSource();
        int calls = 0;
        ElevatedMas.RunProcess = (info, _, _) =>
        {
            calls++;
            Assert.Equal(ElevatedMas.SudoPath, info.FileName);
            cancellation.Cancel();
            return Task.FromResult(1);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ElevatedMas.RunAsync(HelperExecutable, ["update", Amphetamine], "Update app", _ => { }, cancellation.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SudoWithoutAStartMarkerCannotReportSuccess()
    {
        Settings.Set(Settings.K.DisableMasTouchIdHelper, true);
        ElevatedMas.TouchIdSudoEnabled = () => true;
        ElevatedMas.RunProcess = (_, _, _) => Task.FromResult(0);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ElevatedMas.RunAsync(HelperExecutable, ["update", Amphetamine], "Update app", _ => { }, CancellationToken.None));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(7)]
    public async Task SudoWrapperPreservesLiteralArgumentsAndExitCode(int exitCode)
    {
        if (!OperatingSystem.IsMacOS())
            return;
        Settings.Set(Settings.K.DisableMasTouchIdHelper, true);
        ElevatedMas.TouchIdSudoEnabled = () => true;
        ElevatedMas.RunProcess = (info, onLine, token) =>
        {
            Assert.Equal(ElevatedMas.SudoPath, info.FileName);
            info.FileName = "/bin/sh";
            for (int i = 0; i < 5; i++)
                info.ArgumentList.RemoveAt(0);
            // Replace `<helper> perform-root 501 ...` with `/bin/sh -c ...` to exercise SudoCommandScript directly.
            info.ArgumentList[4] = "/bin/sh";
            info.ArgumentList[5] = "-c";
            info.ArgumentList[6] = "printf '%s\\n' \"$1\"; printf 'stderr\\n' >&2; exit \"$2\"";
            return _runProcess(info, onLine, token);
        };
        const string literal = "$(exit 99); \"quoted\" & 'single'\nsecond line";
        var result = await ElevatedMas.RunAsync("/app/helper",
            ["test", literal, exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            "A \"quoted\" prompt", _ => { }, CancellationToken.None);

        Assert.Equal(exitCode, result.ExitCode);
        Assert.Contains("$(exit 99); \"quoted\" & 'single'", result.Output);
        Assert.Contains("second line", result.Output);
        Assert.Contains("stderr", result.Output);
    }

    [Fact]
    public async Task ElevationCanBeProhibited()
    {
        MacAppStorePkgOperationHelper.IsRoot = () => false;
        Settings.Set(Settings.K.ProhibitElevation, true);
        var (manager, _) = NewManager();
        List<string> log = [];

        var verdict = await Operations(manager).TryPerformAsync(App(manager), new InstallOptions(),
            OperationType.Update, log.Add, _ => { }, CancellationToken.None);

        Assert.Equal(OperationVeredict.Failure, verdict);
        Assert.Contains("elevation is prohibited", Assert.Single(log));
    }

    [Fact]
    public async Task APreCanceledOperationNeverOpensAuthorization()
    {
        MacAppStorePkgOperationHelper.IsRoot = () => false;
        var (manager, _) = NewManager();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Operations(manager).TryPerformAsync(App(manager),
            new InstallOptions(), OperationType.Uninstall, _ => { }, _ => { }, cancellation.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationDoesNotAbandonAnAuthorizedInstaller(bool touchId)
    {
        Settings.Set(Settings.K.DisableMasTouchIdHelper, true);
        ElevatedMas.TouchIdSudoEnabled = () => touchId;
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        ElevatedMas.RunProcess = (info, onLine, token) =>
        {
            Assert.False(token.CanBeCanceled);
            if (touchId)
                onLine(info.ArgumentList[8]);
            started.SetResult();
            return finished.Task;
        };
        List<string> log = [];

        var run = ElevatedMas.RunAsync(HelperExecutable, ["update", Amphetamine], "Update app", log.Add, cancellation.Token);
        await started.Task;
        cancellation.Cancel();
        Assert.False(run.IsCompleted);
        Assert.Contains(log, line => line.Contains("Waiting for the authorized Mac App Store operation"));
        finished.SetResult(0);
        Assert.Equal(0, (await run).ExitCode);
    }

    [Fact]
    public void DistinguishesAuthorizationCancellationAndStoreFailure()
    {
        var (manager, _) = NewManager();
        var operations = Operations(manager);
        List<string> log = [];

        Assert.Equal(OperationVeredict.Success, operations.Verdict(0, [], log.Add));
        Assert.Equal(OperationVeredict.Canceled,
            operations.Verdict(1, ["0:19: execution error: User canceled. (-128)"], log.Add));
        Assert.Equal(OperationVeredict.Failure, operations.Verdict(1, ["Error: No apps found"], log.Add));
        Assert.Empty(log);

        Assert.Equal(OperationVeredict.Failure,
            operations.Verdict(1, ["execution error: Error: No downloads initiated for ADAM ID 6450432292 (1)"], log.Add));
        Assert.Contains("Open the App Store", Assert.Single(log));
        Assert.Contains("Apple Account", log[0]);
        Assert.Contains("administrator authorization and Apple Account sign-in are separate", log[0]);
        Assert.Contains("complete any account prompts", log[0]);
        log.Clear();
        Assert.Equal(OperationVeredict.Failure, operations.Verdict(128, ["Command failed (128)"], log.Add));
        Assert.Equal(OperationVeredict.Canceled, operations.Verdict(NativeMas.CancellationResult, [], log.Add));
        Assert.Empty(log);
    }

    [Fact]
    public async Task PromptsAndArgumentsAreDataNotAppleScriptOrShellCode()
    {
        Settings.Set(Settings.K.DisableMasTouchIdHelper, true);
        const string prompt = "100% sure about \"quoted\" \\\\ names?\nNew line";
        const string path = "/Applications/John's Apps/UniGetUIMasHelper";
        const string argument = "$(exit 99); \"quoted\" & 'single'";
        ProcessStartInfo? started = null;
        ElevatedMas.RunProcess = (info, _, _) => { started = info; return Task.FromResult(0); };

        await ElevatedMas.RunAsync(path, [argument], prompt, _ => { }, CancellationToken.None);

        Assert.NotNull(started);
        Assert.Equal(ElevatedMas.AuthorizationScript, started.ArgumentList[1]);
        Assert.Equal("--", started.ArgumentList[2]);
        Assert.Equal(
            "'/bin/launchctl' 'asuser' '501' '/Applications/John'\\''s Apps/UniGetUIMasHelper' 'perform-root' '501' '$(exit 99); \"quoted\" & '\\''single'\\''' 2>&1",
            started.ArgumentList[3]);
        Assert.Equal(prompt, started.ArgumentList[4]);
    }

    [Fact]
    public async Task NativeScriptPreservesOutputAndExitCode()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        Settings.Set(Settings.K.DisableMasTouchIdHelper, true);
        ElevatedMas.RunProcess = (info, onLine, token) =>
        {
            info.ArgumentList[1] = info.ArgumentList[1].Replace(" with administrator privileges", "");
            const string prefix = "'/bin/launchctl' 'asuser' '501' '/usr/bin/printf' 'perform-root' '501' ";
            if (info.ArgumentList[3].StartsWith(prefix, StringComparison.Ordinal))
                info.ArgumentList[3] = "'/usr/bin/printf' " + info.ArgumentList[3][prefix.Length..];
            else
                info.ArgumentList[3] = info.ArgumentList[3].Replace("'/bin/launchctl' 'asuser' '501' '/bin/sh' 'perform-root' '501' ", "'/bin/sh' ");
            return _runProcess(info, onLine, token);
        };
        string[] messages = ["one", "$(exit 99); \"quoted\" & 'single'", "three"];
        var success = await ElevatedMas.RunAsync("/usr/bin/printf", ["%s\n", .. messages],
            "A \"quoted\" prompt", _ => { }, CancellationToken.None);
        Assert.Equal(0, success.ExitCode);
        Assert.Equal(messages, success.Output);

        var failure = await ElevatedMas.RunAsync("/bin/sh",
            ["-c", "printf 'standard output\\n'; printf 'standard error\\n' >&2; exit 7"],
            "Failure", _ => { }, CancellationToken.None);
        Assert.NotEqual(0, failure.ExitCode);
        Assert.Contains(failure.Output, line => line.Contains("standard output"));
        Assert.Contains(failure.Output, line => line.Contains("standard error"));
        Assert.EndsWith("(7)", failure.Output[^1]);
    }

    [Fact]
    public void NativeUserIdsIdentifyTheRequestingProcess()
    {
        if (!OperatingSystem.IsMacOS())
            return;
        var (user, group) = _userIds();
        foreach (var (flag, expected) in new[] { ("-u", user), ("-g", group) })
        {
            using var process = Process.Start(new ProcessStartInfo("/usr/bin/id")
            {
                ArgumentList = { flag },
                RedirectStandardOutput = true,
                UseShellExecute = false,
            })!;
            string actual = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
            Assert.Equal(expected.ToString(System.Globalization.CultureInfo.InvariantCulture), actual);
        }
    }

    // ── Manager ────────────────────────────────────────────────────────────

    [Fact]
    public void DescribesTheBuiltInManagerWithoutThirdPartyDependencies()
    {
        var manager = new MacAppStore();

        Assert.Equal("mas", manager.Properties.Id);
        Assert.Equal("Mac App Store", manager.Properties.DisplayName);
        Assert.Equal("MacAppStore", manager.DefaultSource.AsString);
        Assert.Equal("Mac App Store", manager.DefaultSource.AsString_DisplayName);
        Assert.Null(manager.Properties.InstallCommands.MacOS);
        Assert.Null(manager.Properties.InstallCommands.Windows);
        Assert.Null(manager.Properties.InstallCommands.Linux);
        Assert.Equal("PACKAGE_MANAGERS.md#mac-app-store", manager.Properties.SetupGuide);
        Assert.False(manager.Capabilities.SupportsCustomSources);
        Assert.False(manager.Capabilities.CanListVersions);
        Assert.True(manager.Capabilities.SerializesOperations);
        if (OperatingSystem.IsMacOS())
        {
            string fakeHelper = Path.Combine(_testRoot, "UniGetUIMasHelper");
            File.WriteAllText(fakeHelper, "");
            NativeMas.HelperPath = () => fakeHelper;
            manager.Initialize();
            Assert.True(manager.IsReady());
            Assert.Contains(manager.GetMaintenanceActions(), action => action.Id == "reset-cache");
        }
        else
        {
            Assert.Empty(manager.FindCandidateExecutableFiles());
        }
    }

    // ── Built-in Spotlight helper + native iTunes catalog listings ─────────

    private sealed class ListingMacAppStore : MacAppStore
    {
        public IReadOnlyList<Package> Search(string query) => FindPackages_UnSafe(query);
        public IReadOnlyList<Package> Installed() => GetInstalledPackages_UnSafe();
        public IReadOnlyList<Package> Updates() => GetAvailableUpdates_UnSafe();

        public string LoadVersion()
        {
            _loadManagerVersion(out string version);
            return version;
        }
    }

    private string FakeHelper(Dictionary<string, string> outputs, Dictionary<string, int>? codes = null)
    {
        string dir = Path.Combine(_testRoot, "fake-helper");
        Directory.CreateDirectory(dir);
        foreach (var (name, text) in outputs)
            File.WriteAllText(Path.Combine(dir, name), text);
        foreach (var (verb, code) in codes ?? [])
            File.WriteAllText(Path.Combine(dir, verb + ".code"), code.ToString(System.Globalization.CultureInfo.InvariantCulture));

        string script = Path.Combine(dir, "UniGetUIMasHelper");
        File.WriteAllText(script,
            $"""
            #!/bin/sh
            dir='{dir}'
            printf '%s\n' "$*" >> "$dir/calls"
            [ -f "$dir/$1.out" ] && cat "$dir/$1.out"
            [ -f "$dir/$1.err" ] && cat "$dir/$1.err" >&2
            [ -f "$dir/$1.code" ] && exit "$(cat "$dir/$1.code")"
            exit 0
            """.ReplaceLineEndings("\n") + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return script;
    }

    private string[] Calls() => File.ReadAllLines(Path.Combine(_testRoot, "fake-helper", "calls"));

    private static ListingMacAppStore Listing(string helperPath, bool isAppleSilicon = true, Version? macOSVersion = null)
    {
        var manager = new ListingMacAppStore
        {
            IsAppleSilicon = isAppleSilicon,
            CurrentMacOSVersion = macOSVersion ?? new Version(26, 7, 3),
            Status = new ManagerStatus { Found = true, ExecutablePath = helperPath, ExecutableCallArgs = "", Version = "" },
        };
        manager.Catalog.Download = _ => throw new InvalidOperationException("No lookup may run in this test.");
        return manager;
    }

    [Fact]
    public void ListsInstalledUpdatesAndNativeCatalogSearchWithoutMasCli()
    {
        if (OperatingSystem.IsWindows())
            return;
        string installed = Path.Combine(_testRoot, "Amphetamine.app");
        Directory.CreateDirectory(installed);
        string list = Fixture("list.jsonl").Replace("\"/Applications/Amphetamine.app\"", $"\"{installed}\"");
        var manager = Listing(FakeHelper(new()
        {
            ["storefront.out"] = "143455-6,13\n",
            ["list.out"] = list,
        }), isAppleSilicon: false, macOSVersion: new Version(26, 0, 0));

        List<Uri> catalogRequests = [];
        manager.Catalog.Download = url =>
        {
            catalogRequests.Add(url);
            if (url.AbsolutePath == "/lookup")
            {
                return """
                {
                  "resultCount": 3,
                  "results": [
                    {"trackId": 937984704, "trackName": "Amphetamine", "kind": "mac-software", "version": "5.3.2", "minimumOsVersion": "12.0", "artworkUrl512": "https://example.com/a.png", "description": "Keep Mac awake"},
                    {"trackId": 409183694, "trackName": "Keynote", "kind": "mac-software", "version": "15.0", "minimumOsVersion": "27.0", "artworkUrl512": "https://example.com/k.png", "description": "Requires newer macOS"},
                    {"trackId": 6450432292, "trackName": "Universal Print", "kind": "mac-software", "version": "1.1.1", "minimumOsVersion": "13.0", "artworkUrl512": "https://example.com/u.png", "description": "Cloud print"}
                  ]
                }
                """;
            }
            return """
            {
              "resultCount": 2,
              "results": [
                {"trackId": 937984704, "trackName": "Amphetamine", "kind": "mac-software", "version": "5.3.2", "artworkUrl512": "https://example.com/a.png", "description": "Keep Mac awake"},
                {"trackId": 424389933, "trackName": "Final Cut Pro", "kind": "mac-software", "version": "11.0", "price": 399.99, "artworkUrl512": "https://example.com/f.png", "description": "Video"}
              ]
            }
            """;
        };

        Assert.StartsWith("macOS ", manager.LoadVersion());
        Assert.Equal("CA", manager.Storefront);

        var apps = manager.Installed();
        Assert.Equal([Amphetamine, Keynote, "6450432292"], apps.Select(app => app.Id));
        Assert.All(apps, app => Assert.Same(manager.DefaultSource, app.Source));
        Assert.Equal(installed, manager.InstallPathOf(apps[0]));

        // Keynote 15.0 requires macOS 27.0 while CurrentMacOSVersion is 26.0.0 -> skipped!
        // Universal Print 1.1.1 requires macOS 13.0 and > 1.0 -> offered!
        var updates = manager.Updates();
        Package update = Assert.Single(updates);
        Assert.Equal("6450432292", update.Id);
        Assert.Equal("1.0", update.VersionString);
        Assert.Equal("1.1.1", update.NewVersionString);

        var found = manager.Search("final cut");
        Assert.Equal([Amphetamine, FinalCut], found.Select(app => app.Id));
        Assert.NotNull(manager.Catalog.Peek(FinalCut));

        Assert.Equal(["storefront", "list", "list"], Calls());
        Assert.Equal(2, catalogRequests.Count);
        Assert.All(catalogRequests, req => Assert.Contains("country=ca", req.Query));
    }

    [Fact]
    public void SearchesAppStoreIdsThroughTheCatalog()
    {
        var (manager, requests) = NewManager(storefront: "CA");

        var found = Assert.Single(manager.Search($" {Amphetamine} "));
        Assert.Equal(Amphetamine, found.Id);
        Assert.Equal("Amphetamine", found.Name);
        Assert.Equal("5.3.2", found.VersionString);
        Assert.Same(manager.DefaultSource, found.Source);
        Assert.Single(manager.Search(Amphetamine));
        Assert.Contains($"id={Amphetamine}", Assert.Single(requests).Query);
        Assert.Contains("country=ca", requests[0].Query);
    }

    [Theory]
    [InlineData("software", "iOS-only app")]
    [InlineData("song", "A song")]
    [InlineData(null, "Missing kind")]
    [InlineData("mac-software", null)]
    [InlineData("mac-software", " ")]
    public void ExactIdSearchRejectsNonMacAndIncompleteRecords(string? kind, string? name)
    {
        var (manager, _) = NewManager(isAppleSilicon: true);
        manager.Catalog.Download = _ => """{"resultCount":0,"results":[]}""";
        var record = LookupRecord(Amphetamine);
        record["kind"] = kind;
        record["trackName"] = name;
        record.Remove("supportedDevices");
        manager.Catalog.Remember(record);

        Assert.Empty(manager.Search(Amphetamine));
    }

    [Fact]
    public void NumericNamesStillSearchWhenNoAppHasThatId()
    {
        var (manager, requests) = NewManager(isAppleSilicon: false);
        manager.Catalog.Download = url =>
        {
            requests.Add(url);
            return url.AbsolutePath == "/lookup"
                ? """{"resultCount":0,"results":[]}"""
                : """{"resultCount":1,"results":[{"trackId":999,"trackName":"2048 Game","kind":"mac-software","version":"1.2","artworkUrl512":"https://example.com/2048.png","description":"Puzzle"}]}""";
        };

        Assert.Single(manager.Search("2048"));
        Assert.Single(manager.Search("2048"));
        // 1 missed lookup (cached) + 2 name searches
        Assert.Equal(3, requests.Count);
    }

    [Fact]
    public void ASearchWithNoResultsIsNotAFailure()
    {
        var (manager, _) = NewManager(isAppleSilicon: false);
        manager.Catalog.Download = _ => """{"resultCount":0,"results":[]}""";

        Assert.Empty(manager.Search("zzqqxx"));
        Assert.Empty(manager.Search("--"));
    }

    [Fact]
    public void AFailedListingIsReported()
    {
        if (OperatingSystem.IsWindows())
            return;
        var manager = Listing(FakeHelper(new()
        {
            ["list.err"] = "Error: Unable to read the Spotlight index\n",
        }, new() { ["list"] = 1 }));

        var error = Assert.Throws<InvalidOperationException>(manager.Installed);
        Assert.Contains("Spotlight", error.Message);
    }

    [Fact]
    public void ExactIdAndSearchResolveAppleSiliconMacDesktopSoftwareOnlyOnArm64()
    {
        const string universalAppId = "1462282993";
        const string universalJson = """
        {
          "resultCount": 2,
          "results": [
            {"trackId": 1462282993, "trackName": "Devolutions Password Manager", "kind": "software", "version": "2026.3.5", "minimumOsVersion": "16.0", "supportedDevices": ["iPhone16-iPhone16", "MacDesktop-MacDesktop"], "artworkUrl512": "https://example.com/dpm.png", "description": "Vault"},
            {"trackId": 389801252, "trackName": "Instagram", "kind": "software", "version": "400.0", "minimumOsVersion": "16.3", "supportedDevices": ["iPhone16-iPhone16"], "artworkUrl512": "https://example.com/ig.png", "description": "Photos"}
          ]
        }
        """;

        var (armManager, _) = NewManager(isAppleSilicon: true);
        armManager.Catalog.Download = url =>
            url.Query.Contains("entity=desktopSoftware", StringComparison.Ordinal)
            || (url.AbsolutePath == "/search" && !url.Query.Contains("devolutions", StringComparison.OrdinalIgnoreCase))
                ? """{"resultCount":0,"results":[]}"""
                : universalJson;

        Package byId = Assert.Single(armManager.Search(universalAppId));
        Assert.Equal(universalAppId, byId.Id);
        Assert.Equal("Devolutions Password Manager", byId.Name);
        Assert.Empty(armManager.Search("389801252"));

        Package bySearch = Assert.Single(armManager.Search("devolutions"));
        Assert.Equal(universalAppId, bySearch.Id);

        var (intelManager, _) = NewManager(isAppleSilicon: false);
        intelManager.Catalog.Download = _ => universalJson;
        Assert.Empty(intelManager.Search(universalAppId));
    }

    [Fact]
    public async Task ManagedOperationSurfacesStoreFailureExplanationInFailureMessage()
    {
        MacAppStorePkgOperationHelper.IsRoot = () => false;
        var (manager, _) = NewManager();
        ElevatedMas.RunProcess = (_, onLine, _) =>
        {
            onLine("Error: No downloads initiated for ADAM ID 6450432292");
            return Task.FromResult(1);
        };

        using var operation = new UpdatePackageOperation(App(manager, "6450432292", "Universal Print"), new InstallOptions());
        await operation.MainThread();

        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Contains("administrator authorization and Apple Account sign-in are separate", operation.Metadata.FailureMessage);
    }

    [Fact]
    public void ScreenshotsAcceptMacAndIpadLookupFields()
    {
        var macRecord = JsonNode.Parse("""{"macScreenshotUrls":["https://example.com/mac1.png"],"screenshotUrls":[]}""")!.AsObject();
        var ipadRecord = JsonNode.Parse("""{"screenshotUrls":[],"ipadScreenshotUrls":["https://example.com/ipad1.png"]}""")!.AsObject();

        Assert.Equal([new Uri("https://example.com/mac1.png")], AppStoreRecord.Screenshots(macRecord));
        Assert.Equal([new Uri("https://example.com/ipad1.png")], AppStoreRecord.Screenshots(ipadRecord));
    }

    [Fact]
    public async Task DisablingTouchIdWithoutInstalledDaemonRequiresNoElevationAndRestoresOnCancel()
    {
        NativeMas.HasInstalledDaemonFiles = () => false;
        string message = await MacAppStore.ConfigureTouchIdAsync(enable: false);
        Assert.Contains("disabled", message, StringComparison.OrdinalIgnoreCase);
        Assert.True(Settings.Get(Settings.K.DisableMasTouchIdHelper));

        Settings.Set(Settings.K.DisableMasTouchIdHelper, false);
        NativeMas.HasInstalledDaemonFiles = () => true;
        string fakeHelper = Path.Combine(_testRoot, "UniGetUIMasHelper");
        File.WriteAllText(fakeHelper, "");
        NativeMas.HelperPath = () => fakeHelper;
        ElevatedMas.RunProcess = (_, onLine, _) =>
        {
            onLine("0:19: execution error: User canceled. (-128)");
            return Task.FromResult(1);
        };

        await Assert.ThrowsAsync<OperationCanceledException>(() => MacAppStore.ConfigureTouchIdAsync(enable: false));
        Assert.False(Settings.Get(Settings.K.DisableMasTouchIdHelper));
    }
}
