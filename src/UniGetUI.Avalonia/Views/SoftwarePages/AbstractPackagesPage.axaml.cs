using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Avalonia.Views.Controls;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.Avalonia.Views.Pages;

public abstract partial class AbstractPackagesPage : UserControl,
    IKeyboardShortcutListener, IEnterLeaveListener, ISearchBoxPage
{
    public PackagesPageViewModel ViewModel => (PackagesPageViewModel)DataContext!;

    protected AbstractPackagesPage(PackagesPageData data)
    {
        // InitializeComponent BEFORE setting DataContext so that the svg:Svg
        // Path binding has no context during XamlIlPopulate — Skia crashes if
        // it tries to load an SVG synchronously mid-init on macOS.
        InitializeComponent();
        DataContext = new PackagesPageViewModel(data);

        // Wire ViewModel events that need UI access
        ViewModel.FocusListRequested += OnFocusListRequested;
        ViewModel.HelpRequested += () => GetMainWindow()?.Navigate(PageType.Help);
        ViewModel.ManageIgnoredRequested += async () =>
        {
            if (GetMainWindow() is { } win)
                await new ManageIgnoredUpdatesWindow().ShowDialog(win);
        };
        ViewModel.SharePackageRequested += async (pkgName, url) =>
        {
            if (GetMainWindow() is not { } win) return;
            if (url is null)
            {
                await ViewModel.ShowInfoDialog(win,
                    CoreTools.Translate("Nothing to share"),
                    CoreTools.Translate("Please select a package first."));
                return;
            }
            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null) await clipboard.SetTextAsync(url);
            await ViewModel.ShowInfoDialog(win,
                CoreTools.Translate("Share link copied"),
                CoreTools.Translate("The share link for {0} has been copied to the clipboard.", pkgName ?? ""));
        };

        // "New version" sort option is only relevant on the updates page
        OrderByNewVersion_Menu.IsVisible = ViewModel.RoleIsUpdateLike;

        // Stamp initial checkmarks, then keep them in sync with sort-property changes
        UpdateSortMenuChecks();
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(PackagesPageViewModel.SortFieldIndex)
                                  or nameof(PackagesPageViewModel.SortAscending))
                UpdateSortMenuChecks();
        };

        // Build the toolbar now that both AXAML controls and the ViewModel are ready
        GenerateToolBar(ViewModel);

        // Double-click a list row → show details
        PackageList.DoubleTapped += (_, _) => _ = ShowDetailsForPackage(SelectedItem);

        // Keyboard shortcuts on the package list
        PackageList.KeyDown += PackageList_KeyDown;

        // Wire context menu (built by subclass)
        var contextMenu = GenerateContextMenu();
        if (contextMenu is not null)
        {
            PackageList.ContextMenu = contextMenu;
            contextMenu.Opening += (_, _) =>
            {
                var pkg = SelectedItem;
                if (pkg is not null) WhenShowingContextMenu(pkg);
            };
        }
    }

    // ─── UI-only: focus the package list ─────────────────────────────────────
    private void OnFocusListRequested() => PackageList.Focus();

    public void FocusPackageList()
    {
        if (ViewModel.MegaQueryBoxEnabled)
            Dispatcher.UIThread.Post(() => MegaQueryBlock.Focus(), DispatcherPriority.Background);
        else
            ViewModel.RequestFocusList();
    }
    public void FilterPackages() => ViewModel.FilterPackages();

    // ─── Abstract: let concrete pages add toolbar items ───────────────────────
    protected abstract void GenerateToolBar(PackagesPageViewModel vm);

    // ─── Abstract: per-page actions invoked by base class keyboard/mouse handlers ─
    /// <summary>Performs the page's primary action (install / uninstall / update) on the package.</summary>
    protected abstract void PerformMainPackageAction(IPackage? package);
    /// <summary>Opens the details dialog for the package.</summary>
    protected abstract Task ShowDetailsForPackage(IPackage? package);
    /// <summary>Opens the installation-options dialog for the package.</summary>
    protected abstract Task ShowInstallationOptionsForPackage(IPackage? package);

    // ─── Virtual: let concrete pages supply a context menu ────────────────────
    protected virtual ContextMenu? GenerateContextMenu() => null;
    protected virtual void WhenShowingContextMenu(IPackage package) { }

    // ─── Helper: create a 16×16 SvgIcon for use as a menu item icon ───────────
    protected static SvgIcon LoadMenuIcon(string svgName) => new()
    {
        Path = $"avares://UniGetUI.Avalonia/Assets/Symbols/{svgName}.svg",
        Width = 16,
        Height = 16,
    };

    // ─── Protected access to main toolbar controls for subclasses ─────────────
    /// <summary>Sets the icon and text of the primary action button.</summary>
    protected void SetMainButton(string svgName, string label, Action onClick)
    {
        MainToolbarButtonIcon.Path = $"avares://UniGetUI.Avalonia/Assets/Symbols/{svgName}.svg";
        MainToolbarButtonText.Text = label;
        MainToolbarButton.Click += (_, _) => onClick();
    }

    /// <summary>Sets the dropdown flyout of the primary action button.</summary>
    protected void SetMainButtonDropdown(MenuFlyout flyout)
    {
        MainToolbarButtonDropdown.Flyout = flyout;
    }

    // ─── Package selection ────────────────────────────────────────────────────
    /// <summary>
    /// Returns the focused row's package, or the single checked package if
    /// nothing is focused. Mirrors the WinUI SelectedItem pattern.
    /// </summary>
    protected IPackage? SelectedItem
    {
        get
        {
            if (PackageList.SelectedItem is PackageWrapper w)
                return w.Package;

            var checked_ = ViewModel.FilteredPackages.GetCheckedPackages();
            if (checked_.Count == 1)
                return checked_.First();

            return null;
        }
    }

    // ─── Operation launchers (delegated to ViewModel) ─────────────────────────
    protected static Task LaunchInstall(
        IEnumerable<IPackage> packages,
        bool? elevated = null,
        bool? interactive = null,
        bool? no_integrity = null)
        => PackagesPageViewModel.LaunchInstall(packages, elevated, interactive, no_integrity);

    // ─── Sort menu checkmarks (UI reacts to ViewModel sort changes) ───────────
    private static TextBlock? Check(bool show) =>
        show ? new TextBlock { Text = "✓", FontSize = 12 } : null;

    private void UpdateSortMenuChecks()
    {
        OrderByName_Menu.Icon = Check(ViewModel.SortFieldIndex == 0);
        OrderById_Menu.Icon = Check(ViewModel.SortFieldIndex == 1);
        OrderByVersion_Menu.Icon = Check(ViewModel.SortFieldIndex == 2);
        OrderByNewVersion_Menu.Icon = Check(ViewModel.SortFieldIndex == 3);
        OrderBySource_Menu.Icon = Check(ViewModel.SortFieldIndex == 4);
        OrderByAscending_Menu.Icon = Check(ViewModel.SortAscending);
        OrderByDescending_Menu.Icon = Check(!ViewModel.SortAscending);
    }

    // ─── IKeyboardShortcutListener ────────────────────────────────────────────
    public void SearchTriggered()
    {
        // TODO: focus global search box
    }

    public void ReloadTriggered() => ViewModel.TriggerReload();
    public void SelectAllTriggered() => ViewModel.ToggleSelectAll();

    // ─── IEnterLeaveListener ──────────────────────────────────────────────────
    public virtual void OnEnter() { }
    public virtual void OnLeave() { }

    // ─── ISearchBoxPage ───────────────────────────────────────────────────────
    public string QueryBackup
    {
        get => ViewModel.QueryBackup;
        set => ViewModel.QueryBackup = value;
    }

    public string SearchBoxPlaceholder => ViewModel.SearchBoxPlaceholder;

    public void SearchBox_QuerySubmitted(object? sender, EventArgs? e) => ViewModel.HandleSearchSubmitted();

    private void MegaQueryBlock_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter || e.Key == Key.Return)
            ViewModel.SubmitSearch();
    }

    private void PackageList_KeyDown(object? sender, KeyEventArgs e)
    {
        var pkg = SelectedItem;
        if (pkg is null) return;

        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if (e.Key is Key.Enter or Key.Return)
        {
            if (alt)
                _ = ShowInstallationOptionsForPackage(pkg);
            else if (ctrl)
                PerformMainPackageAction(pkg);
            else
                _ = ShowDetailsForPackage(pkg);
            e.Handled = true;
        }
    }

    // ─── Shared cross-page helpers ────────────────────────────────────────────
    protected static MainWindow? GetMainWindow()
        => Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow w } ? w : null;
}
