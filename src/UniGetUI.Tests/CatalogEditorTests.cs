using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using UniGetUI.Avalonia.Models;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.Views.Pages;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.Classes;
using UniGetUI.PackageEngine.Classes.Manager.ManagerHelpers;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.Tests;

public class CatalogEditorTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"catalog-editor-tests-{Guid.NewGuid():N}");
    private string FilePath => Path.Combine(_directory, "catalog.json");

    public CatalogEditorTests() => Directory.CreateDirectory(_directory);

    private static CatalogEntry Entry(string source = "winget") => new()
    {
        Name = "Claude",
        Id = "Anthropic.Claude",
        ManagerName = "WinGet",
        Source = source,
    };

    private static CatalogDefinition[] Definitions =>
    [
        new() { Id = "devolutions", Name = "Devolutions", Packages = [Entry()] },
        new() { Id = "tools", Name = "Tools", Packages = [] },
    ];

    private async Task<CatalogEditorViewModel> LoadEditorAsync()
    {
        await SoftwareCatalog.SaveAsync(FilePath, Definitions);
        var vm = new CatalogEditorViewModel();
        Assert.True(await vm.LoadAsync(FilePath));
        return vm;
    }

    [Fact]
    public async Task DiscoverAddsSelectionToTheFirstCatalogAndPreservesTheOtherCatalogs()
    {
        var vm = await LoadEditorAsync();
        var manager = new SearchManager();
        var source = new ManagerSource(manager, "private", new Uri("https://example.test/feed"));
        var package = new Package("Tool", "Company.Tool", "2.0", source, manager);
        Assert.Same(vm.Catalogs[0], vm.SelectedCatalog);
        Assert.Equal(1, vm.AddPackages([package, package]));
        var entry = vm.SelectedCatalog!.Packages[1];
        Assert.Equal(package.Id, entry.Id);
        Assert.Equal(package.Name, entry.Name);
        Assert.Equal("private", entry.Source);
        Assert.Equal(manager.DisplayName, entry.ManagerName);
        Assert.Equal("", entry.Version);
        Assert.True(vm.IsDirty);
        Assert.Empty(vm.Catalogs[1].Packages);
        Assert.Single((await SoftwareCatalog.LoadAsync(FilePath))[0].Packages);
        Assert.True(await vm.SaveAsync(FilePath));
        Assert.Equal(2, (await SoftwareCatalog.LoadAsync(FilePath))[0].Packages.Length);
        Assert.Equal(2, vm.Catalogs.Count);
    }

    [Theory]
    [InlineData("catalog-test")]
    [InlineData("CatalogTest")]
    [InlineData("Catalog test manager")]
    public async Task DiscoverDeduplicatesManagerAliasesAndKeepsExistingVersions(string managerName)
    {
        var vm = await LoadEditorAsync();
        var manager = new SearchManager();
        var package = new Package("Tool", "Company.Tool", "2", manager.DefaultSource, manager);
        vm.SelectedCatalog = vm.Catalogs[1];
        var existing = new CatalogEntry
        {
            Id = package.Id,
            Name = "Pinned Tool",
            Version = "1",
            Source = package.Source.Name,
            ManagerName = managerName,
        };
        vm.SelectedCatalog.Packages.Add(existing);
        Assert.Equal(0, vm.AddPackages([package]));
        Assert.Same(existing, Assert.Single(vm.SelectedCatalog.Packages));
        Assert.Equal("1", existing.Version);
        var other = new Package("Tool", package.Id, "2",
            new ManagerSource(manager, "other", new Uri("https://example.test/other")), manager);
        Assert.Equal(1, vm.AddPackages([other]));
        Assert.Equal(2, vm.SelectedCatalog.Packages.Count);
    }

    [Fact]
    public void AddingPackagesRequiresALoadedDocumentAndACatalog()
    {
        var vm = new CatalogEditorViewModel();
        Assert.Throws<InvalidOperationException>(() => vm.AddPackages([]));
        vm.NewDocument();
        Assert.True(vm.IsLoaded);
        Assert.True(vm.IsDirty);
        Assert.Single(vm.Catalogs);
        Assert.Same(vm.Catalogs[0], vm.SelectedCatalog);
        Assert.Equal("", vm.FilePath);
        vm.SelectedCatalog = null;
        Assert.Throws<InvalidOperationException>(() => vm.AddPackages([]));
    }

    [Fact]
    public async Task SharedDocumentDoesNotReloadAndDiscardUnsavedChanges()
    {
        var vm = await LoadEditorAsync();
        vm.SelectedCatalog = vm.Catalogs[1];
        vm.SelectedCatalog.Name = "Unsaved name";
        Assert.True(await vm.EnsureLoadedAsync());
        Assert.Same(vm.Catalogs[1], vm.SelectedCatalog);
        Assert.Equal("Unsaved name", vm.SelectedCatalog.Name);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void CatalogPackageListPreservesUnavailableIdentitiesAndDoesNotTouchBundles()
    {
        var loader = new CatalogPackagesLoader();
        var first = new CatalogEntry { Id = "tool", Name = "Tool", ManagerName = "missing-one", Source = "one" };
        var second = new CatalogEntry { Id = "tool", Name = "Tool", ManagerName = "missing-two", Source = "two" };
        var bundles = UniGetUI.PackageEngine.PackageLoader.PackageBundlesLoader.Instance;
        loader.Replace([first, second]);
        Assert.Equal(2, loader.Packages.Count);
        Assert.Equal(2, loader.Packages.Select(p => p.GetVersionedHash()).Distinct().Count());
        Assert.All(loader.Packages, p => Assert.Contains(loader.FindEntry(p), new[] { first, second }));
        loader.Replace([second]);
        Assert.Same(second, loader.FindEntry(Assert.Single(loader.Packages)));
        Assert.Same(bundles, UniGetUI.PackageEngine.PackageLoader.PackageBundlesLoader.Instance);
    }

    [Fact]
    public void CatalogEntryLookupUsesTheLoaderIdentityRatherThanPackageObjectEquality()
    {
        var loader = new CatalogPackagesLoader();
        var entry = new CatalogEntry { Id = "tool", Name = "Tool", ManagerName = "missing", Source = "private" };
        loader.Replace([entry]);
        var original = Assert.Single(loader.Packages);
        var equivalent = new InvalidImportedPackage(entry.AsSerializable().GetInvalidEquivalent(), original.Source,
            $"{entry.ManagerName}\0{entry.Source}\0{entry.Id}");
        Assert.NotSame(original, equivalent);
        Assert.Equal(original.GetVersionedHash(), equivalent.GetVersionedHash());
        Assert.Same(entry, loader.FindEntry(equivalent));
    }

    [Fact]
    public async Task DiscardRestoresEveryCatalogAndItsPackagesWithoutWritingToDisk()
    {
        var vm = await LoadEditorAsync();
        string original = await File.ReadAllTextAsync(FilePath);
        Assert.False(vm.CanDiscardChanges);
        vm.SelectedCatalog!.Name = "Changed";
        vm.SelectedCatalog.Packages[0].Version = "Changed";
        vm.SelectedCatalog.Packages.Clear();
        vm.NewCatalog();
        Assert.True(vm.CanDiscardChanges);
        vm.DiscardChanges();
        Assert.False(vm.CanDiscardChanges);
        Assert.False(vm.IsDirty);
        Assert.Equal(["devolutions", "tools"], vm.Catalogs.Select(c => c.Id));
        Assert.Equal("Devolutions", vm.SelectedCatalog!.Name);
        Assert.Equal("", Assert.Single(vm.SelectedCatalog.Packages).Version);
        Assert.Equal(original, await File.ReadAllTextAsync(FilePath));
        vm.SelectedCatalog.Name = "Changed again";
        vm.DiscardChanges();
        Assert.Equal("Devolutions", vm.SelectedCatalog!.Name);
    }

    [Fact]
    public async Task DiscardUsesTheLastSuccessfulSaveAndRetainsTheSelectedCatalog()
    {
        var vm = await LoadEditorAsync();
        vm.SelectedCatalog = vm.Catalogs[1];
        vm.SelectedCatalog.Name = "Saved";
        string export = Path.Combine(_directory, "export.json");
        Assert.True(await vm.SaveAsync(export));
        vm.SelectedCatalog.Name = "Changed";
        vm.SelectedCatalog.Packages.Add(Entry());
        vm.DiscardChanges();
        Assert.Equal(export, vm.FilePath);
        Assert.Equal("tools", vm.SelectedCatalog!.Id);
        Assert.Equal("Saved", vm.SelectedCatalog.Name);
        Assert.Empty(vm.SelectedCatalog.Packages);
        Assert.False(vm.IsDirty);
        Assert.Equal("Tools", (await SoftwareCatalog.LoadAsync(FilePath))[1].Name);
    }

    [Fact]
    public async Task FailedSaveDoesNotReplaceTheDiscardBaseline()
    {
        var vm = await LoadEditorAsync();
        vm.SelectedCatalog!.Name = "Unsaved";
        Assert.False(await vm.SaveAsync(Path.Combine(_directory, "missing", "catalog.json")));
        Assert.True(vm.CanDiscardChanges);
        vm.DiscardChanges();
        Assert.Equal("Devolutions", vm.SelectedCatalog!.Name);
        Assert.Equal(FilePath, vm.FilePath);
        Assert.False(vm.IsDirty);
        Assert.Empty(vm.Message);
    }

    [Fact]
    public async Task DiscardingANewDocumentDoesNotRestoreThePreviousFile()
    {
        var vm = await LoadEditorAsync();
        vm.NewDocument();
        vm.DiscardChanges();
        Assert.Empty(vm.Catalogs);
        Assert.Null(vm.SelectedCatalog);
        Assert.Equal("", vm.FilePath);
        Assert.True(vm.IsLoaded);
        Assert.False(vm.IsDirty);
        vm.NewCatalog();
        Assert.True(vm.CanDiscardChanges);
    }

    [Fact]
    public async Task EditorLoadsAllCatalogsWithoutMarkingThemDirty()
    {
        var vm = await LoadEditorAsync();
        Assert.Equal(2, vm.Catalogs.Count);
        Assert.Equal("devolutions", vm.SelectedCatalog!.Id);
        Assert.Equal(FilePath, vm.FilePath);
        Assert.True(vm.CanEdit);
        Assert.False(vm.IsDirty);
        Assert.Single(vm.SelectedCatalog.Packages);
    }

    [Fact]
    public async Task AddingAndRemovingPreservesThePackageManagerAndSource()
    {
        var vm = await LoadEditorAsync();
        vm.SelectedCatalog = vm.Catalogs[1];
        vm.SelectedResult = Entry("private");
        Assert.True(vm.CanAddPackage);
        vm.AddPackageCommand.Execute(null);
        Assert.True(vm.IsDirty);
        Assert.Equal("private", Assert.Single(vm.SelectedCatalog.Packages).Source);
        Assert.Single(vm.Catalogs[0].Packages);
        vm.SelectedPackage = vm.SelectedCatalog.Packages[0];
        Assert.True(vm.CanRemovePackage);
        vm.RemovePackageCommand.Execute(null);
        Assert.Empty(vm.SelectedCatalog.Packages);
        Assert.False(vm.CanRemovePackage);
    }

    [Fact]
    public async Task DuplicateIdentityIsRejectedButOtherSourcesAndCatalogsAreAllowed()
    {
        var vm = await LoadEditorAsync();
        vm.SelectedResult = new CatalogEntry { Name = "Renamed", Id = "anthropic.claude", ManagerName = "WINGET", Source = "WINGET" };
        vm.AddPackageCommand.Execute(null);
        Assert.Single(vm.SelectedCatalog!.Packages);
        Assert.False(vm.IsDirty);
        Assert.NotEmpty(vm.Message);
        vm.SelectedResult = Entry("private");
        vm.AddPackageCommand.Execute(null);
        Assert.Equal(2, vm.SelectedCatalog.Packages.Count);
        vm.SelectedCatalog = vm.Catalogs[1];
        vm.SelectedResult = Entry();
        vm.AddPackageCommand.Execute(null);
        Assert.Single(vm.SelectedCatalog.Packages);
    }

    [Fact]
    public async Task SaveAsRoundTripsAllCatalogsAndDoesNotChangeTheOriginalFile()
    {
        var vm = await LoadEditorAsync();
        vm.SelectedCatalog!.Name = "Updated";
        vm.SelectedPackage = vm.SelectedCatalog.Packages[0];
        vm.RemovePackageCommand.Execute(null);
        string output = Path.Combine(_directory, "export.json");
        Assert.True(await vm.SaveAsync(output));
        Assert.False(vm.IsDirty);
        Assert.Equal(output, vm.FilePath);
        var saved = await SoftwareCatalog.LoadAsync(output);
        Assert.Equal(2, saved.Length);
        Assert.Equal("Updated", saved[0].Name);
        Assert.Empty(saved[0].Packages);
        Assert.Single((await SoftwareCatalog.LoadAsync(FilePath))[0].Packages);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task SaveOverwritesAnExistingFileWithValidatedMetadata()
    {
        var vm = await LoadEditorAsync();
        vm.SelectedCatalog!.Name = "Updated";
        Assert.True(vm.IsDirty);
        Assert.True(await vm.SaveAsync(FilePath));
        Assert.Equal("Updated", (await SoftwareCatalog.LoadAsync(FilePath))[0].Name);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task SavedSchemaIsVersionedAndPackagesMatchSoftwareBundles()
    {
        var definitions = Definitions;
        definitions[0] = new CatalogDefinition
        {
            Id = "devolutions",
            Name = "Devolutions",
            Packages =
            [
                new CatalogEntry
                {
                    Id = "Microsoft.VisualStudioCode", Name = "Visual Studio Code",
                    Version = "1.90.0", Source = "winget", ManagerName = "WinGet",
                },
            ],
        };
        await SoftwareCatalog.SaveAsync(FilePath, definitions);
        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(FilePath));
        Assert.Equal(["version", "catalogs"], json.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(1, json.RootElement.GetProperty("version").GetInt32());
        var catalog = json.RootElement.GetProperty("catalogs")[0];
        Assert.Equal(["id", "name", "packages"], catalog.EnumerateObject().Select(p => p.Name));
        var package = catalog.GetProperty("packages")[0];
        Assert.Equal(["Id", "Name", "Version", "Source", "ManagerName"], package.EnumerateObject().Select(p => p.Name));
        var bundled = new UniGetUI.PackageEngine.Classes.Serializable.SerializablePackage(JsonNode.Parse(package.GetRawText())!);
        Assert.Equal("Microsoft.VisualStudioCode", bundled.Id);
        Assert.Equal("Visual Studio Code", bundled.Name);
        Assert.Equal("1.90.0", bundled.Version);
        Assert.Equal("winget", bundled.Source);
        Assert.Equal("WinGet", bundled.ManagerName);
        var vm = new CatalogEditorViewModel();
        Assert.True(await vm.LoadAsync(FilePath));
        vm.SelectedCatalog!.Name = "Renamed";
        Assert.True(await vm.SaveAsync(FilePath));
        Assert.Equal("1.90.0", (await SoftwareCatalog.LoadAsync(FilePath))[0].Packages[0].Version);
    }

    [Fact]
    public async Task InvalidEditsDoNotDamageTheExistingFileOrClearDirtyState()
    {
        var vm = await LoadEditorAsync();
        string original = await File.ReadAllTextAsync(FilePath);
        vm.SelectedCatalog!.Id = "TOOLS";
        Assert.False(await vm.SaveAsync(FilePath));
        Assert.True(vm.IsDirty);
        Assert.NotEmpty(vm.Message);
        Assert.Equal(original, await File.ReadAllTextAsync(FilePath));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task UnwritableSaveRetainsEditsAndReportsFailure()
    {
        var vm = await LoadEditorAsync();
        vm.SelectedCatalog!.Name = "Unsaved";
        Assert.False(await vm.SaveAsync(Path.Combine(_directory, "missing-folder", "catalog.json")));
        Assert.True(vm.IsDirty);
        Assert.Equal(FilePath, vm.FilePath);
        Assert.NotEmpty(vm.Message);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task FailedOpenPreservesTheCurrentDocumentAndEdits()
    {
        var vm = await LoadEditorAsync();
        vm.SelectedCatalog!.Name = "Unsaved";
        string invalid = Path.Combine(_directory, "invalid.json");
        await File.WriteAllTextAsync(invalid, "[{}]");
        Assert.False(await vm.LoadAsync(invalid));
        Assert.Equal(FilePath, vm.FilePath);
        Assert.Equal("Unsaved", vm.SelectedCatalog.Name);
        Assert.True(vm.IsDirty);
        Assert.Equal(2, vm.Catalogs.Count);
    }

    [Fact]
    public async Task EmptyFileCanReceiveNewCatalogsWithUniqueIdentifiers()
    {
        await SoftwareCatalog.SaveAsync(FilePath, []);
        var vm = new CatalogEditorViewModel();
        Assert.True(await vm.LoadAsync(FilePath));
        Assert.Null(vm.SelectedCatalog);
        Assert.False(vm.HasSelectedCatalog);
        vm.NewCatalogCommand.Execute(null);
        vm.NewCatalogCommand.Execute(null);
        Assert.Equal(2, vm.Catalogs.Count);
        Assert.Equal(2, vm.Catalogs.Select(c => c.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(await vm.SaveAsync(FilePath));
        Assert.Equal(2, (await SoftwareCatalog.LoadAsync(FilePath)).Length);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public async Task EmptyCatalogNamesCannotBeSaved(string name)
    {
        var vm = await LoadEditorAsync();
        vm.SelectedCatalog!.Name = name;
        Assert.False(await vm.SaveAsync(FilePath));
    }

    [Fact]
    public void SearchIsDisabledWithoutAReadyManagerOrQuery()
    {
        var vm = new CatalogEditorViewModel();
        Assert.False(vm.SearchCommand.CanExecute(null));
        vm.Query = "Claude";
        Assert.False(vm.SearchCommand.CanExecute(null));
    }

    [Fact]
    public async Task SerializationRejectsInvalidDefinitionsBeforeCreatingAFile()
    {
        await Assert.ThrowsAsync<JsonException>(() => SoftwareCatalog.SaveAsync(FilePath,
            [new CatalogDefinition { Id = "", Name = "Bad", Packages = [] }]));
        Assert.False(File.Exists(FilePath));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task SearchUsesTheQueryAndPreservesFullIdentityWithoutDuplicates()
    {
        var vm = await LoadEditorAsync();
        var manager = new SearchManager();
        var source = new ManagerSource(manager, "private", new Uri("https://example.test/feed"));
        var package = new Package("Test package", "Company.Tool", "1.0", source, manager);
        manager.Results = [package, package];
        vm.SelectedManager = manager;
        vm.Query = "  tool  ";
        Assert.True(vm.CanSearch);
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Equal("tool", manager.LastQuery);
        var result = Assert.Single(vm.SearchResults);
        Assert.Equal("Company.Tool", result.Id);
        Assert.Equal("Catalog test manager", result.ManagerName);
        Assert.Equal("private", result.Source);
        Assert.Equal("", result.Version);
        Assert.True(result.Matches(package));
        Assert.True(new CatalogEntry
        {
            Id = result.Id,
            Name = result.Name,
            Source = result.Source,
            ManagerName = manager.Id,
        }.Matches(package));
        Assert.True(new CatalogEntry
        {
            Id = result.Id,
            Name = result.Name,
            Source = result.Source,
            ManagerName = manager.Name,
        }.Matches(package));
        vm.SelectedResult = result;
        vm.AddPackageCommand.Execute(null);
        Assert.Contains(result, vm.SelectedCatalog!.Packages);
        Assert.False(vm.IsSearching);
    }

    [Fact]
    public async Task SearchFailuresAreVisibleAndDoNotChangeCatalogContents()
    {
        var vm = await LoadEditorAsync();
        vm.SelectedManager = new SearchManager { Failure = new IOException("Search unavailable") };
        vm.Query = "tool";
        await vm.SearchCommand.ExecuteAsync(null);
        Assert.Contains("Search unavailable", vm.Message);
        Assert.Empty(vm.SearchResults);
        Assert.Single(vm.SelectedCatalog!.Packages);
        Assert.False(vm.IsDirty);
        Assert.False(vm.IsSearching);
    }

    [Fact]
    public async Task ManagerSwitchCancelsStaleSearchResults()
    {
        var vm = await LoadEditorAsync();
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var manager = new SearchManager { Started = started, Release = release };
        manager.Results = [new Package("Test", "Company.Tool", "1", manager.DefaultSource, manager)];
        vm.SelectedManager = manager;
        vm.Query = "tool";
        var search = vm.SearchCommand.ExecuteAsync(null);
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            vm.SelectedManager = new SearchManager();
        }
        finally
        {
            release.Set();
        }
        await search;
        Assert.Empty(vm.SearchResults);
        Assert.False(vm.IsSearching);
    }

    private sealed class SearchManager : IPackageManager
    {
        public IReadOnlyList<IPackage> Results { get; set; } = [];
        public string? LastQuery { get; private set; }
        public Exception? Failure { get; init; }
        public ManualResetEventSlim? Started { get; init; }
        public ManualResetEventSlim? Release { get; init; }
        public ManagerProperties Properties { get; } = new(IsDummy: true);
        public ManagerCapabilities Capabilities { get; } = new(IsDummy: true);
        public ManagerStatus Status { get; } = new();
        public string Id => "catalog-test";
        public string Name => "CatalogTest";
        public string DisplayName => "Catalog test manager";
        public IManagerSource DefaultSource => new ManagerSource(this, "test", new Uri("https://example.test/feed"));
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
        public bool LastInstalledListingFailed => false;
        public int? CompareVersions(string versionA, string versionB) => throw new NotSupportedException();
        public void Initialize() => throw new NotSupportedException();
        public bool IsEnabled() => true;
        public bool IsReady() => true;
        public IReadOnlyList<IPackage> FindPackages(string query)
        {
            LastQuery = query;
            Started?.Set();
            if (Release is not null && !Release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("The test did not release the search.");
            if (Failure is not null) throw Failure;
            return Results;
        }
        public IReadOnlyList<IPackage> GetAvailableUpdates() => throw new NotSupportedException();
        public IReadOnlyList<IPackage> GetInstalledPackages() => throw new NotSupportedException();
        public void RefreshPackageIndexes() => throw new NotSupportedException();
        public void AttemptFastRepair() => throw new NotSupportedException();
        public IReadOnlyList<string> FindCandidateExecutableFiles() => [];
        public Tuple<bool, string> GetExecutableFile() => Tuple.Create(false, "");
    }

    public void Dispose() => Directory.Delete(_directory, true);
}
