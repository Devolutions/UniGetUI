using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Avalonia.Views.Controls;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.Avalonia.Views.Pages;

public abstract partial class AbstractPackagesPage : UserControl,
    IKeyboardShortcutListener, IEnterLeaveListener, ISearchBoxPage
{
    public PackagesPageViewModel ViewModel => (PackagesPageViewModel)DataContext!;
    private readonly ContextMenu? _contextMenu;
    private double _savedFilterPaneWidth = 220;
    private bool _isOverlayMode;

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

        // "New version" sort option is only relevant on the updates page
        OrderByNewVersion_Menu.IsVisible = ViewModel.RoleIsUpdateLike;

        // Stamp initial checkmarks, then keep them in sync with sort-property changes
        UpdateSortMenuChecks();
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(PackagesPageViewModel.SortFieldIndex)
                                  or nameof(PackagesPageViewModel.SortAscending))
            {
                UpdateSortMenuChecks();
                SyncOrderByButtonName();
            }
            if (args.PropertyName is nameof(PackagesPageViewModel.IsFilterPaneOpen))
            {
                SyncFiltersButtonName();
                UpdateFilterPaneColumn(ViewModel.IsFilterPaneOpen);
            }
        };
        SyncFiltersButtonName();
        SyncOrderByButtonName();

        // Reload button added before subclass toolbar items (mirrors WinUI AbstractPackagesPage)
        if (!ViewModel.DisableReload)
        {
            var reloadBtn = ViewModel.AddToolbarButton("reload", CoreTools.Translate("Reload"),
                ViewModel.TriggerReload);
            UpdateReloadButtonTooltip(reloadBtn);
        }

        // Build the toolbar now that both AXAML controls and the ViewModel are ready
        GenerateToolBar(ViewModel);

        // Double-click a list row → show details
        PackageList.DoubleTapped += (_, _) => _ = ShowDetailsForPackage(SelectedItem);

        // Keyboard shortcuts on the package list
        PackageList.KeyDown += PackageList_KeyDown;

        // Type-to-search: printable characters typed while the list is focused
        // redirect focus + the typed character to the global search box.
        PackageList.TextInput += PackageList_TextInput;

        // Snap-close when splitter is dragged below the minimum (inline mode only).
        // Using ColumnDefinition.WidthProperty fires every drag step, not just on release.
        FilteringPanel.ColumnDefinitions[0]
            .GetObservable(ColumnDefinition.WidthProperty)
            .Subscribe(width =>
            {
                if (_isOverlayMode || !ViewModel.IsFilterPaneOpen) return;
                if (width.IsAbsolute && width.Value >= 100)
                {
                    _savedFilterPaneWidth = width.Value;
                    Settings.SetDictionaryItem(Settings.K.SidepanelWidths, ViewModel.PageName, (int)width.Value);
                }
                else if (width.IsAbsolute && width.Value < 100)
                {
                    _savedFilterPaneWidth = 220;
                    ViewModel.IsFilterPaneOpen = false;
                }
            });

        // Responsive: switch between inline and overlay modes based on content width.
        FilteringPanel.GetObservable(BoundsProperty)
            .Subscribe(bounds => OnFilteringPanelWidthChanged(bounds.Width));

        // Overlay backdrop dismisses the filter pane when tapped.
        FilterOverlayBackdrop.PointerPressed += (_, _) => ViewModel.IsFilterPaneOpen = false;

        // Wire context menu (built by subclass)
        _contextMenu = GenerateContextMenu();
        if (_contextMenu is not null)
        {
            PackageList.ContextMenu = _contextMenu;
            _contextMenu.Opening += (_, _) =>
            {
                var pkg = SelectedItem;
                if (pkg is not null) WhenShowingContextMenu(pkg);
            };
        }

        // Restore per-page filter pane width from settings.
        var savedWidth = Settings.GetDictionaryItem<string, int>(Settings.K.SidepanelWidths, ViewModel.PageName);
        if (savedWidth >= 100) _savedFilterPaneWidth = savedWidth;

        // Apply the initial filter-pane state (AXAML defaults to 220px open).
        UpdateFilterPaneColumn(ViewModel.IsFilterPaneOpen);
    }

    // ─── UI-only: focus the package list ─────────────────────────────────────
    private void OnFocusListRequested() => PackageList.Focus();

    private void UpdateReloadButtonTooltip(Button reloadButton)
    {
        ToolTip.SetTip(reloadButton, ViewModel.ReloadButtonTooltip);
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(PackagesPageViewModel.ReloadButtonTooltip))
                ToolTip.SetTip(reloadButton, ViewModel.ReloadButtonTooltip);
        };
    }

    public void FocusPackageList()
    {
        if (ViewModel.MegaQueryBoxEnabled)
            Dispatcher.UIThread.Post(() =>
            {
                if (!ViewModel.MegaQueryVisible) return;
                MegaQueryBlock.Focus();
            }, DispatcherPriority.ApplicationIdle);
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
        AutomationProperties.SetName(MainToolbarButton, label);
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

    private void SyncFiltersButtonName()
    {
        bool open = ViewModel.IsFilterPaneOpen;
        string state = open ? CoreTools.Translate("Open") : CoreTools.Translate("Closed");
        string label = CoreTools.Translate("Filters");
        AutomationProperties.SetName(ToggleFiltersButton, $"{label}, {state}");
    }

    private void SyncOrderByButtonName()
    {
        string direction = ViewModel.SortAscending
            ? CoreTools.Translate("Ascending")
            : CoreTools.Translate("Descending");
        AutomationProperties.SetName(
            OrderByButton,
            CoreTools.Translate("{0}: {1}, {2}", CoreTools.Translate("Order by"), ViewModel.SortFieldName, direction));
    }

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
    public void SearchTriggered() => GetMainWindow()?.FocusGlobalSearch();

    public void ReloadTriggered() => ViewModel.TriggerReload();
    public void SelectAllTriggered() => ViewModel.ToggleSelectAll();
    public void DetailsTriggered() { if (SelectedItem is { } pkg) _ = ShowDetailsForPackage(pkg); }

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

    private void PackageList_TextInput(object? sender, TextInputEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text)) return;

        // Append the typed character to the current query and move focus to the search box
        GetMainWindow()?.FocusGlobalSearch(ViewModel.GlobalQueryText + e.Text);
        e.Handled = true;
    }

    // ─── Filter pane column width management ─────────────────────────────────

    private void OnFilteringPanelWidthChanged(double width)
    {
        if (width <= 0) return; // layout not complete yet
        bool shouldBeOverlay = width < 1000;
        if (shouldBeOverlay == _isOverlayMode) return;

        _isOverlayMode = shouldBeOverlay;

        if (_isOverlayMode && ViewModel.IsFilterPaneOpen)
            ViewModel.IsFilterPaneOpen = false; // collapse pane when entering overlay
        else
            UpdateFilterPaneColumn(ViewModel.IsFilterPaneOpen);
    }

    private void UpdateFilterPaneColumn(bool open)
    {
        if (FilteringPanel.ColumnDefinitions.Count < 2) return;

        if (_isOverlayMode)
        {
            // Package list fills full width; filter pane and splitter take no space.
            FilteringPanel.ColumnDefinitions[0].Width = new GridLength(0);
            FilteringPanel.ColumnDefinitions[1].Width = new GridLength(0);

            // Float the filter pane on top of the content when open.
            Grid.SetColumnSpan(SidePanel, 3);
            SidePanel.ZIndex = 10;
            SidePanel.Width = _savedFilterPaneWidth;
            SidePanel.HorizontalAlignment = HorizontalAlignment.Left;

            // Semi-transparent backdrop covers the package list behind the pane.
            FilterOverlayBackdrop.IsVisible = open;
        }
        else
        {
            // Inline mode: pane sits beside the package list.
            Grid.SetColumnSpan(SidePanel, 1);
            SidePanel.ZIndex = 0;
            SidePanel.Width = double.NaN;
            SidePanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            FilterOverlayBackdrop.IsVisible = false;

            FilteringPanel.ColumnDefinitions[0].Width = open
                ? new GridLength(_savedFilterPaneWidth)
                : new GridLength(0);
            FilteringPanel.ColumnDefinitions[1].Width = open
                ? new GridLength(4)
                : new GridLength(0);
        }
    }

    // ─── Card overflow button (Grid / Icons view) ─────────────────────────────
    private void CardOverflowButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: PackageWrapper wrapper }) return;
        PackageList.SelectedItem = wrapper;
        if (_contextMenu is null) return;
        WhenShowingContextMenu(wrapper.Package);
        _contextMenu.Open(sender as Control);
        e.Handled = true;
    }

    // ─── Shared cross-page helpers ────────────────────────────────────────────
    protected static MainWindow? GetMainWindow()
        => Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime { MainWindow: MainWindow w } ? w : null;
}
