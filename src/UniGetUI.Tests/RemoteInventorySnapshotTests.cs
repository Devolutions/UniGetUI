using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Devolutions.Now.Policy.Api;
using UniGetUI.Interface;
using UniGetUI.Interface.AgentRegistration;
using UniGetUI.PackageEngine.AgentBroker;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.Classes;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Structs;

namespace UniGetUI.Tests;

public sealed class RemoteInventorySnapshotTests
{
    private static readonly DateTime Installed = new(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Updated = Installed.AddMinutes(5);

    internal sealed class SnapshotManager(string id, bool enabled = true, bool found = true, bool ready = true)
        : IPackageManager
    {
        public ManagerProperties Properties { get; } = new(IsDummy: true);
        public ManagerCapabilities Capabilities { get; } = new(IsDummy: true);
        public ManagerStatus Status { get; } = new() { Found = found };
        public string Id => id;
        public string Name => id;
        public string DisplayName => id;
        public IManagerSource DefaultSource => new ManagerSource(this, "default", new Uri("https://example.test/feed"));
        public IManagerLogger TaskLogger => throw new NotSupportedException();
        public IMultiSourceHelper SourcesHelper => throw new NotSupportedException();
        public IPackageDetailsHelper DetailsHelper => throw new NotSupportedException();
        public IPackageOperationHelper OperationHelper => throw new NotSupportedException();
        public IReadOnlyList<ManagerDependency> Dependencies => [];
        public Encoding OutputEncoding => Encoding.UTF8;
        public bool InstallerUrlFollowsPackageVersion => false;
        public bool CommandLineIsShellInterpreted => false;
        public bool IdentifiersAreQuotedOnCommandLine => true;
        public bool LastUpdatesListingFailed => false;
        public bool LastInstalledListingFailed { get; set; }
        public IReadOnlyList<IPackage> ListedPackages { get; set; } = [];
        public ManualResetEventSlim? ListingGate { get; set; }
        public long InstalledPackagesGeneration => 0;
        public void InvalidateInstalledPackages() { }
        public int? CompareVersions(string versionA, string versionB) => throw new NotSupportedException();
        public bool CanUpdateToExactVersion => false;
        public IPackage? ResolveSkippedMajorUpdate(IPackage update, UniGetUI.PackageEngine.Serializable.InstallOptions? options = null) => update;
        public void Initialize() => throw new NotSupportedException();
        public bool IsEnabled() => enabled;
        public bool IsReady() => ready && enabled && found;
        public IReadOnlyList<IPackage> FindPackages(string query) => throw new NotSupportedException();
        public IReadOnlyList<IPackage> FindPackages(string query, out bool failed) => throw new NotSupportedException();
        public ManagerHealthRecord GetHealthRecord() => throw new NotSupportedException();
        public bool ExpectsInstalledPackages => true;
        public bool IsInitializing => false;
        public IReadOnlyList<ManagerMaintenanceAction> GetMaintenanceActions() => [];
        public IReadOnlyList<IPackage> GetAvailableUpdates() => throw new NotSupportedException();
        public IReadOnlyList<IPackage> GetInstalledPackages()
        {
            ListingGate?.Wait(TimeSpan.FromSeconds(30));
            return ListedPackages;
        }
        public void RefreshPackageIndexes() => throw new NotSupportedException();
        public void AttemptFastRepair() => throw new NotSupportedException();
        public IReadOnlyList<string> FindCandidateExecutableFiles() => [];
        public Tuple<bool, string> GetExecutableFile() => Tuple.Create(false, "");
    }

    private static Package Row(IPackageManager manager, string id, string? scope, string version = "1.0", string? newVersion = null) =>
        new(id, id, version, newVersion ?? version, manager.DefaultSource, manager,
            scope is null ? null : new OverridenInstallationOptions(scope));

    private static CompletedPackageScan Scan(DateTime at, IEnumerable<IPackage> packages, params IPackageManager[] succeeded) =>
        new(packages.ToArray(), at, succeeded.ToHashSet());

    private sealed class TestInstalledLoader(IReadOnlyList<IPackageManager> managers) : InstalledPackagesLoader(managers)
    {
        protected override bool WillQueryAnyManager() => false;
        protected override Task WhenAddingPackage(IPackage package) => Task.CompletedTask;
    }

    [Fact]
    public async Task CurrentListCannotIdentifyARowAfterAnIncompleteScan()
    {
        var old = InstalledPackagesLoader.Instance;
        var manager = new SnapshotManager("a");
        manager.ListedPackages = [Row(manager, "p", "user")];
        var loader = new TestInstalledLoader([manager]);
        var backend = new IpcRemoteInventoryBackend();
        try
        {
            await loader.ReloadPackages();
            Assert.True(backend.IsOnlyInstalledRow("a", "p", "user"));

            manager.LastInstalledListingFailed = true;
            await loader.ReloadPackages();
            Assert.Single(loader.Packages);
            Assert.False(backend.IsOnlyInstalledRow("a", "p", "user"));
            Assert.Null(IpcRemoteInventory.BuildSnapshot([manager], loader.LastCompletedScan, null, []));
        }
        finally
        {
            InstalledPackagesLoader.Instance = old;
        }
    }

    [Fact]
    public async Task OperationArrivingDuringARescanIsCheckedAgainstTheLastCompletedScan()
    {
        var old = InstalledPackagesLoader.Instance;
        var manager = new SnapshotManager("a");
        var observed = new ManagerSource(manager, "observed-feed", new Uri("https://feed.example.test/v2"));
        manager.ListedPackages =
        [
            new Package("p", "p", "1.0", "1.0", observed, manager, new OverridenInstallationOptions("user")),
        ];
        var loader = new TestInstalledLoader([manager]);
        var backend = new IpcRemoteInventoryBackend();
        using var gate = new ManualResetEventSlim(true);
        manager.ListingGate = gate;
        Task? rescan = null;
        try
        {
            await loader.ReloadPackages();
            gate.Reset();
            rescan = loader.ReloadPackagesListingOnly();
            Assert.True(loader.IsLoading);

            Assert.True(backend.IsOnlyInstalledRow("a", "p", "user"));
            Assert.Contains(observed, IpcRemoteInventory.ScannedSources());
        }
        finally
        {
            gate.Set();
            if (rescan is not null)
                await rescan.WaitAsync(TimeSpan.FromSeconds(30));
            InstalledPackagesLoader.Instance = old;
        }
    }

    [Fact]
    public void UpdateListedForAnotherVersionIsNotShownOnTheRow()
    {
        var a = new SnapshotManager("a");
        var snapshot = IpcRemoteInventory.BuildSnapshot(
            [a],
            Scan(Installed, [Row(a, "p", "user", "2.0")], a),
            Scan(Updated, [Row(a, "p", "user", "1.0", "2.0"), Row(a, "q", null, "1.0", "2.0")], a),
            []);

        var row = Assert.Single(snapshot!.InstalledPackages);
        Assert.Equal("", row.NewVersion);
        Assert.False(row.IsUpgradable);
    }

    [Fact]
    public void InventoryIsUnavailableUntilEveryExpectedManagerHasACompletedScan()
    {
        var a = new SnapshotManager("a");
        var b = new SnapshotManager("b");
        IPackageManager[] managers = [a, b];

        Assert.Null(IpcRemoteInventory.BuildSnapshot(managers, null, null, []));
        Assert.Null(IpcRemoteInventory.BuildSnapshot(managers, Scan(Installed, [Row(a, "p", null)], a), null, []));
        Assert.Null(IpcRemoteInventory.BuildSnapshot(
            [a, new SnapshotManager("c", ready: false)], Scan(Installed, [], a), null, []));
    }

    [Fact]
    public void DisabledAndMissingManagersAreNotExpectedAndTheirRowsAreDropped()
    {
        var a = new SnapshotManager("a");
        var disabled = new SnapshotManager("disabled", enabled: false);
        var missing = new SnapshotManager("missing", found: false);

        var snapshot = IpcRemoteInventory.BuildSnapshot(
            [a, disabled, missing],
            Scan(Installed, [Row(a, "p", null), Row(disabled, "q", null), Row(missing, "r", null)], a),
            null,
            []);

        Assert.NotNull(snapshot);
        Assert.Equal(Installed, snapshot.CompletedUtc);
        Assert.Equal("p", Assert.Single(snapshot.InstalledPackages).Id);
    }

    [Fact]
    public void DisabledManagersKeepObservedSourcesWithoutExportingTheirPackages()
    {
        var enabled = new SnapshotManager("enabled");
        var disabled = new SnapshotManager("disabled", enabled: false);
        var installedSource = new ManagerSource(disabled, "observed-installed", new Uri("https://example.test/installed"));
        var updateSource = new ManagerSource(disabled, "observed-updates", new Uri("https://example.test/updates"));
        var installed = new Package("q", "q", "1.0", "1.0", installedSource, disabled);
        var update = new Package("q", "q", "1.0", "2.0", updateSource, disabled);

        var snapshot = IpcRemoteInventory.BuildSnapshot(
            [enabled, disabled],
            Scan(Installed, [Row(enabled, "p", null), installed], enabled),
            Scan(Updated, [update]),
            []);

        Assert.NotNull(snapshot);
        Assert.Equal("p", Assert.Single(snapshot.InstalledPackages).Id);
        Assert.Empty(snapshot.Updates);
        Assert.Null(snapshot.UpdateCompletedUtc);
        Assert.Contains(snapshot.Sources, source => source.Manager == "disabled" && source.Name == "observed-installed");
        Assert.Contains(snapshot.Sources, source => source.Manager == "disabled" && source.Name == "observed-updates");
        Assert.All(snapshot.Sources, source => Assert.Equal("", source.Url));
    }

    [Fact]
    public void ScopeDistinctInstallationsStaySeparateAndTakeTheirOwnUpdate()
    {
        var a = new SnapshotManager("a");
        var snapshot = IpcRemoteInventory.BuildSnapshot(
            [a],
            Scan(Installed, [Row(a, "p", "user"), Row(a, "p", "machine")], a),
            Scan(Updated, [Row(a, "p", "machine", "1.0", "2.0")], a),
            []);

        Assert.NotNull(snapshot);
        Assert.Equal(Updated, snapshot.UpdateCompletedUtc);
        Assert.Equal(2, snapshot.InstalledPackages.Count);
        var user = snapshot.InstalledPackages.Single(row => row.ReportedScope == "user");
        var machine = snapshot.InstalledPackages.Single(row => row.ReportedScope == "machine");
        Assert.Equal("", user.NewVersion);
        Assert.False(user.IsUpgradable);
        Assert.Equal("2.0", machine.NewVersion);
        Assert.True(machine.IsUpgradable);
    }

    [Fact]
    public void UnscopedUpdateIsAttributedOnlyToALoneInstallation()
    {
        var a = new SnapshotManager("a");
        var lone = IpcRemoteInventory.BuildSnapshot(
            [a], Scan(Installed, [Row(a, "p", "user")], a), Scan(Updated, [Row(a, "p", null, "1.0", "2.0")], a), []);
        var two = IpcRemoteInventory.BuildSnapshot(
            [a],
            Scan(Installed, [Row(a, "p", "user"), Row(a, "p", "machine")], a),
            Scan(Updated, [Row(a, "p", null, "1.0", "2.0")], a),
            []);
        var unscopedAndScoped = IpcRemoteInventory.BuildSnapshot(
            [a],
            Scan(Installed, [Row(a, "p", null), Row(a, "p", "user")], a),
            Scan(Updated, [Row(a, "p", null, "1.0", "2.0")], a),
            []);

        Assert.Equal("2.0", Assert.Single(lone!.InstalledPackages).NewVersion);
        Assert.All(two!.InstalledPackages, row => Assert.Equal("", row.NewVersion));
        Assert.All(unscopedAndScoped!.InstalledPackages, row => Assert.Equal("", row.NewVersion));
    }

    [Fact]
    public void IncompleteUpdateScanIsLeftOutEntirely()
    {
        var a = new SnapshotManager("a");
        var b = new SnapshotManager("b");
        var snapshot = IpcRemoteInventory.BuildSnapshot(
            [a, b],
            Scan(Installed, [Row(a, "p", null), Row(b, "q", null)], a, b),
            Scan(Updated, [Row(a, "p", null, "1.0", "2.0")], a),
            []);

        Assert.NotNull(snapshot);
        Assert.Null(snapshot.UpdateCompletedUtc);
        Assert.Empty(snapshot.Updates);
        Assert.All(snapshot.InstalledPackages, row => Assert.Equal("", row.NewVersion));
    }

    private sealed class SnapshotBackend(RemoteInventorySnapshot? snapshot) : IAgentRegistrationBackend
    {
        public RemoteInventorySnapshot? TryGetInventorySnapshot() => snapshot;
        public string? FindManagerId(ManagerName manager) => throw new NotSupportedException();
        public RemoteSourceResolution ResolveSource(string managerId, string sourceName) =>
            throw new NotSupportedException();
        public bool IsOnlyInstalledRow(string managerId, string packageId, string reportedScope) =>
            throw new NotSupportedException();
        public bool IsManagerEnabled(string managerId) => throw new NotSupportedException();
        public Task<RemoteGrantDelivery> SubmitGrantAsync(
            RemoteGrantPlan plan, string grant, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> RescanInventoryAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public bool RefreshInstalledInventory() => throw new NotSupportedException();
    }

    /// <summary>
    /// The GetInventorySnapshot responses for a representative machine, kept in
    /// Fixtures/AgentInterop so the Agent's interop tests can cite them. Set
    /// UNIGETUI_REGENERATE_FIXTURES=1 to rewrite them after an intended change.
    /// </summary>
    [Theory]
    [InlineData("inventory-snapshot-response.json", true, true)]
    [InlineData("inventory-snapshot-response-no-update-scan.json", true, false)]
    [InlineData("inventory-snapshot-response-unavailable.json", false, false)]
    public async Task InventorySnapshotResponsesMatchTheInteropFixtures(string fixture, bool installedScanComplete, bool updateScanComplete)
    {
        var winget = new SnapshotManager("winget");
        var winps = new SnapshotManager("winps");
        var pwsh = new SnapshotManager("pwsh");
        var dotnet = new SnapshotManager("dotnet-tool");
        var pip = new SnapshotManager("pip");
        var cargo = new SnapshotManager("cargo", enabled: false, ready: false);
        IPackageManager[] managers = [winget, winps, pwsh, dotnet, pip, cargo];
        IPackageManager[] scanned = [winget, winps, pwsh, dotnet, pip];

        var completed = new DateTime(2026, 11, 15, 13, 20, 5, DateTimeKind.Utc).AddTicks(1234567);
        var installed = Scan(
            completed,
            [
                Row(winget, "7zip.7zip", null, "24.08"),
                Row(dotnet, "dotnet-ef", null, "9.0.0"),
                Row(winget, "Git.Git", "user", "2.47.0"),
                Row(winget, "Git.Git", "machine", "2.46.0"),
                Row(pwsh, "Pester", "user", "5.6.1"),
                Row(winps, "PSReadLine", null, "2.0.0"),
                Row(pip, "requests", "machine", "2.32.3"),
            ],
            installedScanComplete ? scanned : [winget]);
        var updates = Scan(
            completed.AddMinutes(2),
            [Row(dotnet, "dotnet-ef", null, "9.0.0", "10.0.0"), Row(winget, "Git.Git", "machine", "2.46.0", "2.47.1")],
            updateScanComplete ? scanned : [winget]);
        IpcRemoteManagerInfo[] info = managers
            .Select(manager => new IpcRemoteManagerInfo
            {
                Name = manager.Name,
                DisplayName = manager.DisplayName,
                Enabled = manager.IsEnabled(),
                Ready = manager.IsReady(),
            })
            .ToArray();

        var snapshot = IpcRemoteInventory.BuildSnapshot(managers, installed, updates, info);
        byte[] response = await new AgentRequestDispatcher(new SnapshotBackend(snapshot))
            .HandleAsync("11111111-2222-3333-4444-555555555555", "GetInventorySnapshot", default);

        AssertMatchesFixture(fixture, JsonNode.Parse(response)!);
    }

    private static void AssertMatchesFixture(string name, JsonNode actual, [CallerFilePath] string here = "")
    {
        string path = Path.Combine(Path.GetDirectoryName(here)!, "Fixtures", "AgentInterop", name);
        if (Environment.GetEnvironmentVariable("UNIGETUI_REGENERATE_FIXTURES") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            File.WriteAllText(path, actual.ToJsonString(options).ReplaceLineEndings("\n") + "\n");
        }

        Assert.True(
            JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(path)), actual),
            $"{name} no longer matches the response UniGetUI sends.");
    }

    [Fact]
    public void OnlyInstalledRowRequiresOneInstallationWithTheReportedScope()
    {
        var a = new SnapshotManager("a");
        string managerId = IpcManagerSettingsApi.GetPublicManagerId(a);
        IPackage[] one = [Row(a, "p", null), Row(a, "other", "user")];
        IPackage[] two = [Row(a, "p", "user"), Row(a, "p", "machine")];

        Assert.True(IpcRemoteInventory.IsOnlyInstalledRow(one, managerId, "p", ""));
        Assert.False(IpcRemoteInventory.IsOnlyInstalledRow(one, managerId, "p", "user"));
        Assert.False(IpcRemoteInventory.IsOnlyInstalledRow(one, managerId, "P", ""));
        Assert.False(IpcRemoteInventory.IsOnlyInstalledRow(one, "b", "p", ""));
        Assert.False(IpcRemoteInventory.IsOnlyInstalledRow(two, managerId, "p", "user"));
        Assert.False(IpcRemoteInventory.IsOnlyInstalledRow([], managerId, "p", ""));
    }
}
