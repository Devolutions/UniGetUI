using System.Diagnostics;
using Avalonia.Controls;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.Classes;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Avalonia.Views.Pages;

public class InstalledPackagesPage : AbstractPackagesPage
{
    // Context-menu items whose enabled state depends on the focused package
    private MenuItem? _menuAsAdmin;
    private MenuItem? _menuInteractive;
    private MenuItem? _menuRemoveData;
    private MenuItem? _menuInstallationOptions;
    private MenuItem? _menuReinstall;
    private MenuItem? _menuUninstallThenReinstall;
    private MenuItem? _menuIgnoreUpdates;
    private MenuItem? _menuShare;
    private MenuItem? _menuDetails;
    private MenuItem? _menuOpenInstallLocation;
    private MenuItem? _menuDownloadInstaller;

    public InstalledPackagesPage() : base(new PackagesPageData
    {
        PageName = "SoftwarePages.InstalledPackagesPage",
        PageTitle = CoreTools.Translate("Installed Packages"),
        IconName = "local_pc",
        PageRole = OperationType.Uninstall,
        Loader = InstalledPackagesLoader.Instance ?? new InstalledPackagesLoader([]),
        MegaQueryBlockEnabled = false,
        DisableSuggestedResultsRadio = true,
        PackagesAreCheckedByDefault = false,
        ShowLastLoadTime = true,
        DisableAutomaticPackageLoadOnStart = false,
        DisableFilterOnQueryChange = false,
        DisableReload = false,
        NoPackages_BackgroundText = CoreTools.Translate("No packages were found"),
        NoPackages_SourcesText = CoreTools.Translate("No packages were found"),
        NoPackages_SubtitleText_Base = CoreTools.Translate("No packages were found"),
        MainSubtitle_StillLoading = CoreTools.Translate("Loading packages"),
        NoMatches_BackgroundText = CoreTools.Translate("No results were found matching the input criteria"),
    })
    { }

    protected override void GenerateToolBar(PackagesPageViewModel vm)
    {
        // ── Dropdown: uninstall variants ────────────────────────────────────
        var uninstallAsAdmin = new MenuItem { Header = CoreTools.Translate("Uninstall as administrator") };
        var uninstallInteractive = new MenuItem { Header = CoreTools.Translate("Interactive uninstall") };
        var downloadInstallers = new MenuItem { Header = CoreTools.Translate("Download selected installers") };

        SetMainButton("delete", CoreTools.Translate("Uninstall selection"), () =>
            _ = LaunchUninstall(vm.FilteredPackages.GetCheckedPackages()));

        SetMainButtonDropdown(new MenuFlyout
        {
            Items = { uninstallAsAdmin, uninstallInteractive, new Separator(), downloadInstallers },
        });

        uninstallAsAdmin.Click += (_, _) => _ = LaunchUninstall(vm.FilteredPackages.GetCheckedPackages(), elevated: true);
        uninstallInteractive.Click += (_, _) => _ = LaunchUninstall(vm.FilteredPackages.GetCheckedPackages(), interactive: true);
        downloadInstallers.Click += (_, _) => { /* TODO: download-only operation not yet ported */ };

        // ── Toolbar buttons ─────────────────────────────────────────────────
        ViewModel.AddToolbarSeparator();
        ViewModel.AddToolbarButton("options", CoreTools.Translate("Uninstall options"),
            () => _ = ShowInstallationOptionsForPackage(SelectedItem), showLabel: false);
        ViewModel.AddToolbarSeparator();
        ViewModel.AddToolbarButton("info_round", CoreTools.Translate("Package details"),
            () => _ = ShowDetailsForPackage(SelectedItem), showLabel: false);
        ViewModel.AddToolbarButton("share", CoreTools.Translate("Share"),
            () => vm.RequestShareCommand.Execute(SelectedItem), showLabel: false);
        ViewModel.AddToolbarSeparator();
        ViewModel.AddToolbarButton("pin", CoreTools.Translate("Ignore selected packages"), async () =>
        {
            foreach (var pkg in vm.FilteredPackages.GetCheckedPackages())
            {
                if (!pkg.Source.IsVirtualManager)
                {
                    UpgradablePackagesLoader.Instance.Remove(pkg);
                    await pkg.AddToIgnoredUpdatesAsync();
                }
            }
        });
        ViewModel.AddToolbarButton("clipboard_list", CoreTools.Translate("Manage ignored updates"),
            () => vm.RequestManageIgnoredCommand.Execute(null));
        ViewModel.AddToolbarSeparator();
        ViewModel.AddToolbarButton("add_to", CoreTools.Translate("Add selection to bundle"),
            () => _ = ExportSelectionToBundleAsync(vm));
        ViewModel.AddToolbarSeparator();
        ViewModel.AddToolbarButton("help", CoreTools.Translate("Help"),
            () => vm.RequestHelpCommand.Execute(null));
    }

    // ─── Context menu ─────────────────────────────────────────────────────────
    protected override ContextMenu? GenerateContextMenu()
    {
        var menuUninstall = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Uninstall"),
            Icon = LoadMenuIcon("delete"),
        };
        menuUninstall.Click += (_, _) => _ = LaunchUninstall([SelectedItem!]);

        _menuInstallationOptions = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Uninstall options"),
            Icon = LoadMenuIcon("options"),
        };
        _menuInstallationOptions.Click += (_, _) => _ = ShowInstallationOptionsForPackage(SelectedItem);

        _menuOpenInstallLocation = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Open install location"),
            Icon = LoadMenuIcon("launch"),
        };
        _menuOpenInstallLocation.Click += (_, _) => OpenInstallLocation(SelectedItem);

        _menuAsAdmin = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Uninstall as administrator"),
            Icon = LoadMenuIcon("uac"),
        };
        _menuAsAdmin.Click += (_, _) => _ = LaunchUninstall([SelectedItem!], elevated: true);

        _menuInteractive = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Interactive uninstall"),
            Icon = LoadMenuIcon("interactive"),
        };
        _menuInteractive.Click += (_, _) => _ = LaunchUninstall([SelectedItem!], interactive: true);

        _menuRemoveData = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Uninstall and remove data"),
            Icon = LoadMenuIcon("close_round"),
        };
        _menuRemoveData.Click += (_, _) => _ = LaunchUninstall([SelectedItem!], remove_data: true);

        _menuDownloadInstaller = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Download installer"),
            Icon = LoadMenuIcon("download"),
        };
        _menuDownloadInstaller.Click += (_, _) => { /* TODO: download-only operation not yet ported */ };

        _menuReinstall = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Reinstall package"),
            Icon = LoadMenuIcon("download"),
        };
        _menuReinstall.Click += (_, _) => _ = LaunchReinstall(SelectedItem);

        _menuUninstallThenReinstall = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Uninstall package, then reinstall it"),
            Icon = LoadMenuIcon("undelete"),
        };
        _menuUninstallThenReinstall.Click += (_, _) => _ = LaunchUninstallThenReinstall(SelectedItem);

        _menuIgnoreUpdates = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Ignore updates for this package"),
            Icon = LoadMenuIcon("pin"),
        };
        _menuIgnoreUpdates.Click += (_, _) => _ = ToggleIgnoreUpdatesAsync(SelectedItem);

        _menuShare = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Share this package"),
            Icon = LoadMenuIcon("share"),
        };
        _menuShare.Click += (_, _) => ViewModel.RequestShareCommand.Execute(SelectedItem);

        _menuDetails = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Package details"),
            Icon = LoadMenuIcon("info_round"),
        };
        _menuDetails.Click += (_, _) => _ = ShowDetailsForPackage(SelectedItem);

        var menu = new ContextMenu();
        menu.Items.Add(menuUninstall);
        menu.Items.Add(new Separator());
        menu.Items.Add(_menuInstallationOptions);
        menu.Items.Add(_menuOpenInstallLocation);
        menu.Items.Add(new Separator());
        menu.Items.Add(_menuAsAdmin);
        menu.Items.Add(_menuInteractive);
        menu.Items.Add(_menuRemoveData);
        menu.Items.Add(new Separator());
        menu.Items.Add(_menuDownloadInstaller);
        menu.Items.Add(new Separator());
        menu.Items.Add(_menuReinstall);
        menu.Items.Add(_menuUninstallThenReinstall);
        menu.Items.Add(new Separator());
        menu.Items.Add(_menuIgnoreUpdates);
        menu.Items.Add(new Separator());
        menu.Items.Add(_menuShare);
        menu.Items.Add(_menuDetails);

        return menu;
    }

    protected override void WhenShowingContextMenu(IPackage package)
    {
        if (_menuAsAdmin is null || _menuInteractive is null || _menuRemoveData is null
            || _menuInstallationOptions is null || _menuReinstall is null
            || _menuUninstallThenReinstall is null || _menuIgnoreUpdates is null
            || _menuShare is null || _menuDetails is null
            || _menuOpenInstallLocation is null || _menuDownloadInstaller is null)
        {
            Logger.Warn("Context menu items are null on InstalledPackagesPage");
            return;
        }

        bool isLocal = package.Source.IsVirtualManager;
        var caps = package.Manager.Capabilities;

        _menuAsAdmin.IsEnabled = caps.CanRunAsAdmin;
        _menuInteractive.IsEnabled = caps.CanRunInteractively;
        _menuRemoveData.IsEnabled = caps.CanRemoveDataOnUninstall;
        _menuDownloadInstaller.IsEnabled = !isLocal && caps.CanDownloadInstaller;
        _menuInstallationOptions.IsEnabled = !isLocal;
        _menuReinstall.IsEnabled = !isLocal;
        _menuUninstallThenReinstall.IsEnabled = !isLocal;
        _menuShare.IsEnabled = !isLocal;
        _menuDetails.IsEnabled = !isLocal;
        _menuOpenInstallLocation.IsEnabled =
            package.Manager.DetailsHelper.GetInstallLocation(package) is not null;

        // Async ignore-state toggle label — fire and forget is fine here
        _ = UpdateIgnoreMenuItemAsync(package);
    }

    // ─── Abstract action overrides ────────────────────────────────────────────
    protected override void PerformMainPackageAction(IPackage? package)
    {
        if (package is null) return;
        _ = LaunchUninstall([package]);
    }

    protected override async Task ShowDetailsForPackage(IPackage? package)
    {
        if (package is null) return;
        if (GetMainWindow() is not { } win) return;

        var dialog = new PackageDetailsWindow(package, OperationType.Uninstall);
        await dialog.ShowDialog(win);

        if (dialog.ShouldProceedWithOperation)
            await LaunchUninstall([package]);
    }

    protected override async Task ShowInstallationOptionsForPackage(IPackage? package)
    {
        if (package is null || package.Source.IsVirtualManager) return;
        var opts = await InstallOptionsFactory.LoadForPackageAsync(package);
        if (GetMainWindow() is not { } win) return;

        var dialog = new InstallOptionsWindow(package, OperationType.Uninstall, opts);
        await dialog.ShowDialog(win);
        await InstallOptionsFactory.SaveForPackageAsync(opts, package);

        if (dialog.ShouldProceedWithOperation)
            await LaunchUninstall([package]);
    }

    // ─── Page-specific actions ────────────────────────────────────────────────

    private static void OpenInstallLocation(IPackage? package)
    {
        if (package is null) return;
        var path = package.Manager.DetailsHelper.GetInstallLocation(package);
        if (path is not null)
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }

    private static async Task ExportSelectionToBundleAsync(PackagesPageViewModel vm)
    {
        var packages = vm.FilteredPackages.GetCheckedPackages();
        GetMainWindow()?.Navigate(PageType.Bundles);
        if (PackageBundlesLoader.Instance is not null)
            await PackageBundlesLoader.Instance.AddPackagesAsync(packages);
    }

    private static async Task LaunchUninstall(
        IEnumerable<IPackage> packages,
        bool? elevated = null,
        bool? interactive = null,
        bool? remove_data = null)
    {
        var list = packages.ToList();
        if (list.Count == 0) return;

        foreach (var pkg in list)
        {
            var opts = await InstallOptionsFactory.LoadApplicableAsync(
                pkg, elevated: elevated, interactive: interactive, remove_data: remove_data);
            var op = new UninstallPackageOperation(pkg, opts);
            AvaloniaOperationRegistry.Add(op);
            _ = op.MainThread();
        }
    }

    private static async Task LaunchReinstall(IPackage? package)
    {
        if (package is null || package.Source.IsVirtualManager) return;
        var opts = await InstallOptionsFactory.LoadApplicableAsync(package);
        var op = new InstallPackageOperation(package, opts);
        AvaloniaOperationRegistry.Add(op);
        _ = op.MainThread();
    }

    private static async Task LaunchUninstallThenReinstall(IPackage? package)
    {
        if (package is null || package.Source.IsVirtualManager) return;
        var uninstallOpts = await InstallOptionsFactory.LoadApplicableAsync(package);
        var reinstallOpts = await InstallOptionsFactory.LoadApplicableAsync(package);
        var uninstallOp = new UninstallPackageOperation(package, uninstallOpts);
        var reinstallOp = new InstallPackageOperation(package, reinstallOpts, req: uninstallOp);
        AvaloniaOperationRegistry.Add(uninstallOp);
        AvaloniaOperationRegistry.Add(reinstallOp);
        _ = uninstallOp.MainThread();
        _ = reinstallOp.MainThread();
    }

    private static async Task ToggleIgnoreUpdatesAsync(IPackage? package)
    {
        if (package is null || package.Source.IsVirtualManager) return;
        if (await package.HasUpdatesIgnoredAsync())
            await package.RemoveFromIgnoredUpdatesAsync();
        else
        {
            await package.AddToIgnoredUpdatesAsync();
            UpgradablePackagesLoader.Instance.Remove(package);
        }
    }

    private async Task UpdateIgnoreMenuItemAsync(IPackage package)
    {
        if (_menuIgnoreUpdates is null || package.Source.IsVirtualManager) return;
        _menuIgnoreUpdates.IsEnabled = false;
        bool ignored = await package.HasUpdatesIgnoredAsync();
        _menuIgnoreUpdates.Header = ignored
            ? CoreTools.Translate("Do not ignore updates for this package anymore")
            : CoreTools.Translate("Ignore updates for this package");
        _menuIgnoreUpdates.IsEnabled = true;
    }
}
