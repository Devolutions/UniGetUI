using NUnit.Framework;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.Operations.History;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Views.Dialogs;
using UniGetUI.Tui.Views.Pages;
using K = Avalonia.Input.Key;
using NAssert = NUnit.Framework.Assert;

namespace UniGetUI.Tui.Tests;

/// <summary>Discover / Updates / Installed, driven by keyboard against the fake data set.</summary>
[TestFixture]
[NonParallelizable]
internal sealed class PackagePagesE2ETests : TuiE2ETestBase
{
    private static PackageListPage Page(string id) => Window.GetPage<PackageListPage>(id);

    [Test]
    public async Task InstalledPage_ListsSeededPackages_FiltersAndShowsDetails()
    {
        await ResetAsync(TuiPageIds.Installed);
        await WaitForTextAsync("Installed Packages", "Contoso Editor", "Woodgrove Vault", "16 packages  ·");

        await EnterPage();
        await FilterPackagesAsync("Woodgrove");
        await WaitForTextAsync("1 of 16 shown");
        await WaitForNoTextAsync("Contoso Editor");

        await Key(K.Enter);
        await WaitForDialogAsync("Package details");
        await WaitForTextAsync("A password manager with end-to-end encrypted vaults.", "Woodgrove Bank", "GPL-3.0");
        await Key(K.Escape);
        await WaitForNoDialogAsync();
    }

    [Test]
    public async Task UpdatesPage_UpdatesAPackage_ThroughTheFakeManagerProcess()
    {
        await ResetAsync(TuiPageIds.Updates);
        await WaitForTextAsync("Software Updates", "Northwind Terminal", "1.20.1");
        await EnterPage();
        await FilterPackagesAsync("Northwind Terminal");
        await WaitForTextAsync("1 of 13 shown");

        await Type("u");
        await WaitForOperationsToFinishAsync();

        NAssert.That(InstalledVersion("Winget", "Northwind.Terminal"), Is.EqualTo("1.20.1"));
        await WaitUntilAsync(() => UpgradablePackagesLoader.Instance.Packages.All(p => p.Id != "Northwind.Terminal"), "update to leave the Updates list");
        await WaitForTextAsync("of 12 shown");
        var record = OperationHistoryStore.GetAll().Single(r => r.PackageId == "Northwind.Terminal");
        NAssert.That(record.Status, Is.EqualTo(OperationHistoryRecord.StatusSucceeded));
        NAssert.That(record.Kind, Is.EqualTo("update-package"));
    }

    [Test]
    public async Task DiscoverPage_SearchesAndInstalls()
    {
        await ResetAsync(TuiPageIds.Discover);
        await EnterPage();
        await Type("photo");
        await Key(K.Enter);
        await WaitForTextAsync("Contoso Photo Studio", "2025.1");
        await Key(K.Down);
        await Type("i");
        await WaitForOperationsToFinishAsync();

        NAssert.That(InstalledVersion("Winget", "Contoso.PhotoStudio"), Is.EqualTo("2025.1"));
        await WaitUntilAsync(() => InstalledPackagesLoader.Instance.Packages.Any(p => p.Id == "Contoso.PhotoStudio"), "the package to appear as installed");
        await OnUi(() => Window.NavigateTo(TuiPageIds.Installed));
        await WaitForTextAsync("Contoso Photo Studio", "17 packages  ·");
    }

    [Test]
    public async Task InstalledPage_UninstallAsksForConfirmation()
    {
        await ResetAsync(TuiPageIds.Installed);
        await EnterPage();
        await FilterPackagesAsync("Woodgrove");
        await Type("x");
        await WaitForDialogAsync("Uninstall");
        await WaitForTextAsync("Do you really want to uninstall", "Woodgrove Vault");

        // "No" keeps the package.
        await Key(K.Right);
        await Key(K.Enter);
        await WaitForNoDialogAsync();
        NAssert.That(InstalledVersion("Winget", "Woodgrove.Vault"), Is.EqualTo("7.4.1"));

        await Type("x");
        await WaitForDialogAsync("Uninstall");
        await Key(K.Enter);
        await WaitForOperationsToFinishAsync();
        NAssert.That(InstalledVersion("Winget", "Woodgrove.Vault"), Is.Null);
        await WaitForTextAsync("of 15 shown");
    }

    [Test]
    public async Task FailingUpdate_IsReportedAsFailed_AndLeavesTheSystemUnchanged()
    {
        await ResetAsync(TuiPageIds.Updates);
        await EnterPage();
        await FilterPackagesAsync("Failing");
        await Type("u");
        await WaitForOperationsToFinishAsync();

        NAssert.That(InstalledVersion("Winget", "Fabrikam.FailingUpdater"), Is.EqualTo("1.0.0"));
        await OnUi(() => Window.NavigateTo(TuiPageIds.Operations));
        await WaitForTextAsync("Fabrikam Failing Updater", "Failed", "Fatal error during update");
        NAssert.That(OperationHistoryStore.GetAll().Single().Status, Is.EqualTo(OperationHistoryRecord.StatusFailed));
    }

    [Test]
    public async Task UpdatesPage_SelectionAndBulkUpdate()
    {
        await ResetAsync(TuiPageIds.Updates);
        await WaitForTextAsync("·  13 selected");
        await EnterPage();
        await Key(K.A, RawModifiers.Control);
        await WaitForTextAsync("·  0 selected");

        // Select two rows with Space, then update the selection.
        await FilterPackagesAsync("fabrikam-");
        await WaitForTextAsync("2 of 13 shown");
        await Key(K.Space);
        await Key(K.Down);
        await Key(K.Space);
        await WaitForTextAsync("·  2 selected");
        await Key(K.Enter, RawModifiers.Control);
        await WaitForOperationsToFinishAsync();

        NAssert.That(InstalledVersion("Scoop", "fabrikam-grep"), Is.EqualTo("14.1.0"));
        NAssert.That(InstalledVersion("Pip", "fabrikam-numbers"), Is.EqualTo("2.1.0"));
        NAssert.That(InstalledVersion("Winget", "Contoso.Editor"), Is.EqualTo("4.1.0"), "unselected packages must not be updated");
    }

    [Test]
    public async Task IgnoreUpdates_AndManageIgnoredUpdates()
    {
        await ResetAsync(TuiPageIds.Updates);
        await EnterPage();
        await FilterPackagesAsync("Fabrikam Browser");
        await Type("g");
        await WaitUntilAsync(() => UpgradablePackagesLoader.Instance.Packages.All(p => p.Id != "Fabrikam.Browser"), "the update to be ignored");
        NAssert.That(IgnoredUpdatesDatabase.GetDatabase().Keys, Has.Some.Contains("Fabrikam.Browser"));

        await Type("m");
        await ChooseAsync("Manage ignored updates");
        await WaitForDialogAsync("Manage ignored updates");
        await WaitForTextAsync("Fabrikam.Browser", "All versions");
        await ChooseAsync("Fabrikam");
        await WaitUntilAsync(() => UpgradablePackagesLoader.Instance.Packages.Any(p => p.Id == "Fabrikam.Browser"), "the update to come back");
        NAssert.That(IgnoredUpdatesDatabase.GetDatabase().Keys, Has.None.Contains("Fabrikam.Browser"));
        await Key(K.Escape);
        await WaitForNoDialogAsync();
    }

    [Test]
    public async Task SkipVersion_AndPauseUpdates_FromTheActionMenu()
    {
        await ResetAsync(TuiPageIds.Updates);
        await EnterPage();
        await FilterPackagesAsync("Contoso Editor");
        await Type("m");
        await ChooseAsync("Skip this version");
        await WaitUntilAsync(() => UpgradablePackagesLoader.Instance.Packages.All(p => p.Id != "Contoso.Editor"), "the version to be skipped");
        NAssert.That(IgnoredUpdatesDatabase.GetDatabase().Single(kv => kv.Key.Contains("Contoso.Editor")).Value, Is.EqualTo("4.3.1"));

        await FilterPackagesAsync("Northwind Terminal");
        await Type("m");
        await ChooseAsync("Pause updates for");
        await ChooseAsync("1 week");
        await WaitUntilAsync(() => UpgradablePackagesLoader.Instance.Packages.All(p => p.Id != "Northwind.Terminal"), "updates to be paused");
        NAssert.That(IgnoredUpdatesDatabase.GetDatabase().Single(kv => kv.Key.Contains("Northwind.Terminal")).Value, Does.StartWith("<"));
    }

    [Test]
    public async Task InstallOptions_PinAVersion_ThenInstallUsesIt()
    {
        await ResetAsync(TuiPageIds.Discover);
        await EnterPage();
        await Type("litware");
        await Key(K.Enter);
        await WaitForTextAsync("Litware Media Player");
        await Key(K.Down);
        await Type("o");
        await WaitForDialogAsync("installation options");
        await WaitUntilAsync(() => TuiModal.Top is InstallOptionsDialog { StateLoaded: true }, "the options to load");
        await WaitForDialogAsync("installation options");
        await FocusFieldAsync("Follow the default options");
        await Key(K.Space);
        await FocusFieldAsync("Version to install:");
        for (int i = 0; i < 6 && !(await FocusedTextAsync()).Contains("3.0.20"); i++)
            await Key(K.Right);
        NAssert.That(await FocusedTextAsync(), Does.Contain("3.0.20"));
        await PressButtonAsync("Save and close");
        await WaitForNoDialogAsync();

        await Type("i");
        await WaitForOperationsToFinishAsync();
        NAssert.That(InstalledVersion("Winget", "Litware.MediaPlayer"), Is.EqualTo("3.0.20"));
    }

    [Test]
    public async Task SortAndSourceFilter()
    {
        await ResetAsync(TuiPageIds.Installed);
        await EnterPage();
        await Type("s");
        await ChooseAsync("Id");
        await WaitUntilAsync(() => Page(TuiPageIds.Installed).SortField == 1, "sort by id");
        await Type("s");
        await ChooseAsync("Descending");
        await WaitUntilAsync(() => Page(TuiPageIds.Installed).VisibleRows[0].Id == "Woodgrove.Vault", "descending id sort");

        await Type("f");
        await WaitForDialogAsync("Filter by source");
        await PressButtonAsync("Clear selection");
        await FocusFieldAsync("Npm", K.Tab);
        await Key(K.Space);
        await PressButtonAsync("Apply");
        await WaitForNoDialogAsync();
        await WaitUntilAsync(() => Page(TuiPageIds.Installed).VisibleRows.Count == 2, "only Npm packages");
        await WaitForTextAsync("@contoso/cli", "northwind-lint", " hidden ]");

        // Restore defaults for the next tests.
        await Type("f");
        await WaitForDialogAsync("Filter by source");
        await PressButtonAsync("Select all");
        await PressButtonAsync("Apply");
        await Type("s");
        await ChooseAsync("Name");
        await Type("s");
        await ChooseAsync("Ascending");
    }

    [Test]
    public async Task ReinstallAndUninstallThenReinstall_FromTheInstalledMenu()
    {
        await ResetAsync(TuiPageIds.Installed);
        await EnterPage();
        await FilterPackagesAsync("Contoso Notes");
        await Type("m");
        await ChooseAsync("Uninstall package, then reinstall it");
        await WaitForOperationsToFinishAsync();
        NAssert.That(InstalledVersion("Winget", "Contoso.Notes"), Is.EqualTo("2.0.0"));
        var kinds = OperationHistoryStore.GetAll().Select(r => r.Kind).ToList();
        NAssert.That(kinds, Is.EquivalentTo(new[] { "uninstall-package", "install-package" }));
    }

    [Test]
    public async Task UpdateToVersion_FromTheInstalledPage()
    {
        await ResetAsync(TuiPageIds.Installed);
        await EnterPage();
        await FilterPackagesAsync("contoso-7zip");
        await Type("u");
        await WaitForOperationsToFinishAsync();
        NAssert.That(InstalledVersion("Scoop", "contoso-7zip"), Is.EqualTo("24.08"));
    }

    [Test]
    public async Task ManualCommand_ShowsTheStandaloneCommandLine()
    {
        await ResetAsync(TuiPageIds.Updates);
        await EnterPage();
        await FilterPackagesAsync("Contoso Editor");
        await Type("m");
        await ChooseAsync("Manual update");
        await WaitForDialogAsync("Manual update");
        await WaitForTextAsync("winget.exe update --id Contoso.Editor");
        await Key(K.Escape);
    }

    [Test]
    public async Task DownloadInstaller_WritesAFileWithoutTouchingTheNetwork()
    {
        await ResetAsync(TuiPageIds.Discover);
        await EnterPage();
        await Type("proseware");
        await Key(K.Enter);
        await WaitForTextAsync("Proseware Archiver");
        await Key(K.Down);
        await Type("m");
        await ChooseAsync("Download installer");
        await WaitForDialogAsync("Download installer");
        string folder = Path.Join(FakeDataSetUp.Sandbox, "downloads-test");
        await ReplaceTextAsync(folder);
        await Key(K.Enter);
        await WaitForOperationsToFinishAsync();
        string[] files = Directory.GetFiles(folder);
        NAssert.That(files, Has.Length.EqualTo(1));
        NAssert.That(File.ReadAllText(files[0]), Does.Contain("fake-data placeholder installer"));
    }

    [Test]
    public async Task ExportToCsv()
    {
        await ResetAsync(TuiPageIds.Installed);
        await EnterPage();
        await Type("m");
        await ChooseAsync("Export to CSV");
        await WaitForDialogAsync("Export to CSV");
        string path = Path.Join(FakeDataSetUp.Sandbox, "installed.csv");
        await ReplaceTextAsync(path);
        await Key(K.Enter);
        await WaitUntilAsync(() => File.Exists(path), "the CSV file");
        string csv = File.ReadAllText(path);
        NAssert.That(csv, Does.StartWith("﻿Name,Id,Version,Source,Manager").Or.StartWith("Name,Id,Version,Source,Manager"));
        NAssert.That(csv.Split('\n', StringSplitOptions.RemoveEmptyEntries), Has.Length.EqualTo(17));
        NAssert.That(csv, Does.Contain("Woodgrove Vault,Woodgrove.Vault,7.4.1"));
    }

    [Test]
    public async Task SearchOptions_ExactMatch()
    {
        await ResetAsync(TuiPageIds.Installed);
        await EnterPage();
        await Type("m");
        await ChooseAsync("Search options");
        await WaitForDialogAsync("Search options");
        await Key(K.Right);
        await Key(K.Right);
        await Key(K.Right);
        NAssert.That(await FocusedTextAsync(), Does.Contain("Exact match"));
        await PressButtonAsync("Apply");
        await FilterPackagesAsync("contoso");
        await WaitUntilAsync(() => Page(TuiPageIds.Installed).VisibleRows.Count == 0, "no exact match for a partial name");
        await FilterPackagesAsync("contoso-7zip");
        await WaitUntilAsync(() => Page(TuiPageIds.Installed).VisibleRows.Count == 1, "an exact id match");
        await OnUi(() => Page(TuiPageIds.Installed).SetSearchOptions(PackageSearchMode.Both, false, false, true));
    }

    [Test]
    public async Task DetailsDialog_MainActionInstalls()
    {
        await ResetAsync(TuiPageIds.Discover);
        await EnterPage();
        await Type("tailspin-jq");
        await Key(K.Enter);
        await WaitForTextAsync("tailspin-jq");
        await Key(K.Down);
        await Key(K.Enter);
        await WaitForDialogAsync("Package details");
        await WaitForTextAsync("A lightweight and flexible command-line JSON processor.", "Scoop");
        await PressButtonAsync("Install");
        await WaitForOperationsToFinishAsync();
        NAssert.That(InstalledVersion("Scoop", "tailspin-jq"), Is.EqualTo("1.7.1"));
    }
}
