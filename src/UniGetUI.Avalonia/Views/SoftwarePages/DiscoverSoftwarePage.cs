using System.Web;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Telemetry;
using UniGetUI.PackageEngine.Classes.Manager.Classes;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Avalonia.Views.Pages;

public class DiscoverSoftwarePage : AbstractPackagesPage
{
    // Context-menu items whose enabled state depends on the focused package's manager
    private MenuItem? _menuAsAdmin;
    private MenuItem? _menuInteractive;
    private MenuItem? _menuSkipHash;
    private MenuItem? _menuDownloadInstaller;

    public DiscoverSoftwarePage() : base(new PackagesPageData
    {
        PageName = "SoftwarePages.DiscoverSoftwarePage",
        PageTitle = CoreTools.Translate("Discover Packages"),
        IconName = "ms_store",
        PageRole = OperationType.Install,
        Loader = DiscoverablePackagesLoader.Instance ?? new DiscoverablePackagesLoader([]),
        MegaQueryBlockEnabled = true,
        DisableSuggestedResultsRadio = false,
        PackagesAreCheckedByDefault = false,
        ShowLastLoadTime = false,
        DisableAutomaticPackageLoadOnStart = true,
        DisableFilterOnQueryChange = true,
        DisableReload = false,
        NoPackages_BackgroundText = CoreTools.Translate("No results were found matching the input criteria"),
        NoPackages_SourcesText = CoreTools.Translate("No packages were found"),
        NoPackages_SubtitleText_Base = CoreTools.Translate("No packages were found"),
        MainSubtitle_StillLoading = CoreTools.Translate("Loading packages"),
        NoMatches_BackgroundText = CoreTools.Translate("No results were found matching the input criteria"),
    })
    { }

    protected override void GenerateToolBar(PackagesPageViewModel vm)
    {
        // ── Main button dropdown: install variants ──────────────────────────
        var installAsAdmin = new MenuItem { Header = CoreTools.Translate("Install as administrator"), IsVisible = OperatingSystem.IsWindows() };
        var installSkipHash = new MenuItem { Header = CoreTools.Translate("Skip integrity checks") };
        var installInteractive = new MenuItem { Header = CoreTools.Translate("Interactive installation") };
        var downloadInstallers = new MenuItem { Header = CoreTools.Translate("Download selected installers") };

        SetMainButton("download", CoreTools.Translate("Install selection"), () =>
            _ = LaunchInstall(vm.FilteredPackages.GetCheckedPackages()));

        SetMainButtonDropdown(new MenuFlyout
        {
            Items = { installAsAdmin, installSkipHash, installInteractive, new Separator(), downloadInstallers },
        });

        installAsAdmin.Click += (_, _) => _ = LaunchInstall(vm.FilteredPackages.GetCheckedPackages(), elevated: true);
        installSkipHash.Click += (_, _) => _ = LaunchInstall(vm.FilteredPackages.GetCheckedPackages(), no_integrity: true);
        installInteractive.Click += (_, _) => _ = LaunchInstall(vm.FilteredPackages.GetCheckedPackages(), interactive: true);
        downloadInstallers.Click += (_, _) => { /* TODO: download-only operation not yet ported */ };

        // ── Toolbar buttons ─────────────────────────────────────────────────
        ViewModel.AddToolbarSeparator();
        ViewModel.AddToolbarButton("options", CoreTools.Translate("Install options"),
            () => _ = ShowInstallationOptionsForPackage(SelectedItem));
        ViewModel.AddToolbarSeparator();
        ViewModel.AddToolbarButton("info_round", CoreTools.Translate("Package details"),
            () => _ = ShowDetailsForPackage(SelectedItem), showLabel: false);
        ViewModel.AddToolbarButton("share", CoreTools.Translate("Share"),
            () => vm.RequestShareCommand.Execute(SelectedItem), showLabel: false);
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
        _menuAsAdmin = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Install as administrator"),
            Icon = LoadMenuIcon("uac"),
            IsVisible = OperatingSystem.IsWindows(),
        };
        _menuAsAdmin.Click += (_, _) => _ = LaunchInstall([SelectedItem!], elevated: true);

        _menuInteractive = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Interactive installation"),
            Icon = LoadMenuIcon("interactive"),
        };
        _menuInteractive.Click += (_, _) => _ = LaunchInstall([SelectedItem!], interactive: true);

        _menuSkipHash = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Skip hash check"),
            Icon = LoadMenuIcon("checksum"),
        };
        _menuSkipHash.Click += (_, _) => _ = LaunchInstall([SelectedItem!], no_integrity: true);

        _menuDownloadInstaller = new MenuItem
        {
            Header = CoreTools.AutoTranslated("Download installer"),
            Icon = LoadMenuIcon("download"),
        };
        _menuDownloadInstaller.Click += (_, _) => { /* TODO: download-only operation */ };

        var menuInstall = new MenuItem { Header = CoreTools.AutoTranslated("Install"), Icon = LoadMenuIcon("download") };
        menuInstall.Click += (_, _) => _ = LaunchInstall([SelectedItem!]);

        var menuInstallOptions = new MenuItem { Header = CoreTools.AutoTranslated("Install options"), Icon = LoadMenuIcon("options") };
        menuInstallOptions.Click += (_, _) => _ = ShowInstallationOptionsForPackage(SelectedItem);

        var menuShare = new MenuItem { Header = CoreTools.AutoTranslated("Share this package"), Icon = LoadMenuIcon("share") };
        menuShare.Click += (_, _) => ViewModel.RequestShareCommand.Execute(SelectedItem);

        var menuDetails = new MenuItem { Header = CoreTools.AutoTranslated("Package details"), Icon = LoadMenuIcon("info_round") };
        menuDetails.Click += (_, _) => _ = ShowDetailsForPackage(SelectedItem);

        var menu = new ContextMenu();
        menu.Items.Add(menuInstall);
        menu.Items.Add(new Separator());
        menu.Items.Add(menuInstallOptions);
        menu.Items.Add(new Separator());
        menu.Items.Add(_menuAsAdmin);
        menu.Items.Add(_menuInteractive);
        menu.Items.Add(_menuSkipHash);
        menu.Items.Add(_menuDownloadInstaller);
        menu.Items.Add(new Separator());
        menu.Items.Add(menuShare);
        menu.Items.Add(menuDetails);

        return menu;
    }

    protected override void WhenShowingContextMenu(IPackage package)
    {
        if (_menuAsAdmin is null || _menuInteractive is null
            || _menuSkipHash is null || _menuDownloadInstaller is null)
        {
            Logger.Warn("Context menu items are null on DiscoverSoftwarePage");
            return;
        }

        _menuAsAdmin.IsEnabled = package.Manager.Capabilities.CanRunAsAdmin;
        _menuInteractive.IsEnabled = package.Manager.Capabilities.CanRunInteractively;
        _menuSkipHash.IsEnabled = package.Manager.Capabilities.CanSkipIntegrityChecks;
        _menuDownloadInstaller.IsEnabled = package.Manager.Capabilities.CanDownloadInstaller;
    }

    // ─── Abstract action overrides ────────────────────────────────────────────
    protected override void PerformMainPackageAction(IPackage? package)
    {
        if (package is null) return;
        _ = LaunchInstall([package]);
    }

    protected override async Task ShowDetailsForPackage(IPackage? package)
    {
        if (package is null) return;
        if (GetMainWindow() is not { } win) return;

        var dialog = new PackageDetailsWindow(package, OperationType.Install);
        await dialog.ShowDialog(win);

        if (dialog.ShouldProceedWithOperation)
            await LaunchInstall([package]);
    }

    protected override async Task ShowInstallationOptionsForPackage(IPackage? package)
    {
        if (package is null || package.Source.IsVirtualManager) return;

        var opts = await InstallOptionsFactory.LoadForPackageAsync(package);
        if (GetMainWindow() is not { } win) return;

        var dialog = new InstallOptionsWindow(package, OperationType.Install, opts);
        await dialog.ShowDialog(win);
        await InstallOptionsFactory.SaveForPackageAsync(opts, package);

        if (dialog.ShouldProceedWithOperation)
            await LaunchInstall([package]);
    }

    private static async Task ExportSelectionToBundleAsync(PackagesPageViewModel vm)
    {
        var packages = vm.FilteredPackages.GetCheckedPackages();
        GetMainWindow()?.Navigate(PageType.Bundles);
        if (PackageBundlesLoader.Instance is not null)
            await PackageBundlesLoader.Instance.AddPackagesAsync(packages);
    }
}
