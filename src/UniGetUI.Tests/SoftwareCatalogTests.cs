using System.Text.Json;
using UniGetUI.Avalonia.Models;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Avalonia.Views;
using UniGetUI.Interface;
using UniGetUI.Core.Data;

namespace UniGetUI.Tests;

public class SoftwareCatalogTests
{
    private const string EntryJson = """
        {"name":"Claude","id":"Anthropic.Claude","manager":"winget","source":"winget"}
        """;

    private static CatalogEntry Entry => new()
    {
        Name = "Claude",
        Id = "Anthropic.Claude",
        Manager = "winget",
        Source = "winget",
    };

    [Fact]
    public void EditOpensTheActiveCatalogWithoutShellInterpolation()
    {
        var startInfo = SoftwareCatalog.CreateEditorStartInfo();
        Assert.False(startInfo.UseShellExecute);
        Assert.Equal(SoftwareCatalog.FilePath, startInfo.ArgumentList.Last());
        Assert.Empty(startInfo.Arguments);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("notepad.exe", startInfo.FileName);
            Assert.Single(startInfo.ArgumentList);
        }
    }

    [Fact]
    public void CatalogFileLivesInTheGlobalDirectory()
    {
        Assert.Equal(Path.Combine(CoreData.UniGetUIGlobalDirectory, "SoftwareCatalog.json"), SoftwareCatalog.FilePath);
    }

    private static string CatalogJson(string packages, string id = "tools", string name = "Tools") =>
        $$"""{"id":"{{id}}","name":"{{name}}","packages":[{{packages}}]}""";

    [Fact]
    public void MultipleCatalogsLoadWithSourceGeneratedMetadata()
    {
        var catalogs = SoftwareCatalog.Parse($"[{CatalogJson(EntryJson)},{CatalogJson(EntryJson, "development", "Development")}]");
        Assert.Equal(["tools", "development"], catalogs.Select(c => c.Id).ToArray());
        Assert.Equal(["Tools", "Development"], catalogs.Select(c => c.Name).ToArray());
        Assert.All(catalogs, c => Assert.Equal("Anthropic.Claude", Assert.Single(c.Packages).Id));
    }

    [Fact]
    public void EmptyCatalogsAreValidAndNoDefaultIsInjected()
    {
        Assert.Empty(SoftwareCatalog.Parse("[]"));
        Assert.Empty(Assert.Single(SoftwareCatalog.Parse($"[{CatalogJson("")}]")).Packages);
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
            await File.WriteAllTextAsync(path, $"[{CatalogJson(EntryJson)}]");
            Assert.Equal("tools", Assert.Single(await SoftwareCatalog.LoadAsync(path)).Id);
            await File.WriteAllTextAsync(path, $"[{CatalogJson("", "development", "Development")}]");
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
        Assert.Throws<JsonException>(() => SoftwareCatalog.Parse($"[{catalog}]"));

    [Fact]
    public void CatalogIdsMustBeUniqueRegardlessOfCase()
    {
        Assert.Throws<JsonException>(() => SoftwareCatalog.Parse($"[{CatalogJson("")},{CatalogJson("", "TOOLS")}]"));
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
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("""[{"name":"Claude","id":"Anthropic.Claude","manager":"winget"}]""")]
    [InlineData("""[{"name":"Claude","id":"","manager":"winget","source":"winget"}]""")]
    [InlineData("""[{"name":" ","id":"Anthropic.Claude","manager":"winget","source":"winget"}]""")]
    [InlineData("""[{"name":"Claude","id":"Anthropic.Claude","manager":null,"source":"winget"}]""")]
    public void InvalidCatalogIsRejected(string json) =>
        Assert.Throws<JsonException>(() => SoftwareCatalog.Parse(json));

    [Theory]
    [InlineData("""{"name":"Claude","id":"Anthropic.Claude","manager":"winget"}""")]
    [InlineData("""{"name":"Claude","id":"","manager":"winget","source":"winget"}""")]
    [InlineData("""{"name":" ","id":"Anthropic.Claude","manager":"winget","source":"winget"}""")]
    [InlineData("""{"name":"Claude","id":"Anthropic.Claude","manager":null,"source":"winget"}""")]
    [InlineData("null")]
    public void InvalidPackageInCatalogIsRejected(string package) =>
        Assert.Throws<JsonException>(() => SoftwareCatalog.Parse($"[{CatalogJson(package)}]"));

    [Fact]
    public void DuplicateIdentitiesAreRejectedRegardlessOfCase()
    {
        string duplicate = EntryJson.Replace("winget", "WINGET").Replace("Anthropic.Claude", "anthropic.claude");
        Assert.Throws<JsonException>(() => SoftwareCatalog.Parse($"[{CatalogJson($"{EntryJson},{duplicate}")}]"));
    }

    [Fact]
    public void SamePackageIdCanAppearInDifferentSources()
    {
        string otherSource = EntryJson.Replace("\"source\":\"winget\"", "\"source\":\"private\"");
        Assert.Equal(2, Assert.Single(SoftwareCatalog.Parse($"[{CatalogJson($"{EntryJson},{otherSource}")}]")).Packages.Length);
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
        Assert.Equal(PageType.Settings, MainWindowViewModel.GetNextPage(PageType.Catalog));
        Assert.Equal(enabled ? PageType.Catalog : PageType.Bundles, MainWindowViewModel.GetPreviousPage(PageType.Settings, enabled));
        Assert.Equal(PageType.Bundles, MainWindowViewModel.GetPreviousPage(PageType.Catalog));
    }

    [Fact]
    public void CatalogIsAnIpcNavigationTarget()
    {
        Assert.Contains("catalog", IpcAppPages.SupportedPages);
        Assert.Equal("catalog", IpcAppPages.NormalizePageName(" Catalog "));
        Assert.Equal("catalog", IpcAppPages.ToPageName(nameof(PageType.Catalog)));
    }
}
