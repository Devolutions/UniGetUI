using System.Diagnostics;
using Avalonia.Controls;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Core.Tools.Scheduling;
using UniGetUI.Interface.Enums;
using UniGetUI.Interface.Telemetry;
using UniGetUI.PackageEngine.Classes.Manager.Classes;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Avalonia.Views.Pages;

public class SoftwareUpdatesPage : AbstractPackagesPage
{
    // Context-menu items whose enabled state depends on the focused package
    private MenuItem? _menuAsAdmin;
    private MenuItem? _menuInteractive;
    private MenuItem? _menuSkipHash;
    private MenuItem? _menuDownloadInstaller;
    private MenuItem? _menuOpenInstallLocation;
    private MenuItem? _menuAutoUpdate;

    public SoftwareUpdatesPage() : base(new PackagesPageData
    {
        PageName = "SoftwarePages.SoftwareUpdatesPage",
        PageTitle = CoreTools.Translate("Software Updates"),
        IconName = "update",
        PageRole = OperationType.Update,
        Loader = UpgradablePackagesLoader.Instance ?? new UpgradablePackagesLoader([]),
        MegaQueryBlockEnabled = false,
        DisableSuggestedResultsRadio = true,
        PackagesAreCheckedByDefault = true,
        ShowLastLoadTime = true,
        DisableAutomaticPackageLoadOnStart = false,
        DisableFilterOnQueryChange = false,
        DisableReload = false,
        NoPackages_BackgroundText = CoreTools.Translate("Hooray! No updates were found."),
        NoPackages_ImagePath = "avares://UniGetUI/Assets/Images/Trophee.png",
        NoPackages_SourcesText = CoreTools.Translate("Everything is up to date"),
        NoPackages_SubtitleText_Base = CoreTools.Translate("Everything is up to date"),
        MainSubtitle_StillLoading = CoreTools.Translate("Loading packages"),
        NoMatches_BackgroundText = CoreTools.Translate("No results were found matching the input criteria"),
    })
    {
        ViewModel.PackagesLoaded += reason => { _ = WhenPackagesLoaded(); };
    }

    protected override void GenerateToolBar(PackagesPageViewModel vm)
    {
        // ── Dropdown: update variants ───────────────────────────────────────
        var updateAsAdmin = new MenuItem { Header = CoreTools.Translate("Update as administrator"), IsVisible = OperatingSystem.IsWindows() };
        var updateSkipHash = new MenuItem { Header = CoreTools.Translate("Skip integrity checks") };
        var updateInteractive = new MenuItem { Header = CoreTools.Translate("Interactive update") };
        var downloadInstallers = new MenuItem { Header = CoreTools.Translate("Download selected installers") };
        var uninstallSelected = new MenuItem { Header = CoreTools.Translate("Uninstall selected packages") };

        SetMainButton("update", CoreTools.Translate("Update selection"), () =>
            _ = LaunchUpdate(vm.FilteredPackages.GetCheckedPackages()));

        SetMainButtonDropdown(new MenuFlyout
        {
            Items =
            {
                updateAsAdmin, updateSkipHash, updateInteractive,
                new Separator(),
                downloadInstallers,
                new Separator(),
                uninstallSelected,
            },
        });

        updateAsAdmin.Click += (_, _) => _ = LaunchUpdate(vm.FilteredPackages.GetCheckedPackages(), elevated: true);
        updateSkipHash.Click += (_, _) => _ = LaunchUpdate(vm.FilteredPackages.GetCheckedPackages(), no_integrity: true);
        updateInteractive.Click += (_, _) => _ = LaunchUpdate(vm.FilteredPackages.GetCheckedPackages(), interactive: true);
        downloadInstallers.Click += (_, _) => _ = AvaloniaPackageOperationHelper.DownloadSelectedAsync(
            vm.FilteredPackages.GetCheckedPackages(), TEL_InstallReferral.ALREADY_INSTALLED);
        uninstallSelected.Click += (_, _) => _ = LaunchUninstallFromUpdates(vm.FilteredPackages.GetCheckedPackages());

        // ── Toolbar buttons ─────────────────────────────────────────────────
        ViewModel.AddToolbarSeparator();
        ViewModel.AddToolbarButton("options", CoreTools.Translate("Update options"),
            () => _ = ShowInstallationOptionsForPackage(SelectedItem), showLabel: false);
        ViewModel.AddToolbarSeparator();
        ViewModel.AddToolbarButton("console", CoreTools.Translate("Manual update"),
            () => _ = ManualInstallHelper.LaunchManualAsync(SelectedItem, OperationType.Update));
        ViewModel.AddToolbarSeparator();
        ViewModel.AddToolbarButton("info_round", CoreTools.Translate("Package details"),
            () => _ = ShowDetailsForPackage(SelectedItem), showLabel: false);
        ViewModel.AddToolbarSeparator();
        ViewModel.AddToolbarButton("pin", CoreTools.Translate("Ignore selected packages"), async () =>
        {
            foreach (var pkg in vm.FilteredPackages.GetCheckedPackages())
            {
                await pkg.AddToIgnoredUpdatesAsync();
                UpgradablePackagesLoader.Instance.Remove(pkg);
                UpgradablePackagesLoader.Instance.IgnoredPackages[pkg.Id] = pkg;
            }
        });
        ViewModel.AddToolbarButton("clipboard_list", CoreTools.Translate("Manage ignored updates"),
            () => vm.RequestManageIgnoredCommand.Execute(null));
        ViewModel.AddToolbarSeparator();
        ViewModel.AddToolbarButton("sandclock", CoreTools.Translate("Automatically update selected packages"),
            () => MarkForAutoUpdates(vm.FilteredPackages.GetCheckedPackages()));
        ViewModel.AddToolbarButton("clipboard_list", CoreTools.Translate("Manage automatic updates"),
            () => vm.RequestManageAutoUpdatesCommand.Execute(null));
        ViewModel.AddToolbarSeparator();
        ViewModel.AddToolbarButton("save_as", CoreTools.Translate("Export to CSV"),
            () => _ = ExportPackagesToCsvAsync());
    }

    // ─── Context menu ─────────────────────────────────────────────────────────
    protected override ContextMenu? GenerateContextMenu()
    {
        var menuUpdate = new MenuItem
        {
            Header = ShortcutHeader(CoreTools.Translate("Update"), MainActionShortcut),
            Icon = LoadMenuIcon("update"),
        };
        menuUpdate.Click += (_, _) => _ = LaunchUpdate([SelectedItem!]);

        var menuUpdateOptions = new MenuItem
        {
            Header = ShortcutHeader(CoreTools.Translate("Update options"), OptionsShortcut),
            Icon = LoadMenuIcon("options"),
        };
        menuUpdateOptions.Click += (_, _) => _ = ShowInstallationOptionsForPackage(SelectedItem);

        var menuManual = new MenuItem { Header = CoreTools.Translate("Manual update"), Icon = LoadMenuIcon("console") };
        menuManual.Click += (_, _) => _ = ManualInstallHelper.LaunchManualAsync(SelectedItem, OperationType.Update);

        _menuOpenInstallLocation = new MenuItem
        {
            Header = CoreTools.Translate("Open install location"),
            Icon = LoadMenuIcon("launch"),
        };
        _menuOpenInstallLocation.Click += (_, _) => OpenInstallLocation(SelectedItem);

        _menuAsAdmin = new MenuItem
        {
            Header = CoreTools.Translate("Update as administrator"),
            Icon = LoadMenuIcon("uac"),
            IsVisible = OperatingSystem.IsWindows(),
        };
        _menuAsAdmin.Click += (_, _) => _ = LaunchUpdate([SelectedItem!], elevated: true);

        _menuInteractive = new MenuItem
        {
            Header = CoreTools.Translate("Interactive update"),
            Icon = LoadMenuIcon("interactive"),
        };
        _menuInteractive.Click += (_, _) => _ = LaunchUpdate([SelectedItem!], interactive: true);

        _menuSkipHash = new MenuItem
        {
            Header = CoreTools.Translate("Skip hash check"),
            Icon = LoadMenuIcon("checksum"),
        };
        _menuSkipHash.Click += (_, _) => _ = LaunchUpdate([SelectedItem!], no_integrity: true);

        _menuDownloadInstaller = new MenuItem
        {
            Header = CoreTools.Translate("Download installer"),
            Icon = LoadMenuIcon("download"),
        };
        _menuDownloadInstaller.Click += (_, _) => _ = AvaloniaPackageOperationHelper.AskLocationAndDownloadAsync(
            SelectedItem, TEL_InstallReferral.ALREADY_INSTALLED);

        var menuUninstallThenUpdate = new MenuItem
        {
            Header = CoreTools.Translate("Uninstall package, then update it"),
            Icon = LoadMenuIcon("undelete"),
        };
        menuUninstallThenUpdate.Click += (_, _) => _ = LaunchUninstallThenUpdate(SelectedItem);

        var menuUninstall = new MenuItem
        {
            Header = CoreTools.Translate("Uninstall package"),
            Icon = LoadMenuIcon("delete"),
        };
        menuUninstall.Click += (_, _) => _ = LaunchUninstallFromUpdates([SelectedItem!]);

        var menuIgnore = new MenuItem
        {
            Header = CoreTools.Translate("Ignore updates for this package"),
            Icon = LoadMenuIcon("pin"),
        };
        menuIgnore.Click += (_, _) =>
        {
            var pkg = SelectedItem;
            if (pkg is null) return;
            _ = pkg.AddToIgnoredUpdatesAsync();
            UpgradablePackagesLoader.Instance.Remove(pkg);
            UpgradablePackagesLoader.Instance.IgnoredPackages[pkg.Id] = pkg;
        };

        _menuAutoUpdate = new MenuItem
        {
            Header = CoreTools.Translate("Update this package automatically"),
            Icon = LoadMenuIcon("sandclock"),
            ToggleType = MenuItemToggleType.CheckBox,
        };
        _menuAutoUpdate.Click += (_, _) =>
        {
            var pkg = SelectedItem;
            if (pkg is null) return;
            string id = AutoUpdatesDatabase.GetIdForPackage(pkg);
            if (AutoUpdatesDatabase.IsAutoUpdated(id))
                AutoUpdatesDatabase.Remove(id);
            else
                MarkForAutoUpdates([pkg]);
        };

        var menuSkipVersion = new MenuItem
        {
            Header = CoreTools.Translate("Skip this version"),
            Icon = LoadMenuIcon("skip"),
        };
        menuSkipVersion.Click += (_, _) =>
        {
            var pkg = SelectedItem;
            if (pkg is null) return;
            _ = pkg.AddToIgnoredUpdatesAsync(pkg.NewVersionString);
            UpgradablePackagesLoader.Instance.Remove(pkg);
            UpgradablePackagesLoader.Instance.IgnoredPackages[pkg.Id] = pkg;
        };

        // ── Pause updates submenu ──────────────────────────────────────────
        var menuPause = new MenuItem
        {
            Header = CoreTools.Translate("Pause updates for"),
            Icon = LoadMenuIcon("sandclock"),
        };
        foreach (var pauseTime in new[]
        {
            new IgnoredUpdatesDatabase.PauseTime { Days  = 1  },
            new IgnoredUpdatesDatabase.PauseTime { Days  = 3  },
            new IgnoredUpdatesDatabase.PauseTime { Weeks = 1  },
            new IgnoredUpdatesDatabase.PauseTime { Weeks = 2  },
            new IgnoredUpdatesDatabase.PauseTime { Weeks = 4  },
            new IgnoredUpdatesDatabase.PauseTime { Months = 3 },
            new IgnoredUpdatesDatabase.PauseTime { Months = 6 },
            new IgnoredUpdatesDatabase.PauseTime { Months = 12},
        })
        {
            var t = pauseTime;
            var item = new MenuItem { Header = t.StringRepresentation() };
            item.Click += (_, _) =>
            {
                var pkg = SelectedItem;
                if (pkg is null) return;
                _ = pkg.AddToIgnoredUpdatesAsync("<" + t.GetDateFromNow());
                UpgradablePackagesLoader.Instance.IgnoredPackages[pkg.Id] = pkg;
                UpgradablePackagesLoader.Instance.Remove(pkg);
            };
            menuPause.Items.Add(item);
        }

        var menuDetails = new MenuItem
        {
            Header = ShortcutHeader(CoreTools.Translate("Package details"), DetailsShortcut),
            Icon = LoadMenuIcon("info_round"),
        };
        menuDetails.Click += (_, _) => _ = ShowDetailsForPackage(SelectedItem);

        var menu = new ContextMenu();
        menu.Items.Add(menuUpdate);
        menu.Items.Add(new Separator());
        menu.Items.Add(menuUpdateOptions);
        menu.Items.Add(menuManual);
        menu.Items.Add(_menuOpenInstallLocation);
        menu.Items.Add(new Separator());
        menu.Items.Add(_menuAsAdmin);
        menu.Items.Add(_menuInteractive);
        menu.Items.Add(_menuSkipHash);
        menu.Items.Add(_menuDownloadInstaller);
        menu.Items.Add(new Separator());
        menu.Items.Add(menuUninstallThenUpdate);
        menu.Items.Add(menuUninstall);
        menu.Items.Add(new Separator());
        menu.Items.Add(_menuAutoUpdate);
        menu.Items.Add(new Separator());
        menu.Items.Add(menuIgnore);
        menu.Items.Add(menuSkipVersion);
        menu.Items.Add(menuPause);
        menu.Items.Add(new Separator());
        menu.Items.Add(menuDetails);

        return menu;
    }

    protected override void WhenShowingContextMenu(IPackage package)
    {
        if (_menuAsAdmin is null || _menuInteractive is null || _menuSkipHash is null
            || _menuDownloadInstaller is null || _menuOpenInstallLocation is null
            || _menuAutoUpdate is null)
        {
            Logger.Warn("Context menu items are null on SoftwareUpdatesPage");
            return;
        }

        var caps = package.Manager.Capabilities;
        _menuAsAdmin.IsEnabled = caps.CanRunAsAdmin;
        _menuInteractive.IsEnabled = caps.CanRunInteractively;
        _menuSkipHash.IsEnabled = caps.CanSkipIntegrityChecks;
        _menuDownloadInstaller.IsEnabled = caps.CanDownloadInstaller;
        _menuOpenInstallLocation.IsEnabled =
            package.Manager.DetailsHelper.GetInstallLocation(package) is not null;
        _menuAutoUpdate.IsChecked = AutoUpdatesDatabase.IsAutoUpdated(package);
    }

    // ─── Abstract action overrides ────────────────────────────────────────────
    protected override void PerformMainPackageAction(IPackage? package)
    {
        if (package is null) return;
        _ = LaunchUpdate([package]);
    }

    protected override async Task ShowDetailsForPackage(IPackage? package)
    {
        if (package is null) return;
        if (GetMainWindow() is not { } win) return;

        var dialog = new PackageDetailsWindow(
            package, OperationType.Update, TEL_InstallReferral.ALREADY_INSTALLED);
        await dialog.ShowDialog(win);

        if (dialog.ShouldProceedWithOperation)
            await LaunchUpdate([package]);
    }

    protected override async Task ShowInstallationOptionsForPackage(IPackage? package)
    {
        if (package is null || package.Source.IsVirtualManager) return;
        var opts = await InstallOptionsFactory.LoadForPackageAsync(package);
        if (GetMainWindow() is not { } win) return;

        var dialog = new InstallOptionsWindow(package, OperationType.Update, opts);
        await dialog.ShowDialog(win);
        await InstallOptionsFactory.SaveForPackageAsync(opts, package);

        if (dialog.ShouldProceedWithOperation)
            await LaunchUpdate([package]);
    }

    // ─── Page-specific actions ────────────────────────────────────────────────

    private static void OpenInstallLocation(IPackage? package)
    {
        if (package is null) return;
        var path = package.Manager.DetailsHelper.GetInstallLocation(package);
        if (path is not null)
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    // ─── Operation launchers ──────────────────────────────────────────────────
    private static async Task LaunchUpdate(
        IEnumerable<IPackage> packages,
        bool? elevated = null,
        bool? interactive = null,
        bool? no_integrity = null)
    {
        foreach (var pkg in packages)
        {
            var opts = await InstallOptionsFactory.LoadApplicableAsync(
                pkg, elevated: elevated, interactive: interactive, no_integrity: no_integrity);
            if (PackageOperation.HasPendingOperation(pkg, OperationType.Update)) continue;
            var op = new UpdatePackageOperation(pkg, opts);
            op.OperationSucceeded += (_, _) => TelemetryHandler.UpdatePackage(pkg, TEL_OP_RESULT.SUCCESS);
            op.OperationFailed += (_, _) => TelemetryHandler.UpdatePackage(pkg, TEL_OP_RESULT.FAILED);
            AvaloniaOperationRegistry.Add(op);
            _ = op.MainThread();
        }
    }

    private static async Task LaunchUninstallFromUpdates(IEnumerable<IPackage> packages)
    {
        foreach (var pkg in packages)
        {
            var opts = await InstallOptionsFactory.LoadApplicableAsync(pkg);
            if (PackageOperation.HasPendingOperation(pkg, OperationType.Uninstall)) continue;
            var op = new UninstallPackageOperation(pkg, opts);
            op.OperationSucceeded += (_, _) => TelemetryHandler.UninstallPackage(pkg, TEL_OP_RESULT.SUCCESS);
            op.OperationFailed += (_, _) => TelemetryHandler.UninstallPackage(pkg, TEL_OP_RESULT.FAILED);
            AvaloniaOperationRegistry.Add(op);
            _ = op.MainThread();
        }
    }

    private static async Task LaunchUninstallThenUpdate(IPackage? package)
    {
        if (package is null || package.Source.IsVirtualManager) return;
        var uninstallOpts = await InstallOptionsFactory.LoadApplicableAsync(package);
        var updateOpts = await InstallOptionsFactory.LoadApplicableAsync(package);
        if (PackageOperation.HasPendingOperation(package, OperationType.Update)) return;
        var uninstallOp = new UninstallPackageOperation(package, uninstallOpts);
        uninstallOp.OperationSucceeded += (_, _) => TelemetryHandler.UninstallPackage(package, TEL_OP_RESULT.SUCCESS);
        uninstallOp.OperationFailed += (_, _) => TelemetryHandler.UninstallPackage(package, TEL_OP_RESULT.FAILED);
        // Once uninstalled the package is gone, so the second step must install the new version fresh; a plain update would fail with "no installed package found".
        var updateOp = new InstallPackageOperation(package, updateOpts, req: uninstallOp);
        updateOp.OperationSucceeded += (_, _) => TelemetryHandler.UpdatePackage(package, TEL_OP_RESULT.SUCCESS);
        updateOp.OperationFailed += (_, _) => TelemetryHandler.UpdatePackage(package, TEL_OP_RESULT.FAILED);
        AvaloniaOperationRegistry.Add(uninstallOp);
        AvaloniaOperationRegistry.Add(updateOp);
        // uninstallOp runs as updateOp's prerequisite; launching it directly too would execute it twice concurrently
        _ = updateOp.MainThread();
    }

    // ─── Auto-update on load ──────────────────────────────────────────────────

    private static async Task WhenPackagesLoaded()
    {
        try
        {
            bool shouldAutoInstall = MaintenanceScheduler.IsAutoInstallDue();

            var upgradable = UpgradablePackagesLoader.Instance.Packages
                .Where(p => p.Tag is not PackageTag.OnQueue and not PackageTag.BeingProcessed)
                .ToList();

            if (upgradable.Count == 0) return;

            if (Settings.Get(Settings.K.DisableAUPOnBattery) && PowerConditions.IsOnBattery())
            {
                Logger.Warn("Updates will not be installed automatically because the device is on battery.");
                ShowAvailableUpdatesNotification(upgradable);
            }
            else if (Settings.Get(Settings.K.DisableAUPOnBatterySaver) && PowerConditions.IsBatterySaverOn())
            {
                Logger.Warn("Updates will not be installed automatically because battery saver is enabled.");
                ShowAvailableUpdatesNotification(upgradable);
            }
            else if (Settings.Get(Settings.K.DisableAUPOnMeteredConnections) && PowerConditions.IsOnMeteredConnection())
            {
                Logger.Warn("Updates will not be installed automatically because the current internet connection is metered.");
                ShowAvailableUpdatesNotification(upgradable);
            }
            else if (shouldAutoInstall)
            {
                MaintenanceScheduler.MarkAutoInstallHandled();
                await LaunchScheduledUpdate(upgradable);
            }
            else if (Environment.GetCommandLineArgs().Contains("--updateapps"))
            {
                _ = AvaloniaPackageOperationHelper.UpdateAllAsync();
                ShowUpgradingPackagesNotification(upgradable);
                Logger.Warn("Automatic install of updates has been enabled via Command Line (user settings have been overriden)");
            }
            else
            {
                ShowAvailableUpdatesNotification(upgradable);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    private static void MarkForAutoUpdates(IEnumerable<IPackage> packages)
    {
        int marked = 0;
        foreach (var pkg in packages)
        {
            string id = AutoUpdatesDatabase.GetIdForPackage(pkg);
            if (AutoUpdatesDatabase.IsAutoUpdated(id)) continue;
            AutoUpdatesDatabase.Add(id);
            marked++;
        }

        if (marked is 0) return;

        var schedule = MaintenanceScheduleStore.Get(MaintenanceTaskKind.InstallUpdates);
        string message = !schedule.Enabled
            ? CoreTools.Translate("Turn on \"Install available updates\" in the scheduled maintenance settings for this to take effect.")
            : schedule.InstallTargets is ScheduleInstallTargets.AllPackages
                ? CoreTools.Translate("Every upgradable package is already installed automatically, so this changes nothing until the scheduled task is limited to marked packages.")
                : CoreTools.Translate("They will be updated when the scheduled maintenance task runs.");

        GetMainWindow()?.ShowBanner(
            CoreTools.Translate("{0} package(s) marked for automatic updates", marked),
            message,
            MainWindow.RuntimeNotificationLevel.Success);
    }

    private static async Task LaunchScheduledUpdate(IReadOnlyList<IPackage> upgradable)
    {
        bool markedOnly = MaintenanceScheduleStore.GetInstallTargets()
            is ScheduleInstallTargets.MarkedPackagesOnly;

        List<IPackage> targets = [];
        List<IPackage> skipped = [];
        foreach (var package in upgradable)
        {
            if (!markedOnly || AutoUpdatesDatabase.IsAutoUpdated(package))
                targets.Add(package);
            else
                skipped.Add(package);
        }

        if (targets.Count > 0)
        {
            await LaunchUpdate(targets);
            ShowUpgradingPackagesNotification(targets);
        }
        else
        {
            Logger.Info("No upgradable package is marked for automatic updates, nothing will be installed");
        }

        if (skipped.Count > 0)
            ShowAvailableUpdatesNotification(skipped);
    }

    private static void ShowAvailableUpdatesNotification(IReadOnlyList<IPackage> upgradable)
    {
        if (OperatingSystem.IsWindows())
            WindowsAppNotificationBridge.ShowUpdatesAvailableNotification(upgradable);
        else if (OperatingSystem.IsMacOS())
            MacOsNotificationBridge.ShowUpdatesAvailableNotification(upgradable);
    }

    private static void ShowUpgradingPackagesNotification(IReadOnlyList<IPackage> upgradable)
    {
        if (OperatingSystem.IsWindows())
            WindowsAppNotificationBridge.ShowUpgradingPackagesNotification(upgradable);
        else if (OperatingSystem.IsMacOS())
            MacOsNotificationBridge.ShowUpgradingPackagesNotification(upgradable);
    }

}
