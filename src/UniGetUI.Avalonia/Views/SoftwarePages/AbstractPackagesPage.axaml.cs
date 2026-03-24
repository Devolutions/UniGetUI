using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Avalonia.Views;
using UniGetUI.Avalonia.Views.Controls;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.Classes;
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

        // "New version" sort option is only relevant on the updates page
        OrderByNewVersion_Menu.IsVisible = ViewModel.RoleIsUpdateLike;

        // Stamp initial checkmarks
        UpdateSortMenuChecks();

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
    private void OnFocusListRequested()
    {
        PackageList.Focus();
    }

    public void FocusPackageList() => ViewModel.RequestFocusList();
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

    // ─── Toolbar builder helpers ───────────────────────────────────────────────
    /// <summary>
    /// Adds a button to the toolbar.
    /// When <paramref name="showLabel"/> is false the label is shown only as a tooltip.
    /// </summary>
    protected Button AddToolbarButton(string svgName, string label, Action onClick, bool showLabel = true)
    {
        var icon = new SvgIcon
        {
            Path = $"avares://UniGetUI.Avalonia/Assets/Symbols/{svgName}.svg",
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        content.Children.Add(icon);
        if (showLabel)
        {
            content.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var btn = new Button
        {
            Height = 36,
            Padding = new global::Avalonia.Thickness(8, 4),
            CornerRadius = new global::Avalonia.CornerRadius(4),
            Content = content,
        };
        ToolTip.SetTip(btn, label);
        btn.Click += (_, _) => onClick();
        ViewModel.ToolBarItems.Add(btn);
        return btn;
    }

    /// <summary>Adds a thin vertical separator to the toolbar.</summary>
    protected void AddToolbarSeparator()
    {
        ViewModel.ToolBarItems.Add(new Separator
        {
            Width = 1,
            Height = 30,
            Margin = new global::Avalonia.Thickness(4, 4),
            Background = Application.Current?.FindResource("AppBorderBrush") as IBrush
                         ?? new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
        });
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

    // ─── Order-by menu items (UI → ViewModel.SortFieldIndex / SortAscending) ───
    private void OrderByName_Click(object? sender, RoutedEventArgs e) { ViewModel.SortFieldIndex = 0; UpdateSortMenuChecks(); }
    private void OrderById_Click(object? sender, RoutedEventArgs e) { ViewModel.SortFieldIndex = 1; UpdateSortMenuChecks(); }
    private void OrderByVersion_Click(object? sender, RoutedEventArgs e) { ViewModel.SortFieldIndex = 2; UpdateSortMenuChecks(); }
    private void OrderByNewVersion_Click(object? sender, RoutedEventArgs e) { ViewModel.SortFieldIndex = 3; UpdateSortMenuChecks(); }
    private void OrderBySource_Click(object? sender, RoutedEventArgs e) { ViewModel.SortFieldIndex = 4; UpdateSortMenuChecks(); }
    private void OrderByAscending_Click(object? sender, RoutedEventArgs e) { ViewModel.SortAscending = true; UpdateSortMenuChecks(); }
    private void OrderByDescending_Click(object? sender, RoutedEventArgs e) { ViewModel.SortAscending = false; UpdateSortMenuChecks(); }

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

    // ─── Search mode radio buttons (UI → ViewModel.SearchMode) ───────────────
    private void QueryNameRadio_IsCheckedChanged(object? sender, RoutedEventArgs e)
    { if (QueryNameRadio.IsChecked == true) ViewModel.SearchMode = SearchMode.Name; }

    private void QueryIdRadio_IsCheckedChanged(object? sender, RoutedEventArgs e)
    { if (QueryIdRadio.IsChecked == true) ViewModel.SearchMode = SearchMode.Id; }

    private void QueryBothRadio_IsCheckedChanged(object? sender, RoutedEventArgs e)
    { if (QueryBothRadio.IsChecked == true) ViewModel.SearchMode = SearchMode.Both; }

    private void QueryExactMatch_IsCheckedChanged(object? sender, RoutedEventArgs e)
    { if (QueryExactMatch.IsChecked == true) ViewModel.SearchMode = SearchMode.Exact; }

    private void QuerySimilarResultsRadio_IsCheckedChanged(object? sender, RoutedEventArgs e)
    { if (QuerySimilarResultsRadio.IsChecked == true) ViewModel.SearchMode = SearchMode.Similar; }

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

    protected static void OpenHelp()
        => GetMainWindow()?.Navigate(PageType.Help);

    protected async Task ShowManageIgnoredAsync()
    {
        if (GetMainWindow() is not { } win) return;
        await new ManageIgnoredUpdatesWindow().ShowDialog(win);
    }

    protected async Task SharePackage(IPackage? package)
    {
        if (GetMainWindow() is not { } win) return;

        if (package is null || package.Source.IsVirtualManager)
        {
            await ShowInfoDialog(win,
                CoreTools.Translate("Nothing to share"),
                CoreTools.Translate("Please select a package first."));
            return;
        }

        var url = "https://marticliment.com/unigetui/share?"
            + "name=" + System.Web.HttpUtility.UrlEncode(package.Name)
            + "&id=" + System.Web.HttpUtility.UrlEncode(package.Id)
            + "&sourceName=" + System.Web.HttpUtility.UrlEncode(package.Source.Name)
            + "&managerName=" + System.Web.HttpUtility.UrlEncode(package.Manager.Name);

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null) await clipboard.SetTextAsync(url);

        await ShowInfoDialog(win,
            CoreTools.Translate("Share link copied"),
            CoreTools.Translate("The share link for {0} has been copied to the clipboard.", package.Name));
    }

    private static async Task ShowInfoDialog(Window owner, string title, string message)
    {
        var dialog = new Window
        {
            Width = 460,
            Height = 180,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = title,
        };

        var okBtn = new Button
        {
            Content = CoreTools.Translate("OK"),
            MinWidth = 80,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        okBtn.Classes.Add("accent");
        okBtn.Click += (_, _) => dialog.Close();

        var root = new Grid
        {
            Margin = new global::Avalonia.Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 12,
        };
        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
        };
        var msgBlock = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
        };
        Grid.SetRow(titleBlock, 0);
        Grid.SetRow(msgBlock, 1);
        Grid.SetRow(okBtn, 2);
        root.Children.Add(titleBlock);
        root.Children.Add(msgBlock);
        root.Children.Add(okBtn);
        dialog.Content = root;

        await dialog.ShowDialog(owner);
    }
}
