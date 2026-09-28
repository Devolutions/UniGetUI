using NUnit.Framework;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools.Scheduling;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Operations.History;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageOperations;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Views.Dialogs;
using UniGetUI.Tui.Views.Pages;
using K = Avalonia.Input.Key;
using NAssert = NUnit.Framework.Assert;

namespace UniGetUI.Tui.Tests;

/// <summary>Bundles, Operations, Managers, Settings, Logs, History and Help, driven by keyboard.</summary>
[TestFixture]
[NonParallelizable]
internal sealed class OtherPagesE2ETests : TuiE2ETestBase
{
    [Test]
    public async Task Bundles_AddSaveNewOpenAndInstall()
    {
        await ResetAsync(TuiPageIds.Discover);
        await EnterPage();
        await Type("proseware");
        await Key(K.Enter);
        await WaitForTextAsync("Proseware Archiver");
        await Key(K.Down);
        await Type("b");
        await WaitUntilAsync(() => PackageBundlesLoader.Instance.Packages.Count == 1, "the bundle to receive the package");

        await OnUi(() => Window.NavigateTo(TuiPageIds.Bundles));
        await WaitForTextAsync("Package Bundles", "unsaved changes", "Proseware Archiver", "Bundles *");
        await EnterPage();
        await Key(K.S, RawModifiers.Control);
        await WaitForDialogAsync("Save as");
        string path = Path.Join(FakeDataSetUp.Sandbox, "test bundle.ubundle");
        await ReplaceTextAsync(path);
        await Key(K.Enter);
        await WaitUntilAsync(() => File.Exists(path), "the bundle file");
        await WaitForNoTextAsync("unsaved changes");
        NAssert.That(File.ReadAllText(path), Does.Contain("Proseware.Archiver"));

        await Type("n");
        await WaitUntilAsync(() => PackageBundlesLoader.Instance.Packages.Count == 0, "a new empty bundle");
        await WaitForTextAsync("Add packages or open an existing package bundle");

        await Key(K.O, RawModifiers.Control);
        await WaitForDialogAsync("Open existing bundle");
        await ReplaceTextAsync(path);
        await Key(K.Enter);
        await WaitForTextAsync("Proseware Archiver", "1 package(s) loaded");

        await EnterPage();
        await WaitUntilAsync(() => Window.GetPage<PackageListPage>(TuiPageIds.Bundles).VisibleRows.Count == 1, "the loaded row");
        await Type("i");
        await WaitForOperationsToFinishAsync();
        NAssert.That(InstalledVersion("Winget", "Proseware.Archiver"), Is.EqualTo("24.08"));
    }

    [Test]
    public async Task Bundles_CreatePowerShellScript_AndRemoveFromBundle()
    {
        await ResetAsync(TuiPageIds.Installed);
        await EnterPage();
        await FilterPackagesAsync("Contoso Editor");
        await Type("b");
        await FilterPackagesAsync("Woodgrove");
        await Type("b");
        await WaitUntilAsync(() => PackageBundlesLoader.Instance.Packages.Count == 2, "two packages in the bundle");

        await OnUi(() => Window.NavigateTo(TuiPageIds.Bundles));
        await EnterPage();
        await Type("m");
        await ChooseAsync("Create .ps1 script");
        await WaitForDialogAsync("Create .ps1 script");
        string script = Path.Join(FakeDataSetUp.Sandbox, "install.ps1");
        await ReplaceTextAsync(script);
        await Key(K.Enter);
        await WaitUntilAsync(() => File.Exists(script), "the script file");
        string text = File.ReadAllText(script);
        NAssert.That(text, Does.Contain("winget.exe install --id Contoso.Editor"));
        NAssert.That(text, Does.Contain("Woodgrove Vault from WinGet"));

        await EnterPage();
        await WaitUntilAsync(() => Window.GetPage<PackageListPage>(TuiPageIds.Bundles).VisibleRows.Count == 2, "two rows");
        await Key(K.Delete);
        await WaitUntilAsync(() => PackageBundlesLoader.Instance.Packages.Count == 1, "one package left");
    }

    [Test]
    public async Task Operations_CancelASlowOperation_ThenRetryIt()
    {
        await ResetAsync(TuiPageIds.Updates);
        await EnterPage();
        await FilterPackagesAsync("Tailspin");
        await Type("u");
        await WaitUntilAsync(() => TuiOperationRegistry.ActiveCount == 1, "the slow update to start");

        await OnUi(() => Window.NavigateTo(TuiPageIds.Operations));
        await WaitForTextAsync("Tailspin Slow Sync");
        await EnterPage();
        await Type("c");
        await WaitForDialogAsync("Cancel");
        await Key(K.Enter);
        await WaitForOperationsToFinishAsync();
        await WaitForTextAsync("Canceled");
        NAssert.That(InstalledVersion("Winget", "Tailspin.SlowSync"), Is.EqualTo("3.2.0"));

        await Type("r");
        await WaitForOperationsToFinishAsync();
        await WaitUntilAsync(() => InstalledVersion("Winget", "Tailspin.SlowSync") == "3.3.0", "the retried update", TimeSpan.FromSeconds(40));
    }

    [Test]
    public async Task Operations_MenuShowsOutput_AndClearFinished()
    {
        await OnUi(() => Settings.Set(Settings.K.MaintainSuccessfulInstalls, true));
        try
        {
            await ResetAsync(TuiPageIds.Updates);
            await EnterPage();
            await FilterPackagesAsync("fabrikam-grep");
            await Type("u");
            await WaitForOperationsToFinishAsync();

            await OnUi(() => Window.NavigateTo(TuiPageIds.Operations));
            await WaitForTextAsync("Succeeded", "Successfully updated");
            await EnterPage();
            await Key(K.Enter);
            await WaitForDialogAsync("fabrikam-grep");
            await WaitForTextAsync("fake update requested", "Successfully updated");
            await Key(K.Escape);
            await WaitForNoDialogAsync();

            await Type("x");
            await WaitUntilAsync(() => TuiOperationRegistry.Snapshot().Count == 0, "finished operations to be cleared");
            await WaitForTextAsync("No operations are running");
        }
        finally
        {
            await OnUi(() => Settings.Set(Settings.K.MaintainSuccessfulInstalls, false));
        }
    }

    [Test]
    public async Task Operations_ParallelLimitQueuesTheRest()
    {
        int previous = AbstractOperation.MAX_OPERATIONS;
        AbstractOperation.MAX_OPERATIONS = 1;
        try
        {
            await ResetAsync(TuiPageIds.Updates);
            await EnterPage();
            await FilterPackagesAsync("Northwind");
            await WaitForTextAsync("2 of 13 shown");
            await Key(K.Enter, RawModifiers.Control);
            await WaitUntilAsync(() => TuiOperationRegistry.Snapshot().Any(o => o.Status is OperationStatus.InQueue), "a queued operation");
            await WaitForOperationsToFinishAsync();
            NAssert.That(InstalledVersion("Winget", "Northwind.Terminal"), Is.EqualTo("1.20.1"));
            NAssert.That(InstalledVersion("Npm", "northwind-lint"), Is.EqualTo("9.14.0"));
        }
        finally
        {
            AbstractOperation.MAX_OPERATIONS = previous;
        }
    }

    [Test]
    public async Task Managers_DisableAManager_HidesItsPackages()
    {
        await ResetAsync(TuiPageIds.Managers);
        await WaitForTextAsync("Package managers", "Enable Scoop", "Scoop is enabled and ready to go", "Cargo was not found");
        await EnterPage();
        await FocusFieldAsync("Enable Scoop");
        await Key(K.Space);
        await WaitUntilAsync(() => InstalledPackagesLoader.Instance.Packages.All(p => p.Manager.Name != "Scoop") && !InstalledPackagesLoader.Instance.IsLoading,
            "Scoop packages to disappear");
        await WaitForTextAsync("Disabled");
        await Key(K.Space);
        await WaitUntilAsync(() => InstalledPackagesLoader.Instance.Packages.Any(p => p.Manager.Name == "Scoop"), "Scoop packages to come back");
    }

    [Test]
    public async Task Managers_ScoopCleanup_RunsAsATrackedOperation()
    {
        await ResetAsync(TuiPageIds.Managers);
        await OpenScoopSettingsAsync();
        await FocusFieldAsync("Run cleanup and clear cache");
        await Key(K.Enter);
        await WaitForDialogAsync("Run cleanup and clear cache");
        await PressButtonAsync("Continue");

        var op = await WaitForScoopOperationAsync("Run cleanup and clear cache");
        await WaitForSuccessAsync(op);
        NAssert.That(await OnUi(() => op.GetOutput().Select(l => l.Item1).ToList()), Does.Contain("Cleaning Scoop cache..."));
        NAssert.That(Window.CurrentPageId, Is.EqualTo(TuiPageIds.Operations));
    }

    [Test]
    public async Task Managers_ScoopUninstall_NeedsBothConfirmations()
    {
        await ResetAsync(TuiPageIds.Managers);
        await OpenScoopSettingsAsync();

        // Cancelling the second, "ALL YOUR SCOOP PACKAGES…" warning starts nothing.
        await FocusFieldAsync("Uninstall Scoop (and its packages)");
        await Key(K.Enter);
        await WaitForDialogAsync("Uninstall Scoop");
        await PressButtonAsync("Continue");
        await WaitForTextAsync("ALL YOUR SCOOP PACKAGES WILL BE PERMANENTLY DELETED.");
        await PressButtonAsync("Cancel");
        await WaitUntilAsync(() => TuiModal.Top?.Title.StartsWith("Scoop settings", StringComparison.Ordinal) == true, "back to Scoop settings");
        NAssert.That(await OnUi(() => TuiOperationRegistry.Snapshot().Count), Is.Zero);

        await FocusFieldAsync("Uninstall Scoop (and its packages)");
        await Key(K.Enter);
        await WaitForDialogAsync("Uninstall Scoop");
        await PressButtonAsync("Continue");
        await WaitForTextAsync("ALL YOUR SCOOP PACKAGES WILL BE PERMANENTLY DELETED.");
        await PressButtonAsync("Uninstall Scoop");
        var op = await WaitForScoopOperationAsync("Uninstall Scoop");
        await WaitForSuccessAsync(op);
    }

    private static async Task WaitForSuccessAsync(UniGetUI.PackageOperations.AbstractOperation op)
    {
        await WaitUntilAsync(() => op.Status is UniGetUI.PackageEngine.Enums.OperationStatus.Succeeded or UniGetUI.PackageEngine.Enums.OperationStatus.Failed,
            "the operation to finish");
        NAssert.That(op.Status, Is.EqualTo(UniGetUI.PackageEngine.Enums.OperationStatus.Succeeded),
            string.Join("\n", await OnUi(() => op.GetOutput().Select(l => l.Item1).ToList())));
    }

    private static async Task OpenScoopSettingsAsync()
    {
        await WaitForTextAsync("Package managers", "Scoop is enabled and ready to go");
        await EnterPage();
        await FocusFieldAsync("Scoop settings");
        await Key(K.Enter);
        await WaitForDialogAsync("Scoop settings");
    }

    private static async Task<UniGetUI.PackageOperations.AbstractOperation> WaitForScoopOperationAsync(string title)
    {
        await WaitUntilAsync(() => TuiOperationRegistry.Snapshot().Any(o => o.Metadata.Title == title), $"the \"{title}\" operation");
        return await OnUi(() => TuiOperationRegistry.Snapshot().First(o => o.Metadata.Title == title));
    }

    [Test]
    public async Task Managers_AddAndRemoveASource()
    {
        await ResetAsync(TuiPageIds.Managers);
        await EnterPage();
        await FocusFieldAsync("WinGet settings");
        await Key(K.Enter);
        await WaitForDialogAsync("WinGet settings");
        await WaitForTextAsync("Manage sources", "msstore");
        await FocusFieldAsync("Add source");
        await Key(K.Enter);
        await ChooseAsync("Other");
        await WaitForDialogAsync("Add source");
        await Type("contoso-private");
        await Key(K.Enter);
        await WaitForDialogAsync("Add source");
        await ReplaceTextAsync("https://fake.unigetui.invalid/private");
        await Key(K.Enter);
        await WaitForOperationsToFinishAsync();
        await WaitUntilAsync(() => State.Sources.Any(s => s.Name == "contoso-private"), "the new source");
        await WaitForTextAsync("Remove  contoso-private");

        await FocusFieldAsync("Remove  contoso-private", K.Up);
        await Key(K.Enter);
        await WaitForDialogAsync("Remove");
        await Key(K.Enter);
        await WaitForOperationsToFinishAsync();
        await WaitUntilAsync(() => State.Sources.All(s => s.Name != "contoso-private"), "the source to be removed");
        await Key(K.Escape);
    }

    [Test]
    public async Task Managers_DefaultInstallOptions_ApplyToNewInstalls()
    {
        await ResetAsync(TuiPageIds.Managers);
        await EnterPage();
        await FocusFieldAsync("Scoop settings");
        await Key(K.Enter);
        await WaitForDialogAsync("Scoop settings");
        await FocusFieldAsync("Change default options");
        await Key(K.Enter);
        await WaitForDialogAsync("Default installation options for Scoop");
        await FocusFieldAsync("Installation scope");
        await Key(K.Right);
        NAssert.That(await FocusedTextAsync(), Does.Contain("User | Local"));
        await PressButtonAsync("Save");
        await WaitForDialogAsync("Scoop settings");
        await Key(K.Escape);
        await WaitForNoDialogAsync();

        await OnUi(() => Window.NavigateTo(TuiPageIds.Discover));
        await EnterPage();
        await Type("litware-fzf");
        await Key(K.Enter);
        await WaitForTextAsync("litware-fzf");
        await Key(K.Down);
        await Type("i");
        await WaitForOperationsToFinishAsync();
        NAssert.That(State.Installed.Single(p => p.Id == "litware-fzf").Scope, Is.EqualTo("user"));
        await UniGetUI.PackageEngine.PackageClasses.InstallOptionsFactory.SaveForManagerAsync(new UniGetUI.PackageEngine.Serializable.InstallOptions(), TuiEngine.FindManager("Scoop")!);
    }

    [Test]
    public async Task Settings_ToggleAnInvertedSetting_AndSwitchSections()
    {
        await ResetAsync(TuiPageIds.Settings);
        await WaitForTextAsync("Settings", "General preferences");
        await EnterPage();
        await Key(K.Right);
        await WaitForTextAsync("User interface preferences", "Select upgradable packages by default");
        await FocusFieldAsync("Select upgradable packages by default");
        NAssert.That(Settings.Get(Settings.K.DisableSelectingUpdatesByDefault), Is.False);
        await Key(K.Space);
        await WaitUntilAsync(() => Settings.Get(Settings.K.DisableSelectingUpdatesByDefault), "the inverted setting to be stored");
        await Key(K.Space);
        await WaitUntilAsync(() => !Settings.Get(Settings.K.DisableSelectingUpdatesByDefault), "the setting to be restored");

        await Key(K.PageDown);
        await WaitForTextAsync("Notification preferences", "Enable UniGetUI notifications");
        await Key(K.PageDown);
        await Key(K.PageDown);
        await WaitForTextAsync("Package operation preferences", "Choose how many operations should be performed in parallel");
        await FocusFieldAsync("Choose how many operations", K.Up);
        await Key(K.Right);
        await WaitUntilAsync(() => Settings.GetValue(Settings.K.ParallelOperationCount) == "2" && AbstractOperation.MAX_OPERATIONS == 2, "parallel operations = 2");
        await Key(K.Left);
        await WaitUntilAsync(() => Settings.GetValue(Settings.K.ParallelOperationCount) == "1", "parallel operations = 1");
    }

    [Test]
    public async Task Settings_SecureSetting_UnlocksCustomArguments()
    {
        await ResetAsync(TuiPageIds.Settings);
        await EnterPage();
        await OnUi(() => Window.GetPage<SettingsPage>(TuiPageIds.Settings).ShowSection(SettingsPage.Section.Administrator));
        await EnterPage();
        await FocusFieldAsync("Allow custom command-line arguments");
        await Key(K.Space);
        await WaitUntilAsync(() => UniGetUI.Core.SettingsEngine.SecureSettings.SecureSettings.Get(UniGetUI.Core.SettingsEngine.SecureSettings.SecureSettings.K.AllowCLIArguments),
            "the secure setting");
        NAssert.That(Directory.GetFiles(Path.Join(FakeDataSetUp.Sandbox, "SecureSettings"), "*", SearchOption.AllDirectories), Is.Not.Empty,
            "secure settings must be written inside the sandbox");
        await Key(K.Space);
        await WaitUntilAsync(() => !UniGetUI.Core.SettingsEngine.SecureSettings.SecureSettings.Get(UniGetUI.Core.SettingsEngine.SecureSettings.SecureSettings.K.AllowCLIArguments),
            "the secure setting to be off");
    }

    [Test]
    public async Task Settings_Scheduler_RunLocalBackupNow()
    {
        string backups = Path.Join(FakeDataSetUp.Sandbox, "backups");
        await OnUi(() => Settings.SetValue(Settings.K.ChangeBackupOutputDirectory, backups));
        try
        {
            await ResetAsync(TuiPageIds.Settings);
            await OnUi(() => Window.GetPage<SettingsPage>(TuiPageIds.Settings).ShowSection(SettingsPage.Section.Backup));
            await WaitForTextAsync("Backup and Restore", "Perform a local backup now");
            await EnterPage();
            await FocusFieldAsync("Perform a local backup now");
            await Key(K.Enter);
            await WaitUntilAsync(() => Directory.Exists(backups) && Directory.GetFiles(backups).Length == 1, "the backup file");
            NAssert.That(File.ReadAllText(Directory.GetFiles(backups)[0]), Does.Contain("Woodgrove.Vault"));

            await OnUi(() => Window.GetPage<SettingsPage>(TuiPageIds.Settings).ShowSection(SettingsPage.Section.Scheduler));
            await WaitForTextAsync("Check for package updates", "Install available updates", "Back up installed packages locally");
            await FocusFieldAsync("Enabled");
            await Key(K.Space);
            await WaitUntilAsync(() => MaintenanceScheduleStore.Get(MaintenanceTaskKind.CheckForUpdates).Enabled, "the schedule to be enabled");
            await Key(K.Space);
        }
        finally
        {
            await OnUi(() => Settings.SetValue(Settings.K.ChangeBackupOutputDirectory, ""));
        }
    }

    [Test]
    public async Task Settings_SearchJumpsToTheMatchingSetting()
    {
        await ResetAsync(TuiPageIds.Settings);
        await EnterPage();
        await Key(K.F, RawModifiers.Control);
        await WaitForDialogAsync("Search settings");
        await Type("proxy");
        await Key(K.Enter);
        await ChooseAsync("Connect the internet using a custom proxy");
        await WaitUntilAsync(() => Window.GetPage<SettingsPage>(TuiPageIds.Settings).CurrentSection == SettingsPage.Section.Internet, "the Internet section");
        await WaitUntilAsync(() => Avalonia.Controls.TopLevel.GetTopLevel(Window)?.FocusManager?.GetFocusedElement() is Avalonia.Controls.ContentControl c
                                   && c.Content?.ToString()?.Contains("custom proxy", StringComparison.Ordinal) == true, "focus on the proxy setting");
    }

    [Test]
    public async Task CloudBackup_SignInBackupAndRestore_WithTheFakeCloud()
    {
        await ResetAsync(TuiPageIds.Settings);
        await OnUi(() => Window.GetPage<SettingsPage>(TuiPageIds.Settings).ShowSection(SettingsPage.Section.Backup));
        await EnterPage();
        await FocusFieldAsync("Log in with GitHub");
        await Key(K.Enter);
        await WaitUntilAsync(() => TuiCloudBackup.IsSignedIn, "the fake sign-in");
        await WaitForTextAsync("You are logged in as fake-user");

        await FocusFieldAsync("Perform a cloud backup now");
        await Key(K.Enter);
        await WaitUntilAsync(() => Directory.Exists(Path.Join(FakeDataSetUp.Sandbox, "FakeCloud"))
                                   && Directory.GetFiles(Path.Join(FakeDataSetUp.Sandbox, "FakeCloud"), "*.ubundle").Length == 1,
            "the fake cloud backup");

        await FocusFieldAsync("Restore a backup from the cloud");
        await Key(K.Enter);
        await ChooseAsync(Environment.MachineName);
        await WaitUntilAsync(() => PackageBundlesLoader.Instance.Packages.Count == 16, "the backup to load into the bundle");
        await WaitForTextAsync("Package Bundles", "Woodgrove Vault");

        await OnUi(TuiCloudBackup.SignOut);
    }

    [Test]
    public async Task DownloadSizeColumn_ShowsSizesOnceDetailsLoad()
    {
        await OnUi(() => Settings.Set(Settings.K.ShowDownloadSizeColumn, true));
        try
        {
            await ResetAsync(TuiPageIds.Updates);
            await WaitForTextAsync("Download size", " MB");
        }
        finally
        {
            await OnUi(() => Settings.Set(Settings.K.ShowDownloadSizeColumn, false));
        }
    }

    [Test]
    public async Task Logs_ShowsTheAppLog_AndManagerLogs()
    {
        await ResetAsync(TuiPageIds.Logs);
        await WaitForTextAsync("Logs", "UniGetUI Log", "lines");
        await EnterPage();
        await Type("l");
        await ChooseAsync("WinGet");
        await WaitForTextAsync("WinGet  ·", "RefreshIndexes");
        await Type("v");
    }

    [Test]
    public async Task History_ShowsFinishedOperations_AndRevertsAnUpdate()
    {
        await ResetAsync(TuiPageIds.Updates);
        await EnterPage();
        await FilterPackagesAsync("Contoso Editor");
        await Type("u");
        await WaitForOperationsToFinishAsync();
        NAssert.That(InstalledVersion("Winget", "Contoso.Editor"), Is.EqualTo("4.3.1"));
        await WaitUntilAsync(() => OperationHistoryStore.GetAll().Count == 1, "the history record");

        await OnUi(() => Window.NavigateTo(TuiPageIds.History));
        await WaitForTextAsync("Operation history", "Contoso Editor", "succeeded", "4.1.0 -> 4.3.1");
        await EnterPage();
        await Key(K.Enter);
        await WaitForDialogAsync("Operation log");
        await WaitForTextAsync("Operation: update-package", "Status: succeeded", "4.1.0 -> 4.3.1");
        await Key(K.Escape);
        await WaitForNoDialogAsync();

        await Type("z");
        await WaitForDialogAsync("Revert");
        await WaitForTextAsync("downgrade");
        await Key(K.Enter);
        await WaitForOperationsToFinishAsync();
        await WaitUntilAsync(() => InstalledVersion("Winget", "Contoso.Editor") == "4.1.0", "the downgrade");
    }

    [Test]
    public async Task Help_AndGlobalShortcuts()
    {
        await ResetAsync(TuiPageIds.Discover);
        await Key(K.F1);
        await WaitForDialogAsync("Help");
        await WaitForTextAsync("Keyboard shortcuts", "Alt+1 … Alt+9");
        // The command-line reference is at the bottom: PgDn scrolls the dialog down to it.
        for (int i = 0; i < 6 && !(await ScreenAsync()).Contains("--fake-data", StringComparison.Ordinal); i++)
            await Key(K.PageDown);
        await WaitForTextAsync("--fake-data");
        await Key(K.Escape);
        await WaitForNoDialogAsync();
        NAssert.That(Window.CurrentPageId, Is.EqualTo(TuiPageIds.Discover));

        await Key(K.D3, RawModifiers.Control);
        await WaitForTextAsync("Installed Packages");
        await Key(K.Tab, RawModifiers.Control);
        await WaitForTextAsync("Package Bundles");
        await Key(K.D7, RawModifiers.Control);
        await WaitForTextAsync("General preferences");
        await Key(K.D5, RawModifiers.Alt);
        await WaitForTextAsync("No operations are running");
    }

    [Test]
    public async Task MenuBar_RunsThePageActions()
    {
        await ResetAsync(TuiPageIds.Installed);
        await EnterPage();
        await FilterPackagesAsync("Woodgrove");
        await Key(K.F10);
        await WaitForTextAsync("Uninstall selection", "Package details", "Export to CSV");
        for (int i = 0; i < 30 && await OnUi(() => Window.SelectedMenuActionLabel) != "Package details"; i++)
            await Key(K.Down);
        NAssert.That(await OnUi(() => Window.SelectedMenuActionLabel), Is.EqualTo("Package details"));
        await Key(K.Enter);
        await WaitUntilAsync(() => TuiModal.IsOpen, "an action to open a dialog");
        await Key(K.Escape);
    }

    [Test]
    public async Task F3_OpensSearchOptions_FromTheListAndTheFilterBox()
    {
        await ResetAsync(TuiPageIds.Updates);
        await EnterPage();
        await Key(K.F3);
        await WaitForDialogAsync("Search options");
        await Key(K.Escape);
        await WaitForNoDialogAsync();

        await Key(K.F, RawModifiers.Control);
        await WaitUntilAsync(() => Avalonia.Controls.TopLevel.GetTopLevel(Window)?.FocusManager?.GetFocusedElement() is Avalonia.Controls.TextBox,
            "the filter box");
        await Key(K.F3);
        await WaitForDialogAsync("Search options");
        await Key(K.Escape);
        await WaitForNoDialogAsync();
    }

    [Test]
    public async Task MenuBar_AltLetterOpensThePageMenu()
    {
        await ResetAsync(TuiPageIds.Installed);
        await EnterPage();
        await FilterPackagesAsync("Woodgrove");
        await Key(K.P, RawModifiers.Alt);
        await WaitForTextAsync("Uninstall selection", "Package details");
        await RunMenuItemAsync("Package details");
        await WaitForTextAsync("Package details: Woodgrove");
        await Key(K.Escape);
        await WaitUntilAsync(() => !TuiModal.IsOpen, "the details dialog to close");
    }
}
