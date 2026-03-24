using System.Diagnostics;
using Avalonia.Controls;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
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
        NoPackages_SourcesText = CoreTools.Translate("Everything is up to date"),
        NoPackages_SubtitleText_Base = CoreTools.Translate("Everything is up to date"),
        MainSubtitle_StillLoading = CoreTools.Translate("Loading packages"),
        NoMatches_BackgroundText = CoreTools.Translate("No results were found matching the input criteria"),
    })
    { }

    protected override void GenerateToolBar(PackagesPageViewModel vm)
    {
        // ── Dropdown: update variants ───────────────────────────────────────
        var updateAsAdmin = new MenuItem { Header = CoreTools.Translate("Update as administrator") };
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
        downloadInstallers.Click += (_, _) => { /* TODO: download-only operation not yet ported */ };
        uninstallSelected.Click += (_, _) => _ = LaunchUninstallFromUpdates(vm.FilteredPackages.GetCheckedPackages());

        // ── Toolbar buttons ─────────────────────────────────────────────────
        AddToolbarSeparator();
        AddToolbarButton("options", CoreTools.Translate("Update options"),
            () => _ = ShowInstallationOptionsForPackage(SelectedItem), showLabel: false);
        AddToolbarSeparator();
        AddToolbarButton("info_round", CoreTools.Translate("Package details"),
            () => _ = ShowDetailsForPackage(SelectedItem), showLabel: false);
        AddToolbarButton("share", CoreTools.Translate("Share"),
            () => _ = SharePackage(SelectedItem), showLabel: false);
        AddToolbarSeparator();
        AddToolbarButton("pin", CoreTools.Translate("Ignore selected packages"), async () =>
        {
            foreach (var pkg in vm.FilteredPackages.GetCheckedPackages())
            {
                await pkg.AddToIgnoredUpdatesAsync();
                UpgradablePackagesLoader.Instance.Remove(pkg);
                UpgradablePackagesLoader.Instance.IgnoredPackages[pkg.Id] = pkg;
            }
        });
        AddToolbarButton("clipboard_list", CoreTools.Translate("Manage ignored updates"),
            () => _ = ShowManageIgnoredAsync());
        AddToolbarSeparator();
        AddToolbarButton("help", CoreTools.Translate("Help"), OpenHelp);
    }

    // ─── Context menu ─────────────────────────────────────────────────────────
    protected override ContextMenu? GenerateContextMenu()
    {
        var menuUpdate = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Update"),
            Icon = LoadMenuIcon("update"),
        };
        menuUpdate.Click += (_, _) => _ = LaunchUpdate([SelectedItem!]);

        var menuUpdateOptions = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Update options"),
            Icon = LoadMenuIcon("options"),
        };
        menuUpdateOptions.Click += (_, _) => _ = ShowInstallationOptionsForPackage(SelectedItem);

        _menuOpenInstallLocation = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Open install location"),
            Icon = LoadMenuIcon("launch"),
        };
        _menuOpenInstallLocation.Click += (_, _) => OpenInstallLocation(SelectedItem);

        _menuAsAdmin = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Update as administrator"),
            Icon = LoadMenuIcon("uac"),
        };
        _menuAsAdmin.Click += (_, _) => _ = LaunchUpdate([SelectedItem!], elevated: true);

        _menuInteractive = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Interactive update"),
            Icon = LoadMenuIcon("interactive"),
        };
        _menuInteractive.Click += (_, _) => _ = LaunchUpdate([SelectedItem!], interactive: true);

        _menuSkipHash = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Skip hash check"),
            Icon = LoadMenuIcon("checksum"),
        };
        _menuSkipHash.Click += (_, _) => _ = LaunchUpdate([SelectedItem!], no_integrity: true);

        _menuDownloadInstaller = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Download installer"),
            Icon = LoadMenuIcon("download"),
        };
        _menuDownloadInstaller.Click += (_, _) => { /* TODO: download-only operation not yet ported */ };

        var menuUninstallThenUpdate = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Uninstall package, then update it"),
            Icon = LoadMenuIcon("undelete"),
        };
        menuUninstallThenUpdate.Click += (_, _) => _ = LaunchUninstallThenUpdate(SelectedItem);

        var menuUninstall = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Uninstall package"),
            Icon = LoadMenuIcon("delete"),
        };
        menuUninstall.Click += (_, _) => _ = LaunchUninstallFromUpdates([SelectedItem!]);

        var menuIgnore = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Ignore updates for this package"),
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

        var menuSkipVersion = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Skip this version"),
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

        var menuShare = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Share this package"),
            Icon = LoadMenuIcon("share"),
        };
        menuShare.Click += (_, _) => _ = SharePackage(SelectedItem);

        var menuDetails = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Package details"),
            Icon = LoadMenuIcon("info_round"),
        };
        menuDetails.Click += (_, _) => _ = ShowDetailsForPackage(SelectedItem);

        var menu = new ContextMenu();
        menu.Items.Add(menuUpdate);
        menu.Items.Add(new Separator());
        menu.Items.Add(menuUpdateOptions);
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
        menu.Items.Add(menuIgnore);
        menu.Items.Add(menuSkipVersion);
        menu.Items.Add(menuPause);
        menu.Items.Add(new Separator());
        menu.Items.Add(menuShare);
        menu.Items.Add(menuDetails);

        return menu;
    }

    protected override void WhenShowingContextMenu(IPackage package)
    {
        if (_menuAsAdmin is null || _menuInteractive is null || _menuSkipHash is null
            || _menuDownloadInstaller is null || _menuOpenInstallLocation is null)
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

        var dialog = new PackageDetailsWindow(package, OperationType.Update);
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
            var op = new UpdatePackageOperation(pkg, opts);
            AvaloniaOperationRegistry.Add(op);
            _ = op.MainThread();
        }
    }

    private static async Task LaunchUninstallFromUpdates(IEnumerable<IPackage> packages)
    {
        foreach (var pkg in packages)
        {
            var opts = await InstallOptionsFactory.LoadApplicableAsync(pkg);
            var op = new UninstallPackageOperation(pkg, opts);
            AvaloniaOperationRegistry.Add(op);
            _ = op.MainThread();
        }
    }

    private static async Task LaunchUninstallThenUpdate(IPackage? package)
    {
        if (package is null || package.Source.IsVirtualManager) return;
        var uninstallOpts = await InstallOptionsFactory.LoadApplicableAsync(package);
        var updateOpts = await InstallOptionsFactory.LoadApplicableAsync(package);
        var uninstallOp = new UninstallPackageOperation(package, uninstallOpts);
        var updateOp = new UpdatePackageOperation(package, updateOpts, req: uninstallOp);
        AvaloniaOperationRegistry.Add(uninstallOp);
        AvaloniaOperationRegistry.Add(updateOp);
        _ = uninstallOp.MainThread();
        _ = updateOp.MainThread();
    }
}
