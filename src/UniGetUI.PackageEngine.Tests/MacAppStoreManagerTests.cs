using System.Diagnostics;
using System.Text.Json.Nodes;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.Managers.MacAppStoreManager;
using UniGetUI.PackageEngine.Managers.MacAppStoreManager.Internal;
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
    private const string MasPath = "/opt/homebrew/bin/mas";

    private readonly string _testRoot = Path.Combine(
        AppContext.BaseDirectory,
        nameof(MacAppStoreManagerTests),
        Guid.NewGuid().ToString("N")
    );

    private readonly Func<ProcessStartInfo, Action<string>, CancellationToken, Task<int>> _runProcess =
        ElevatedMas.RunProcess;
    private readonly Func<bool> _isRoot = MacAppStorePkgOperationHelper.IsRoot;

    public MacAppStoreManagerTests()
    {
        Directory.CreateDirectory(_testRoot);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Settings.ResetSettings();
        ElevatedMas.RunProcess = (_, _, _) => throw new InvalidOperationException("No process may run in this test.");
    }

    public void Dispose()
    {
        ElevatedMas.RunProcess = _runProcess;
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
    private static (MacAppStore Manager, List<Uri> Requests) NewManager(int masMajorVersion = 7)
    {
        var manager = new MacAppStore
        {
            MasMajorVersion = masMajorVersion,
            Status = new ManagerStatus { Found = true, ExecutablePath = MasPath, ExecutableCallArgs = "", Version = "" },
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
    [InlineData("{\"region\":\"fr\"}", "FR")]
    [InlineData("{\"store\":\"CAN\",\"region\":\"de\"}", "DE")]
    [InlineData("region ▁ jp", "JP")]
    [InlineData("{\"mas\":\"7.0.0\"}", null)]
    [InlineData("Error: unknown command", null)]
    [InlineData("", null)]
    public void ReadsTheStorefront(string output, string? expected) =>
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

    [Fact]
    public void OneLookupFetchesEveryAppTheListingsShowed()
    {
        var (manager, requests) = NewManager();
        manager.Catalog.Want([Amphetamine, Keynote]);

        Assert.NotNull(manager.Catalog.Get(Keynote));
        Assert.NotNull(manager.Catalog.Get(Amphetamine));

        Uri request = Assert.Single(requests);
        Assert.Equal("itunes.apple.com", request.Host);
        Assert.Contains("country=ca", request.Query);
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
    [InlineData(7, OperationType.Install, "get")]
    [InlineData(7, OperationType.Update, "update")]
    [InlineData(7, OperationType.Uninstall, "uninstall")]
    [InlineData(0, OperationType.Install, "get")]
    [InlineData(0, OperationType.Update, "update")]
    [InlineData(2, OperationType.Install, "purchase")]
    [InlineData(2, OperationType.Update, "upgrade")]
    [InlineData(2, OperationType.Uninstall, "uninstall")]
    public void ChoosesTheVerbOfTheInstalledMas(int major, OperationType operation, string verb)
    {
        var (manager, _) = NewManager(major);

        Assert.Equal(verb, Operations(manager).Verb(App(manager), operation));
    }

    [Theory]
    [InlineData(7)]
    [InlineData(2)]
    public void APaidAppIsReinstalledRatherThanBought(int major)
    {
        var (manager, _) = NewManager(major);
        manager.Catalog.Remember(SearchRecord(FinalCut));

        Assert.Equal("install", Operations(manager).Verb(App(manager, FinalCut, "Final Cut Pro"), OperationType.Install));
    }

    [Fact]
    public void BuildsTheCommandLineWithCustomParameters()
    {
        var (manager, _) = NewManager();
        var options = new InstallOptions { CustomParameters_Install = ["--force"], CustomParameters_Update = ["--verbose"] };

        Assert.Equal(["get", Amphetamine, "--force"],
            Operations(manager).GetParameters(App(manager), options, OperationType.Install));
        Assert.Equal(["update", Amphetamine, "--verbose"],
            Operations(manager).GetParameters(App(manager), options, OperationType.Update));
        Assert.Equal(["uninstall", Amphetamine],
            Operations(manager).GetParameters(App(manager), options, OperationType.Uninstall));
    }

    [Theory]
    [InlineData(false, 7, OperationType.Install, true)]
    [InlineData(false, 4, OperationType.Update, true)]
    [InlineData(false, 0, OperationType.Install, true)]
    [InlineData(false, 3, OperationType.Install, false)]
    [InlineData(false, 3, OperationType.Update, false)]
    [InlineData(false, 3, OperationType.Uninstall, true)]
    [InlineData(false, 2, OperationType.Uninstall, true)]
    [InlineData(true, 7, OperationType.Install, false)]
    [InlineData(true, 7, OperationType.Uninstall, false)]
    public void ElevatesOnlyWhatMasRunsAsRoot(bool isRoot, int major, OperationType operation, bool expected)
    {
        MacAppStorePkgOperationHelper.IsRoot = () => isRoot;
        var (manager, _) = NewManager(major);

        Assert.Equal(expected, Operations(manager).NeedsRoot(operation));
    }

    [Fact]
    public async Task AnOperationWithoutRootRunsAsAnOrdinaryProcess()
    {
        MacAppStorePkgOperationHelper.IsRoot = () => false;
        var (manager, requests) = NewManager(3);
        List<string> log = [];

        Assert.Null(await Operations(manager).TryPerformAsync(App(manager, Keynote, "Keynote"), new InstallOptions(),
            OperationType.Install, log.Add, _ => { }, CancellationToken.None));

        // The price was looked up before the command line is built.
        Assert.Single(requests);
        Assert.NotNull(manager.Catalog.Peek(Keynote));
        Assert.Empty(log);
    }

    [Fact]
    public async Task RunsMasThroughSudoWithTheAskpassDialog()
    {
        MacAppStorePkgOperationHelper.IsRoot = () => false;
        var (manager, _) = NewManager();
        ProcessStartInfo? started = null;
        string? askpass = null;
        ElevatedMas.RunProcess = (startInfo, onLine, _) =>
        {
            started = startInfo;
            askpass = File.ReadAllText(startInfo.Environment["SUDO_ASKPASS"]!);
            onLine("==> Installed Amphetamine (5.3.2)");
            return Task.FromResult(0);
        };
        List<string> log = [];

        var verdict = await Operations(manager).TryPerformAsync(App(manager), new InstallOptions(),
            OperationType.Install, log.Add, _ => { }, CancellationToken.None);

        Assert.Equal(OperationVeredict.Success, verdict);
        Assert.NotNull(started);
        Assert.Equal(ElevatedMas.SudoPath, started.FileName);
        Assert.False(started.UseShellExecute);
        Assert.Equal(
            ["-A", "-p", "UniGetUI needs your password to install Amphetamine from the Mac App Store.", "--", MasPath, "get", Amphetamine],
            started.ArgumentList);
        Assert.Equal(ElevatedMas.AskpassScript, askpass);
        Assert.Equal(["==> Installed Amphetamine (5.3.2)"], log);

        string script = started.Environment["SUDO_ASKPASS"]!;
        Assert.StartsWith(CoreData.UniGetUIDataDirectory, script);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute,
                File.GetUnixFileMode(script));
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
    public async Task CancellingStopsTheElevatedOperation()
    {
        MacAppStorePkgOperationHelper.IsRoot = () => false;
        var (manager, _) = NewManager();
        using var cancellation = new CancellationTokenSource();
        ElevatedMas.RunProcess = (_, _, token) =>
        {
            cancellation.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Operations(manager).TryPerformAsync(App(manager),
            new InstallOptions(), OperationType.Uninstall, _ => { }, _ => { }, cancellation.Token));
    }

    [Fact]
    public void ReadsTheOutcomeOfSudo()
    {
        var (manager, _) = NewManager();
        var operations = Operations(manager);
        List<string> log = [];

        Assert.Equal(OperationVeredict.Success, operations.Verdict(0, [], log.Add));
        Assert.Equal(OperationVeredict.Canceled,
            operations.Verdict(1, ["sudo: no password was provided", "sudo: a password is required"], log.Add));
        Assert.Equal(OperationVeredict.Failure, operations.Verdict(1, ["Error: No apps found"], log.Add));
        Assert.Empty(log);

        Assert.Equal(OperationVeredict.Failure,
            operations.Verdict(1, ["alex is not in the sudoers file."], log.Add));
        Assert.Contains("administrator account", Assert.Single(log));
    }

    [Fact]
    public void ThePasswordPromptIsShownVerbatim()
    {
        Assert.Equal(["-A", "-p", "100%% sure about %%p?", "--", MasPath, "get", "1"],
            ElevatedMas.SudoArguments("100% sure about %p?", MasPath, ["get", "1"]));
    }

    // ── Manager ────────────────────────────────────────────────────────────

    [Fact]
    public void DescribesTheManager()
    {
        var manager = new MacAppStore();

        Assert.Equal("mas", manager.Properties.Id);
        Assert.Equal("Mac App Store", manager.Properties.DisplayName);
        Assert.Equal("brew install mas", manager.Properties.InstallCommands.MacOS);
        Assert.Equal("PACKAGE_MANAGERS.md#mac-app-store", manager.Properties.SetupGuide);
        Assert.False(manager.Capabilities.SupportsCustomSources);
        Assert.False(manager.Capabilities.CanListVersions);
        Assert.True(manager.Capabilities.SerializesOperations);
        if (!OperatingSystem.IsMacOS())
            Assert.Empty(manager.FindCandidateExecutableFiles());
    }

    // ── Listings, against a stand-in for mas ───────────────────────────────

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

    /// <summary>
    /// A shell script standing in for mas: for "mas &lt;verb&gt; ...", it prints &lt;verb&gt;.out, writes &lt;verb&gt;.err to
    /// standard error, exits with &lt;verb&gt;.code and records its arguments in "calls".
    /// </summary>
    private string FakeMas(Dictionary<string, string> outputs, Dictionary<string, int>? codes = null)
    {
        string dir = Path.Combine(_testRoot, "fake-mas");
        Directory.CreateDirectory(dir);
        foreach (var (name, text) in outputs)
            File.WriteAllText(Path.Combine(dir, name), text);
        foreach (var (verb, code) in codes ?? [])
            File.WriteAllText(Path.Combine(dir, verb + ".code"), code.ToString(System.Globalization.CultureInfo.InvariantCulture));

        string script = Path.Combine(dir, "mas");
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

    private string[] Calls() => File.ReadAllLines(Path.Combine(_testRoot, "fake-mas", "calls"));

    private static ListingMacAppStore Listing(string masPath)
    {
        var manager = new ListingMacAppStore
        {
            Status = new ManagerStatus { Found = true, ExecutablePath = masPath, ExecutableCallArgs = "", Version = "" },
        };
        manager.Catalog.Download = _ => throw new InvalidOperationException("No lookup may run in this test.");
        return manager;
    }

    [Fact]
    public void ListsThroughMas()
    {
        if (OperatingSystem.IsWindows())
            return;
        string installed = Path.Combine(_testRoot, "Amphetamine.app");
        Directory.CreateDirectory(installed);
        string list = Fixture("list.jsonl").Replace("\"/Applications/Amphetamine.app\"", $"\"{installed}\"");
        var manager = Listing(FakeMas(new()
        {
            ["version.out"] = "7.0.0\n",
            ["config.out"] = Fixture("config.json"),
            ["list.out"] = list,
            ["outdated.out"] = Fixture("outdated.jsonl"),
            ["search.out"] = Fixture("search.jsonl"),
        }));

        Assert.Equal("7.0.0", manager.LoadVersion());
        Assert.Equal(7, manager.MasMajorVersion);
        Assert.Equal("CA", manager.Storefront);

        var apps = manager.Installed();
        Assert.Equal([Amphetamine, Keynote, "6450432292"], apps.Select(app => app.Id));
        Assert.All(apps, app => Assert.Same(manager.DefaultSource, app.Source));
        Assert.Equal(installed, manager.InstallPathOf(apps[0]));

        var updates = manager.Updates();
        Assert.Equal(["1.1.0", "11.4.4"], updates.Select(app => app.NewVersionString));

        var found = manager.Search("final cut");
        Assert.Equal([Amphetamine, FinalCut], found.Select(app => app.Id));
        Assert.NotNull(manager.Catalog.Peek(FinalCut));

        Assert.Equal(["version", "config --json", "list --json", "outdated --json", "search --json final cut"], Calls());
    }

    [Fact]
    public void OlderMasIsReadAsATable()
    {
        if (OperatingSystem.IsWindows())
            return;
        var manager = Listing(FakeMas(new()
        {
            ["version.out"] = "2.1.0\n",
            ["list.out"] = Fixture("list.txt"),
            ["outdated.out"] = Fixture("outdated.txt"),
        }));

        manager.LoadVersion();
        Assert.Equal(4, manager.Installed().Count);
        Assert.Equal(2, manager.Updates().Count);

        Assert.Equal(["version", "list", "outdated"], Calls());
    }

    [Fact]
    public void ASearchWithNoResultsIsNotAFailure()
    {
        if (OperatingSystem.IsWindows())
            return;
        var manager = Listing(FakeMas(new()
        {
            ["version.out"] = "7.0.0\n",
            ["search.err"] = "Error: No apps found\n",
        }, new() { ["search"] = 1 }));
        manager.LoadVersion();

        Assert.Empty(manager.Search("zzqqxx"));
        Assert.Empty(manager.Search("--"));
        Assert.Equal(["version", "search --json zzqqxx"], Calls());
    }

    [Fact]
    public void AFailedListingIsReported()
    {
        if (OperatingSystem.IsWindows())
            return;
        var manager = Listing(FakeMas(new()
        {
            ["version.out"] = "7.0.0\n",
            ["list.err"] = "Error: Unable to read the Spotlight index\n",
            ["outdated.err"] = "Error: Unable to look up 1 app\n",
        }, new() { ["list"] = 1, ["outdated"] = 1 }));
        manager.LoadVersion();

        var error = Assert.Throws<InvalidOperationException>(manager.Installed);
        Assert.Contains("Spotlight", error.Message);
        Assert.Empty(manager.Updates());
    }
}
