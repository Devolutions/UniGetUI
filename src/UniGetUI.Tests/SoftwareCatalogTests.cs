using System.Text.Json;
using UniGetUI.Avalonia.Models;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Data;
using UniGetUI.Interface;

namespace UniGetUI.Tests;

public class SoftwareCatalogTests
{
    [Theory]
    [InlineData("clAuDe")]
    [InlineData("Anthropic.")]
    public void CatalogSearchMatchesNameAndIdCaseInsensitively(string query)
    {
        var vm = new SoftwareCatalogViewModel
        {
            HideUnavailablePackages = false,
            SelectedCatalog = new CatalogDefinition
            {
                Id = "test",
                Name = "Test",
                Packages =
                [
                    new CatalogEntry
                    {
                        Id = "Anthropic.Claude", Name = "Claude", ManagerName = "missing-test-manager", Source = "private",
                    },
                    new CatalogEntry { Id = "other", Name = "Other", ManagerName = "different-manager", Source = "public" },
                ],
            },
        };
        var original = vm.Packages[0];
        vm.Query = $"  {query}  ";
        Assert.Same(original, Assert.Single(vm.Packages));
        Assert.False(vm.HasHiddenPackages);
        vm.HideUnavailablePackages = true;
        Assert.Empty(vm.Packages);
        Assert.True(vm.HasHiddenPackages);
        vm.HideUnavailablePackages = false;
        Assert.Same(original, Assert.Single(vm.Packages));
        vm.Query = "no match";
        Assert.Empty(vm.Packages);
        Assert.True(vm.IsEmpty);
        Assert.False(vm.HasHiddenPackages);
        vm.Query = "";
        Assert.Equal(2, vm.Packages.Count);
        Assert.Same(original, vm.Packages[0]);
    }

    [Fact]
    public void CatalogDefaultsToTilesAndViewSwitchingPreservesPackagesAndFilters()
    {
        var vm = new SoftwareCatalogViewModel
        {
            HideUnavailablePackages = false,
            Query = "Claude",
            SelectedCatalog = new CatalogDefinition { Id = "test", Name = "Test", Packages = [Entry] },
        };
        Assert.Equal(3, vm.ViewModeIndex);
        Assert.False(vm.IsFilterPaneOpen);
        Assert.True(vm.IsTilesView);
        var tile = Assert.Single(vm.Packages);
        for (int index = 0; index < 4; index++)
        {
            vm.ViewModeIndex = index;
            Assert.Equal(index == 0, vm.IsListView);
            Assert.Equal(index == 1, vm.IsGridView);
            Assert.Equal(index == 2, vm.IsIconsView);
            Assert.Equal(index == 3, vm.IsTilesView);
            Assert.Same(tile, Assert.Single(vm.Packages));
            Assert.Equal("Claude", vm.Query);
            Assert.False(vm.HideUnavailablePackages);
        }
    }

    private const string EntryJson = """
        {"Id":"Anthropic.Claude","Name":"Claude","Version":"","Source":"winget","ManagerName":"WinGet"}
        """;

    private static CatalogEntry Entry => new()
    {
        Name = "Claude",
        Id = "Anthropic.Claude",
        ManagerName = "WinGet",
        Source = "winget",
    };

    [Theory]
    [InlineData(SearchMode.Name, "claude", 1)]
    [InlineData(SearchMode.Id, "claude", 1)]
    [InlineData(SearchMode.Name, "anthropic", 0)]
    [InlineData(SearchMode.Id, "anthropic", 1)]
    [InlineData(SearchMode.Both, "anthropic", 1)]
    [InlineData(SearchMode.Exact, "cla", 0)]
    [InlineData(SearchMode.Exact, "CLAUDE", 1)]
    [InlineData(SearchMode.Exact, "ANTHROPIC.CLAUDE", 1)]
    [InlineData(SearchMode.Exact, " ", 1)]
    public void CatalogSearchModesApplyImmediately(SearchMode mode, string query, int count)
    {
        var vm = new SoftwareCatalogViewModel
        {
            HideUnavailablePackages = false,
            SelectedCatalog = new CatalogDefinition { Id = "test", Name = "Test", Packages = [Entry] },
            Query = query,
            SearchMode = mode,
        };
        Assert.Equal(count, vm.Packages.Count);
        Assert.Equal(mode == SearchMode.Name, vm.SearchMode_Name);
        Assert.Equal(mode == SearchMode.Id, vm.SearchMode_Id);
        Assert.Equal(mode == SearchMode.Both, vm.SearchMode_Both);
        Assert.Equal(mode == SearchMode.Exact, vm.SearchMode_Exact);
        vm.Query = "anthropic";
        vm.SearchMode_Name = true;
        Assert.Empty(vm.Packages);
        vm.SearchMode_Id = true;
        Assert.Single(vm.Packages);
        vm.SearchMode_Exact = true;
        Assert.Empty(vm.Packages);
        vm.SearchMode_Both = true;
        Assert.Single(vm.Packages);
    }

    [Fact]
    public void CatalogSourcesFilterByManagerAndSourceAndPersistAcrossSameCatalogReload()
    {
        var catalog = new CatalogDefinition
        {
            Id = "test",
            Name = "Test",
            Packages =
            [
                new CatalogEntry { Id = "one", Name = "One", ManagerName = "missing-one", Source = "private" },
                new CatalogEntry { Id = "two", Name = "Two", ManagerName = "MISSING-ONE", Source = "public" },
                new CatalogEntry { Id = "three", Name = "Three", ManagerName = "missing-two", Source = "private" },
            ],
        };
        var vm = new SoftwareCatalogViewModel { HideUnavailablePackages = false, SelectedCatalog = catalog };
        Assert.False(vm.IsFilterPaneOpen);
        Assert.Equal(2, vm.SourceNodes.Count);
        Assert.Equal(2, vm.SourceNodes[0].Children.Count);
        var first = vm.Packages[0];
        vm.SourceNodes[0].Children[0].IsSelected = false;
        Assert.Equal(["two", "three"], vm.Packages.Select(p => p.Entry.Id));
        Assert.False(vm.SourceNodes[0].IsSelected);
        vm.SourceNodes[0].IsSelected = true;
        Assert.Equal(3, vm.Packages.Count);
        Assert.Same(first, vm.Packages[0]);
        vm.SourceNodes[0].IsSelected = false;
        Assert.Equal("three", Assert.Single(vm.Packages).Entry.Id);
        vm.SelectedCatalog = null;
        vm.SelectedCatalog = catalog;
        Assert.Equal("three", Assert.Single(vm.Packages).Entry.Id);
        vm.SelectAllSourcesCommand.Execute(null);
        Assert.Equal(3, vm.Packages.Count);
        vm.ClearSourceSelectionCommand.Execute(null);
        Assert.Empty(vm.Packages);
        vm.HideUnavailablePackages = true;
        vm.SelectAllSourcesCommand.Execute(null);
        Assert.Empty(vm.Packages);
        vm.HideUnavailablePackages = false;
        Assert.Equal(3, vm.Packages.Count);
        vm.ClearSourceSelectionCommand.Execute(null);
        vm.SelectedCatalog = new CatalogDefinition { Id = "other", Name = "Other", Packages = catalog.Packages };
        Assert.Equal(3, vm.Packages.Count);
    }

    [Fact]
    public void UnavailableSourcesAreHiddenByDefaultAndTheFilterRestoresTheSameTiles()
    {
        var vm = new SoftwareCatalogViewModel
        {
            SelectedCatalog = new CatalogDefinition
            {
                Id = "missing-manager",
                Name = "Missing manager",
                Packages = [new CatalogEntry { Id = "tool", Name = "Tool", Source = "private", ManagerName = "missing-test-manager" }],
            },
        };
        Assert.True(vm.HideUnavailablePackages);
        Assert.Empty(vm.Packages);
        Assert.True(vm.HasHiddenPackages);
        vm.HideUnavailablePackages = false;
        var tile = Assert.Single(vm.Packages);
        Assert.NotNull(tile.UnavailableReason);
        vm.HideUnavailablePackages = true;
        Assert.Empty(vm.Packages);
        vm.HideUnavailablePackages = false;
        Assert.Same(tile, Assert.Single(vm.Packages));
    }

    [Fact]
    public void FilterRespondsToSourceAvailabilityWithoutLosingStateOrChangingCatalogs()
    {
        var vm = new SoftwareCatalogViewModel { HideUnavailablePackages = false };
        var catalog = new CatalogDefinition
        {
            Id = "test",
            Name = "Test",
            Packages = [new CatalogEntry { Id = "tool", Name = "Tool", Source = "private", ManagerName = "missing-test-manager" }],
        };
        vm.SelectedCatalog = catalog;
        var tile = Assert.Single(vm.Packages);
        vm.HideUnavailablePackages = true;
        tile.UpdateState(true, null, true, false);
        vm.ApplyAvailabilityFilter();
        Assert.Same(tile, Assert.Single(vm.Packages));
        Assert.True(tile.IsInstalled);
        Assert.False(vm.HasHiddenPackages);
        tile.UpdateState(true, "Disabled source", true, false);
        vm.ApplyAvailabilityFilter();
        Assert.Empty(vm.Packages);
        Assert.Same(catalog, vm.SelectedCatalog);
        vm.SelectedCatalog = null;
        vm.HideUnavailablePackages = false;
        Assert.Empty(vm.Packages);
        Assert.False(vm.HasHiddenPackages);
    }

    [Fact]
    public void EditUsesAnAsynchronousInAppEditorCommand()
    {
        Assert.IsAssignableFrom<CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(new SoftwareCatalogViewModel().EditCommand);
    }

    [Fact]
    public void CatalogFileLivesInTheGlobalDirectory()
    {
        Assert.Equal(Path.Combine(CoreData.UniGetUIGlobalDirectory, "SoftwareCatalog.json"), SoftwareCatalog.FilePath);
    }

    private static string CatalogJson(string packages, string id = "tools", string name = "Tools") =>
        $$"""{"id":"{{id}}","name":"{{name}}","packages":[{{packages}}]}""";

    private static string DocumentJson(string catalogs) =>
        $$"""{"version":1,"catalogs":[{{catalogs}}]}""";

    [Fact]
    public void MultipleCatalogsLoadWithSourceGeneratedMetadata()
    {
        var catalogs = SoftwareCatalog.Parse(DocumentJson($"{CatalogJson(EntryJson)},{CatalogJson(EntryJson, "development", "Development")}"));
        Assert.Equal(["tools", "development"], catalogs.Select(c => c.Id).ToArray());
        Assert.Equal(["Tools", "Development"], catalogs.Select(c => c.Name).ToArray());
        Assert.All(catalogs, c => Assert.Equal("Anthropic.Claude", Assert.Single(c.Packages).Id));
    }

    [Fact]
    public void TheRequestedBundleStyleDocumentLoadsExactly()
    {
        const string json = """
            {
              "version": 1,
              "catalogs": [
                {
                  "id": "devolutions",
                  "name": "Devolutions",
                  "packages": [
                    {
                      "Id": "Microsoft.VisualStudioCode",
                      "Name": "Visual Studio Code",
                      "Version": "",
                      "Source": "winget",
                      "ManagerName": "WinGet"
                    }
                  ]
                }
              ]
            }
            """;
        var catalog = Assert.Single(SoftwareCatalog.Parse(json));
        Assert.Equal("devolutions", catalog.Id);
        Assert.Equal("Devolutions", catalog.Name);
        var entry = Assert.Single(catalog.Packages);
        Assert.Equal("Microsoft.VisualStudioCode", entry.Id);
        Assert.Equal("Visual Studio Code", entry.Name);
        Assert.Equal("", entry.Version);
        Assert.Equal("winget", entry.Source);
        Assert.Equal("WinGet", entry.ManagerName);
    }

    [Fact]
    public void MissingPackageVersionDefaultsToEmptyLikeBundles()
    {
        string entry = EntryJson.Replace("\"Version\":\"\",", "");
        Assert.Equal("", Assert.Single(Assert.Single(SoftwareCatalog.Parse(DocumentJson(CatalogJson(entry)))).Packages).Version);
    }

    [Fact]
    public void LegacyPackageFieldsInsideANewDocumentAreRejected()
    {
        const string entry = """{"name":"Claude","id":"Anthropic.Claude","manager":"winget","source":"winget"}""";
        Assert.Throws<JsonException>(() => SoftwareCatalog.Parse(DocumentJson(CatalogJson(entry))));
    }

    [Fact]
    public void EmptyCatalogsAreValidAndNoDefaultIsInjected()
    {
        Assert.Empty(SoftwareCatalog.Parse(DocumentJson("")));
        Assert.Empty(Assert.Single(SoftwareCatalog.Parse(DocumentJson(CatalogJson("")))).Packages);
    }

    [Fact]
    public async Task MissingCatalogIsReportedInsteadOfUsingADefault()
    {
        string path = Path.Combine(Path.GetTempPath(), $"missing-catalog-{Guid.NewGuid():N}.json");
        await Assert.ThrowsAsync<FileNotFoundException>(() => SoftwareCatalog.LoadAsync(path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task CatalogFileReloadReadsUpdatedCatalogDefinitions()
    {
        string path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path, DocumentJson(CatalogJson(EntryJson)));
            Assert.Equal("tools", Assert.Single(await SoftwareCatalog.LoadAsync(path)).Id);
            await File.WriteAllTextAsync(path, DocumentJson(CatalogJson("", "development", "Development")));
            var catalog = Assert.Single(await SoftwareCatalog.LoadAsync(path));
            Assert.Equal("development", catalog.Id);
            Assert.Equal("Development", catalog.Name);
            Assert.Empty(catalog.Packages);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CatalogSelectorTracksEmptyCatalogs()
    {
        var vm = new SoftwareCatalogViewModel();
        var first = new CatalogDefinition { Id = "first", Name = "First", Packages = [] };
        var second = new CatalogDefinition { Id = "second", Name = "Second", Packages = [] };
        vm.Catalogs.Add(first);
        vm.Catalogs.Add(second);
        vm.SelectedCatalog = first;
        Assert.Same(first, vm.SelectedCatalog);
        Assert.True(vm.IsEmpty);
        vm.SelectedCatalog = second;
        Assert.Same(second, vm.SelectedCatalog);
        Assert.Empty(vm.Packages);
        vm.ErrorMessage = "Catalog file unavailable";
        Assert.False(vm.IsEmpty);
        Assert.True(vm.HasError);
    }

    [Fact]
    public void ExperimentalCatalogOptionIsSearchable()
    {
        Assert.Contains(
            global::UniGetUI.Avalonia.Infrastructure.SettingsSearchIndex.Search("software catalog"),
            result => result.Anchor == "SoftwareCatalogCard");
    }

    [Theory]
    [InlineData("""{"id":"","name":"Tools","packages":[]}""")]
    [InlineData("""{"id":"tools","name":" ","packages":[]}""")]
    [InlineData("""{"id":"tools","name":"Tools"}""")]
    [InlineData("""{"id":"tools","name":"Tools","packages":null}""")]
    [InlineData("""{"id":null,"name":"Tools","packages":[]}""")]
    [InlineData("null")]
    public void InvalidCatalogDefinitionIsRejected(string catalog) =>
        Assert.Throws<JsonException>(() => SoftwareCatalog.Parse(DocumentJson(catalog)));

    [Fact]
    public void CatalogIdsMustBeUniqueRegardlessOfCase()
    {
        Assert.Throws<JsonException>(() => SoftwareCatalog.Parse(DocumentJson($"{CatalogJson("")},{CatalogJson("", "TOOLS")}")));
    }

    [Fact]
    public void CatalogIdentityIncludesManagerAndSource()
    {
        Assert.True(Entry.Matches("anthropic.claude", "WinGet", "WINGET"));
        Assert.False(Entry.Matches("Anthropic.Claude", "scoop", "winget"));
        Assert.False(Entry.Matches("Anthropic.Claude", "winget", "msstore"));
        Assert.False(Entry.Matches("Anthropic.Claude.Other", "winget", "winget"));
    }

    [Theory]
    [InlineData("Chocolatey")]
    [InlineData("chocolatey")]
    [InlineData("CHOCOLATEY")]
    public void ChocolateyInstalledMatchingIgnoresFeedButRepositoryMatchingRemainsExact(string managerName)
    {
        var manager = new UniGetUI.PackageEngine.Managers.ChocolateyManager.Chocolatey();
        var entry = new CatalogEntry
        {
            Id = "example.tool",
            Name = "Example tool",
            ManagerName = managerName,
            Source = "private",
        };
        var installed = new UniGetUI.PackageEngine.PackageClasses.Package(
            "Example tool", "EXAMPLE.TOOL", "1.0", manager.DefaultSource, manager);
        Assert.True(entry.MatchesInstalled(installed));
        Assert.False(entry.Matches(installed));
        var privateSource = new UniGetUI.PackageEngine.Classes.Manager.ManagerSource(
            manager, "private", new Uri("https://example.test/feed"));
        var repositoryPackage = new UniGetUI.PackageEngine.PackageClasses.Package(
            "Example tool", entry.Id, "2.0", privateSource, manager);
        Assert.True(entry.Matches(repositoryPackage));
        Assert.True(entry.MatchesInstalled(repositoryPackage));
        var differentPackage = new UniGetUI.PackageEngine.PackageClasses.Package(
            "Different tool", "example.other", "1.0", manager.DefaultSource, manager);
        Assert.False(entry.MatchesInstalled(differentPackage));
        var tile = new CatalogTileViewModel(entry);
        tile.UpdateState(entry.MatchesInstalled(installed), null, true, false);
        Assert.True(tile.IsInstalled);
        Assert.False(tile.ShowInstall);
        Assert.False(tile.CanInstall);
    }

    [Fact]
    public void OtherManagersStillRequireTheInstalledSourceAndManagerToMatch()
    {
        var manager = new UniGetUI.PackageEngine.Managers.NpmManager.Npm();
        var entry = new CatalogEntry
        {
            Id = "example.tool",
            Name = "Example tool",
            ManagerName = manager.Id,
            Source = "private",
        };
        var package = new UniGetUI.PackageEngine.PackageClasses.Package(
            entry.Name, entry.Id, "1.0", manager.DefaultSource, manager);
        Assert.False(entry.MatchesInstalled(package));
        var privateSource = new UniGetUI.PackageEngine.Classes.Manager.ManagerSource(
            manager, "PRIVATE", new Uri("https://example.test/feed"));
        package = new UniGetUI.PackageEngine.PackageClasses.Package(
            entry.Name, entry.Id, "1.0", privateSource, manager);
        Assert.True(entry.MatchesInstalled(package));
        var chocolateyEntry = new CatalogEntry
        {
            Id = entry.Id,
            Name = entry.Name,
            ManagerName = "chocolatey",
            Source = "private",
        };
        Assert.False(chocolateyEntry.MatchesInstalled(package));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"catalogs":[]}""")]
    [InlineData("""{"version":1}""")]
    [InlineData("""{"version":0,"catalogs":[]}""")]
    [InlineData("""{"version":2,"catalogs":[]}""")]
    [InlineData("""{"version":1,"catalogs":null}""")]
    [InlineData("""{"version":"1","catalogs":[]}""")]
    [InlineData("[]")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("""[{"name":"Claude","id":"Anthropic.Claude","manager":"winget"}]""")]
    [InlineData("""[{"name":"Claude","id":"","manager":"winget","source":"winget"}]""")]
    [InlineData("""[{"name":" ","id":"Anthropic.Claude","manager":"winget","source":"winget"}]""")]
    [InlineData("""[{"name":"Claude","id":"Anthropic.Claude","manager":null,"source":"winget"}]""")]
    public void InvalidCatalogIsRejected(string json) =>
        Assert.Throws<JsonException>(() => SoftwareCatalog.Parse(json));

    [Theory]
    [InlineData("""{"Name":"Claude","Id":"Anthropic.Claude","ManagerName":"WinGet"}""")]
    [InlineData("""{"Name":"Claude","Id":"","ManagerName":"WinGet","Source":"winget"}""")]
    [InlineData("""{"Name":" ","Id":"Anthropic.Claude","ManagerName":"WinGet","Source":"winget"}""")]
    [InlineData("""{"Name":"Claude","Id":"Anthropic.Claude","ManagerName":null,"Source":"winget"}""")]
    [InlineData("""{"Name":"Claude","Id":"Anthropic.Claude","ManagerName":"WinGet","Source":"winget","Version":null}""")]
    [InlineData("null")]
    public void InvalidPackageInCatalogIsRejected(string package) =>
        Assert.Throws<JsonException>(() => SoftwareCatalog.Parse(DocumentJson(CatalogJson(package))));

    [Fact]
    public void DuplicateIdentitiesAreRejectedRegardlessOfCase()
    {
        string duplicate = EntryJson.Replace("WinGet", "WINGET").Replace("winget", "WINGET").Replace("Anthropic.Claude", "anthropic.claude");
        Assert.Throws<JsonException>(() => SoftwareCatalog.Parse(DocumentJson(CatalogJson($"{EntryJson},{duplicate}"))));
    }

    [Fact]
    public void SamePackageIdCanAppearInDifferentSources()
    {
        string otherSource = EntryJson.Replace("\"Source\":\"winget\"", "\"Source\":\"private\"");
        Assert.Equal(2, Assert.Single(SoftwareCatalog.Parse(DocumentJson(CatalogJson($"{EntryJson},{otherSource}")))).Packages.Length);
    }

    [Fact]
    public void AvailableUninstalledTileCanInstall()
    {
        var tile = new CatalogTileViewModel(Entry);
        tile.UpdateState(false, null, true, false);

        Assert.True(tile.ShowInstall);
        Assert.True(tile.CanInstall);
        Assert.Equal(1, tile.TitleOpacity);
        Assert.Equal("", tile.StatusText);
        Assert.Null(tile.IconBitmap);
        Assert.False(tile.HasCustomIcon);
    }

    [Fact]
    public void CatalogIconChangesNotifyTheTile()
    {
        global::Avalonia.Skia.SkiaPlatform.Initialize();
        var tile = new CatalogTileViewModel(Entry);
        var changed = new List<string?>();
        tile.PropertyChanged += (_, args) => changed.Add(args.PropertyName);
        using var bitmap = new global::Avalonia.Media.Imaging.WriteableBitmap(
            new global::Avalonia.PixelSize(1, 1), new global::Avalonia.Vector(96, 96),
            global::Avalonia.Platform.PixelFormat.Bgra8888, global::Avalonia.Platform.AlphaFormat.Premul);

        tile.IconBitmap = bitmap;
        Assert.True(tile.HasCustomIcon);
        Assert.Contains(nameof(tile.IconBitmap), changed);
        Assert.Contains(nameof(tile.HasCustomIcon), changed);
        tile.IconBitmap = null;
        Assert.False(tile.HasCustomIcon);
    }

    [Fact]
    public void InstalledTileHidesInstallButton()
    {
        var tile = new CatalogTileViewModel(Entry);
        tile.UpdateState(true, null, true, false);

        Assert.True(tile.IsInstalled);
        Assert.False(tile.ShowInstall);
        Assert.False(tile.CanInstall);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UnavailableSourceDimsTitleAndExplainsWhy(bool installed)
    {
        var tile = new CatalogTileViewModel(Entry);
        tile.UpdateState(installed, "Source winget is disabled.", true, false);

        Assert.Equal(0.45, tile.TitleOpacity);
        Assert.Equal("Source winget is disabled.", tile.UnavailableReason);
        Assert.Equal(tile.UnavailableReason, tile.StatusText);
        Assert.False(tile.CanInstall);
        Assert.Equal(!installed, tile.ShowInstall);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    public void UnknownInventoryAndPendingInstallsCannotInstall(bool inventoryKnown, bool pending, bool busy)
    {
        var tile = new CatalogTileViewModel(Entry);
        tile.UpdateState(false, null, inventoryKnown, pending);
        tile.IsBusy = busy;

        Assert.False(tile.CanInstall);
        Assert.NotEmpty(tile.StatusText);
    }

    [Fact]
    public void TileRecoversAfterSourceIsEnabledOrInstallFails()
    {
        var tile = new CatalogTileViewModel(Entry);
        tile.UpdateState(false, "Source disabled.", true, false);
        tile.UpdateState(false, null, true, true);
        tile.UpdateState(false, null, true, false);

        Assert.True(tile.CanInstall);
        Assert.Equal(1, tile.TitleOpacity);
        Assert.Null(tile.UnavailableReason);
    }

    [Fact]
    public void TileTracksInstallAndUninstall()
    {
        var tile = new CatalogTileViewModel(Entry);
        tile.UpdateState(false, null, true, true);
        Assert.False(tile.CanInstall);
        tile.UpdateState(true, null, true, false);
        Assert.False(tile.ShowInstall);
        tile.UpdateState(false, null, true, false);
        Assert.True(tile.ShowInstall);
        Assert.True(tile.CanInstall);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CatalogParticipatesInKeyboardPageCycleOnlyWhenEnabled(bool enabled)
    {
        Assert.Equal(enabled ? PageType.Catalog : PageType.Settings, MainWindowViewModel.GetNextPage(PageType.Bundles, enabled));
        Assert.Equal(enabled ? PageType.CatalogEditor : PageType.Settings, MainWindowViewModel.GetNextPage(PageType.Catalog, enabled));
        Assert.Equal(PageType.Settings, MainWindowViewModel.GetNextPage(PageType.CatalogEditor, enabled));
        Assert.Equal(enabled ? PageType.CatalogEditor : PageType.Bundles, MainWindowViewModel.GetPreviousPage(PageType.Settings, enabled));
        Assert.Equal(enabled ? PageType.Catalog : PageType.Bundles, MainWindowViewModel.GetPreviousPage(PageType.CatalogEditor, enabled));
        Assert.Equal(PageType.Bundles, MainWindowViewModel.GetPreviousPage(PageType.Catalog));
    }

    [Fact]
    public void CatalogIsAnIpcNavigationTarget()
    {
        Assert.Contains("catalog", IpcAppPages.SupportedPages);
        Assert.Contains("catalog-editor", IpcAppPages.SupportedPages);
        Assert.Equal("catalog-editor", IpcAppPages.NormalizePageName("Catalog-Editor"));
        Assert.Equal("catalog-editor", IpcAppPages.ToPageName(nameof(PageType.CatalogEditor)));
        Assert.Equal("catalog", IpcAppPages.NormalizePageName(" Catalog "));
        Assert.Equal("catalog", IpcAppPages.ToPageName(nameof(PageType.Catalog)));
    }
}
