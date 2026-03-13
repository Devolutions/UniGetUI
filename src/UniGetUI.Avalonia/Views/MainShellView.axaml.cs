using System.IO;
using System.Text.RegularExpressions;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Controls.ApplicationLifetimes;
using global::Avalonia.Input;
using global::Avalonia.Interactivity;
using global::Avalonia.Markup.Xaml;
using global::Avalonia.Threading;
using global::Avalonia.VisualTree;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Models;
using UniGetUI.Avalonia.Views.Pages;
using UniGetUI.Avalonia.Views.Pages.ManagersPages;
using UniGetUI.Avalonia.Views.Pages.SettingsPages;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Telemetry;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Views;

public partial class MainShellView : UserControl
{
    private readonly Dictionary<ShellPageType, IShellPage> _pageCache = [];
    private readonly Dictionary<ShellPageType, (Button Button, TextBlock Label)> _navButtons = [];
    private readonly List<ShellPageType> _history = [];

    private ShellPageType _currentPage;
    private bool _navigationExpanded = !Settings.Get(Settings.K.CollapseNavMenuOnWideScreen);
    private ContextMenu? _searchSuggestionsMenu;
    private bool _isApplyingSearchSuggestion;

    private Border Sidebar => GetControl<Border>("SidebarBorder");

    private TextBlock ShellSubtitleText => GetControl<TextBlock>("ShellSubtitleBlock");

    private Button DiscoverNavButton => GetControl<Button>("DiscoverButton");

    private TextBlock DiscoverNavLabel => GetControl<TextBlock>("DiscoverLabel");

    private Button UpdatesNavButton => GetControl<Button>("UpdatesButton");

    private TextBlock UpdatesNavLabel => GetControl<TextBlock>("UpdatesLabel");

    private Button InstalledNavButton => GetControl<Button>("InstalledButton");

    private TextBlock InstalledNavLabel => GetControl<TextBlock>("InstalledLabel");

    private Button BundlesNavButton => GetControl<Button>("BundlesButton");

    private TextBlock BundlesNavLabel => GetControl<TextBlock>("BundlesLabel");

    private Button SettingsNavButton => GetControl<Button>("SettingsButton");

    private TextBlock SettingsNavLabel => GetControl<TextBlock>("SettingsLabel");

    private Button ManagersNavButton => GetControl<Button>("ManagersButton");

    private TextBlock ManagersNavLabel => GetControl<TextBlock>("ManagersLabel");

    private Button HelpNavButton => GetControl<Button>("HelpButton");

    private TextBlock HelpNavLabel => GetControl<TextBlock>("HelpLabel");

    private Button LogsNavButton => GetControl<Button>("LogsButton");

    private TextBlock LogsNavLabel => GetControl<TextBlock>("LogsLabel");

    private Button BackNavigationButton => GetControl<Button>("BackButton");

    private TextBlock CurrentPageTitleText => GetControl<TextBlock>("CurrentPageTitleBlock");

    private TextBlock CurrentPageSubtitleText => GetControl<TextBlock>("CurrentPageSubtitleBlock");

    private Button ToggleMaximizeWindowControl => GetControl<Button>("ToggleMaximizeWindowButton");

    private TextBox GlobalSearchHost => GetControl<TextBox>("GlobalSearchBox");

    private TextBox GlobalSearchTextBox => GetControl<TextBox>("GlobalSearchBox");

    private ContentControl PageContentHost => GetControl<ContentControl>("PageHost");

    private Border UpdatesBadgeHost => GetControl<Border>("UpdatesBadge");

    private TextBlock UpdatesBadgeCountText => GetControl<TextBlock>("UpdatesBadgeCount");

    private Border UpdateBannerBorderHost => GetControl<Border>("UpdateBannerBorder");

    private TextBlock UpdateBannerTextBlock => GetControl<TextBlock>("UpdateBannerText");

    private ItemsControl OperationsListControl => GetControl<ItemsControl>("OperationListControl");

    private Border OperationsPanelHost => GetControl<Border>("OperationsPanelBorder");

    private Button OperationsBulkMenuBtn => GetControl<Button>("OperationsBulkMenuButton");

    // Ctrl+Tab / Ctrl+Shift+Tab cycle order (excludes log-like extra pages)
    private static readonly ShellPageType[] _cyclePages =
    [
        ShellPageType.Discover,
        ShellPageType.Updates,
        ShellPageType.Installed,
        ShellPageType.Bundles,
        ShellPageType.Settings,
        ShellPageType.Managers,
    ];

    public MainShellView()
    {
        InitializeComponent();

        ApplyLocalizedShellText();
        ShellSubtitleText.Text = BuildShellSubtitle();
        RegisterNavigationButtons();
        ApplyNavigationWidth();
        NavigateTo(GetDefaultPage(), false);
        AttachedToVisualTree += (_, _) =>
        {
            UpdateWindowButtons();
            AttachKeyboardShortcuts();
            AttachUpdatesBadge();
            AttachOperationsPanel();
            AttachUpdateBanner();
            AttachStartupChecks();
        };
    }

    private void ApplyLocalizedShellText()
    {
        DiscoverNavLabel.Text = CoreTools.Translate("Discover Packages");
        UpdatesNavLabel.Text = CoreTools.Translate("Software Updates");
        InstalledNavLabel.Text = CoreTools.Translate("Installed Packages");
        BundlesNavLabel.Text = CoreTools.Translate("Package Bundles");
        SettingsNavLabel.Text = CoreTools.Translate("Settings");
        ManagersNavLabel.Text = CoreTools.Translate("Package Managers");
        HelpNavLabel.Text = CoreTools.Translate("Help");
        LogsNavLabel.Text = CoreTools.Translate("Package Manager logs");
        GetControl<TextBlock>("ActiveOperationsHeaderBlock").Text = CoreTools.Translate("Operation in progress");
    }

    private void RegisterNavigationButtons()
    {
        _navButtons[ShellPageType.Discover] = (DiscoverNavButton, DiscoverNavLabel);
        _navButtons[ShellPageType.Updates] = (UpdatesNavButton, UpdatesNavLabel);
        _navButtons[ShellPageType.Installed] = (InstalledNavButton, InstalledNavLabel);
        _navButtons[ShellPageType.Bundles] = (BundlesNavButton, BundlesNavLabel);
        _navButtons[ShellPageType.Settings] = (SettingsNavButton, SettingsNavLabel);
        _navButtons[ShellPageType.Managers] = (ManagersNavButton, ManagersNavLabel);
        _navButtons[ShellPageType.Help] = (HelpNavButton, HelpNavLabel);
        _navButtons[ShellPageType.Logs] = (LogsNavButton, LogsNavLabel);
    }

    private static string BuildShellSubtitle()
    {
        var labels = new List<string>();

        if (!string.IsNullOrWhiteSpace(CoreData.VersionName))
        {
            labels.Add(CoreTools.Translate("version {0}", CoreData.VersionName));
        }

#if DEBUG
        labels.Add(CoreTools.Translate("DEBUG BUILD"));
#endif

        return string.Join("  •  ", labels);
    }

    private static ShellPageType GetDefaultPage()
    {
        return Settings.GetValue(Settings.K.StartupPage) switch
        {
            "updates" => ShellPageType.Updates,
            "installed" => ShellPageType.Installed,
            "bundles" => ShellPageType.Bundles,
            "settings" => ShellPageType.Settings,
            _ => ShellPageType.Discover,
        };
    }

    private void NavigateTo(ShellPageType pageType, bool toHistory = true)
    {
        if (_currentPage == pageType && PageContentHost.Content is not null)
        {
            return;
        }

        if (toHistory && PageContentHost.Content is not null)
        {
            _history.Add(_currentPage);
        }

        IShellPage page;

        try
        {
            page = GetPage(pageType);
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to create shell page {pageType}");
            Logger.Error(ex);
            ShowNavigationError(pageType, ex);
            return;
        }

        _currentPage = pageType;
        PageContentHost.Content = (Control)page;
        CurrentPageTitleText.Text = page.Title;
        CurrentPageSubtitleText.Text = page.Subtitle;
        GlobalSearchHost.IsVisible = page.SupportsSearch;
        GlobalSearchTextBox.IsEnabled = page.SupportsSearch;
        GlobalSearchTextBox.Watermark = page.SearchPlaceholder;

        if (!page.SupportsSearch)
        {
            GlobalSearchTextBox.Text = string.Empty;
        }

        page.UpdateSearchQuery(GlobalSearchTextBox.Text ?? string.Empty);
        UpdateSearchSuggestions();

        BackNavigationButton.IsVisible = _history.Count > 0;
        UpdateNavigationSelection();
    }

    private void ShowNavigationError(ShellPageType pageType, Exception ex)
    {
        _currentPage = pageType;
        PageContentHost.Content = new ErrorView(
            CoreTools.Translate("Something went wrong"),
            ex.Message
        );
        CurrentPageTitleText.Text = CoreTools.Translate("Error");
        CurrentPageSubtitleText.Text = CoreTools.Translate("An unexpected error occurred:");
        GlobalSearchHost.IsVisible = false;
        GlobalSearchTextBox.IsEnabled = false;
        GlobalSearchTextBox.Watermark = string.Empty;
        GlobalSearchTextBox.Text = string.Empty;
        BackNavigationButton.IsVisible = _history.Count > 0;
        UpdateNavigationSelection();
    }

    private IShellPage GetPage(ShellPageType pageType)
    {
        if (_pageCache.TryGetValue(pageType, out var existingPage))
        {
            return existingPage;
        }

        IShellPage page = pageType switch
        {
            ShellPageType.Discover => new PackagePageView(
                title: CoreTools.Translate("Discover Packages"),
                subtitle: CoreTools.Translate("Search for packages"),
                searchPlaceholder: CoreTools.Translate("Search for packages"),
                primaryActionLabel: CoreTools.Translate("Install selection"),
                pageGlyph: "◎",
                emptyStateTitle: CoreTools.Translate("Search for packages"),
                emptyStateDescription: CoreTools.Translate("Search for packages to start"),
                showUpgradeColumn: false,
                filtersTitle: CoreTools.Translate("Search mode"),
                pageMode: PackagePageMode.Discover
            ),
            ShellPageType.Updates => new PackagePageView(
                title: CoreTools.Translate("Software Updates"),
                subtitle: CoreTools.Translate("Search on available updates"),
                searchPlaceholder: CoreTools.Translate("Search on available updates"),
                primaryActionLabel: CoreTools.Translate("Update selection"),
                pageGlyph: "↻",
                emptyStateTitle: CoreTools.Translate("No packages were found"),
                emptyStateDescription: CoreTools.Translate("No updates are available"),
                showUpgradeColumn: true,
                filtersTitle: CoreTools.Translate("Search mode"),
                pageMode: PackagePageMode.Updates
            ),
            ShellPageType.Installed => new PackagePageView(
                title: CoreTools.Translate("Installed Packages"),
                subtitle: CoreTools.Translate("Search on your software"),
                searchPlaceholder: CoreTools.Translate("Search on your software"),
                primaryActionLabel: CoreTools.Translate("Uninstall selection"),
                pageGlyph: "▣",
                emptyStateTitle: CoreTools.Translate("No packages were found"),
                emptyStateDescription: CoreTools.Translate("No packages were found"),
                showUpgradeColumn: false,
                filtersTitle: CoreTools.Translate("Search mode"),
                pageMode: PackagePageMode.Installed
            ),
            ShellPageType.Bundles => new BundlesPageView(),
            ShellPageType.Settings => new SettingsPageView(),
            ShellPageType.Managers => new ManagersPageView(),
            ShellPageType.Help => new HelpPageView(),
            ShellPageType.Logs => new LogsPageView(),
            _ => throw new InvalidOperationException($"Unsupported page type {pageType}"),
        };

        _pageCache[pageType] = page;
        return page;
    }

    private void UpdateNavigationSelection()
    {
        foreach (var (pageType, nav) in _navButtons)
        {
            nav.Button.Classes.Set("selected", pageType == _currentPage);
        }
    }

    private void ApplyNavigationWidth()
    {
        Sidebar.Width = _navigationExpanded ? 232 : 72;

        foreach (var nav in _navButtons.Values)
        {
            nav.Label.IsVisible = _navigationExpanded;
        }

        ShellSubtitleText.IsVisible = _navigationExpanded;
    }

    public void SetNavigationCollapsedPreference(bool collapseNavigation)
    {
        _navigationExpanded = !collapseNavigation;
        ApplyNavigationWidth();
    }

    internal void OpenPage(ShellPageType pageType) => NavigateTo(pageType);

    internal void OpenSettingsSection(SettingsSectionRoute route)
    {
        NavigateTo(ShellPageType.Settings);
        if (_pageCache.TryGetValue(ShellPageType.Settings, out var page)
            && page is SettingsPageView settingsPage)
        {
            settingsPage.OpenSection(route);
        }
    }

    internal void OpenManagerLogs(IPackageManager manager, bool verbose = false)
    {
        NavigateTo(ShellPageType.Logs);
        if (_pageCache.TryGetValue(ShellPageType.Logs, out var page)
            && page is LogsPageView logsPage)
        {
            logsPage.ShowManagerLogs(manager, verbose);
        }
    }

    internal async Task OpenBundleFromFileAsync(string filePath)
    {
        NavigateTo(ShellPageType.Bundles);
        if (_pageCache.TryGetValue(ShellPageType.Bundles, out var page)
            && page is BundlesPageView bundlesPage)
        {
            await bundlesPage.OpenFromFileAsync(filePath);
        }
    }

    internal void OpenSharedPackage(string id, string combinedSourceName)
    {
        string[] contents = combinedSourceName.Split(':', 2);
        string managerName = contents[0];
        string sourceName = contents.Length > 1 ? contents[1] : string.Empty;
        _ = ShowSharedPackageAsync(id, managerName, sourceName, eventSource: "LEGACY_COMBINEDSOURCE");
    }

    internal void OpenSharedPackage(string id, string managerName, string sourceName)
    {
        _ = ShowSharedPackageAsync(id, managerName, sourceName, eventSource: "DEFAULT");
    }

    private void ToggleNavigationButton_OnClick(object? sender, RoutedEventArgs e)
    {
        _navigationExpanded = !_navigationExpanded;
        ApplyNavigationWidth();
    }

    private void BackButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_history.Count == 0)
        {
            return;
        }

        var target = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        NavigateTo(target, false);
    }

    private void GlobalSearchBox_OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_isApplyingSearchSuggestion)
        {
            return;
        }

        if (_pageCache.TryGetValue(_currentPage, out var page) && page.SupportsSearch)
        {
            page.UpdateSearchQuery(GlobalSearchTextBox.Text ?? string.Empty);
        }

        UpdateSearchSuggestions();
    }

    private void UpdateSearchSuggestions()
    {
        if (!_pageCache.TryGetValue(_currentPage, out var page) || !page.SupportsSearch)
        {
            CloseSearchSuggestions();
            return;
        }

        string query = GlobalSearchTextBox.Text?.Trim() ?? string.Empty;
        if (query.Length < 2)
        {
            CloseSearchSuggestions();
            return;
        }

        var suggestions = GetSearchSuggestions(query).ToArray();
        if (suggestions.Length == 0)
        {
            CloseSearchSuggestions();
            return;
        }

        CloseSearchSuggestions();

        var menu = new ContextMenu
        {
            PlacementTarget = GlobalSearchTextBox,
            Placement = PlacementMode.Bottom,
        };

        foreach (string suggestion in suggestions)
        {
            var item = new MenuItem { Header = suggestion };
            item.Click += (_, _) => ApplySearchSuggestion(suggestion);
            menu.Items.Add(item);
        }

        _searchSuggestionsMenu = menu;
        GlobalSearchTextBox.ContextMenu = menu;
        menu.Open(GlobalSearchTextBox);
    }

    private IEnumerable<string> GetSearchSuggestions(string query)
    {
        IEnumerable<IPackage> packages = _currentPage switch
        {
            ShellPageType.Discover => DiscoverablePackagesLoader.Instance.Packages,
            ShellPageType.Updates => UpgradablePackagesLoader.Instance.Packages,
            ShellPageType.Installed => InstalledPackagesLoader.Instance.Packages,
            _ => [],
        };

        return packages
            .SelectMany(package => new[] { package.Name, package.Id })
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(value => value.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(value => value.StartsWith(query, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Take(8);
    }

    private void ApplySearchSuggestion(string suggestion)
    {
        _isApplyingSearchSuggestion = true;
        try
        {
            GlobalSearchTextBox.Text = suggestion;
            GlobalSearchTextBox.CaretIndex = suggestion.Length;

            if (_pageCache.TryGetValue(_currentPage, out var page) && page.SupportsSearch)
            {
                page.UpdateSearchQuery(suggestion);
            }
        }
        finally
        {
            _isApplyingSearchSuggestion = false;
            CloseSearchSuggestions();
        }
    }

    private void CloseSearchSuggestions()
    {
        if (_searchSuggestionsMenu is not null)
        {
            _searchSuggestionsMenu.Close();
            if (ReferenceEquals(GlobalSearchTextBox.ContextMenu, _searchSuggestionsMenu))
            {
                GlobalSearchTextBox.ContextMenu = null;
            }
            _searchSuggestionsMenu = null;
        }
    }

    private void TitleRegion_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || IsInteractiveSource(e.Source))
        {
            return;
        }

        if (GetHostWindow() is { } window)
        {
            window.BeginMoveDrag(e);
        }
    }

    private void TitleRegion_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsInteractiveSource(e.Source) || GetHostWindow() is not { CanResize: true } window)
        {
            return;
        }

        ToggleWindowState(window);
    }

    private void MinimizeWindowButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (GetHostWindow() is { } window)
        {
            window.WindowState = WindowState.Minimized;
            UpdateWindowButtons();
        }
    }

    private void ToggleMaximizeWindowButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (GetHostWindow() is { } window)
        {
            ToggleWindowState(window);
        }
    }

    private void CloseWindowButton_OnClick(object? sender, RoutedEventArgs e)
    {
        GetHostWindow()?.Close();
    }

    private void ToggleWindowState(Window window)
    {
        window.WindowState = window.WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

        UpdateWindowButtons();
    }

    private void UpdateWindowButtons()
    {
        if (GetHostWindow() is not { } window)
        {
            return;
        }

        ToggleMaximizeWindowControl.Content = window.WindowState == WindowState.Maximized ? "❐" : "□";
    }

    private Window? GetHostWindow()
    {
        return TopLevel.GetTopLevel(this) as Window;
    }

    private static bool IsInteractiveSource(object? source)
    {
        if (source is not global::Avalonia.Visual visual)
        {
            return false;
        }

        return visual.GetSelfAndVisualAncestors().Any(control => control is Button || control is TextBox);
    }

    private void DiscoverButton_OnClick(object? sender, RoutedEventArgs e) => NavigateTo(ShellPageType.Discover);

    private void UpdatesButton_OnClick(object? sender, RoutedEventArgs e) => NavigateTo(ShellPageType.Updates);

    private void InstalledButton_OnClick(object? sender, RoutedEventArgs e) => NavigateTo(ShellPageType.Installed);

    private void BundlesButton_OnClick(object? sender, RoutedEventArgs e) => NavigateTo(ShellPageType.Bundles);

    private void SettingsButton_OnClick(object? sender, RoutedEventArgs e) => NavigateTo(ShellPageType.Settings);

    private void ManagersButton_OnClick(object? sender, RoutedEventArgs e) => NavigateTo(ShellPageType.Managers);

    private void HelpButton_OnClick(object? sender, RoutedEventArgs e) => NavigateTo(ShellPageType.Help);

    private void LogsButton_OnClick(object? sender, RoutedEventArgs e) => NavigateTo(ShellPageType.Logs);

    // ── Keyboard shortcuts ────────────────────────────────────────────────────

    private void AttachKeyboardShortcuts()
    {
        if (TopLevel.GetTopLevel(this) is not { } topLevel) return;
        topLevel.AddHandler(InputElement.KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl && e.Key == Key.Tab)
        {
            e.Handled = true;
            NavigateCycle(shift ? -1 : +1);
        }
        else if (e.Key == Key.F1 && !ctrl && !shift)
        {
            e.Handled = true;
            NavigateTo(ShellPageType.Help);
        }
        else if (ctrl && !shift && (e.Key == Key.Q || e.Key == Key.W))
        {
            GetHostWindow()?.Close();
            e.Handled = true;
        }
        else if (!ctrl && !shift && e.Key == Key.F5 || ctrl && !shift && e.Key == Key.R)
        {
            // Reload: re-create the current page by evicting the cache
            if (_pageCache.Remove(_currentPage))
            {
                var current = _currentPage;
                _currentPage = default;
                NavigateTo(current, false);
            }
            e.Handled = true;
        }
        else if (ctrl && !shift && e.Key == Key.F)
        {
            if (GlobalSearchHost.IsVisible)
            {
                GlobalSearchHost.Focus();
                e.Handled = true;
            }
        }
        else if (ctrl && !shift && e.Key == Key.A)
        {
            if (_pageCache.TryGetValue(_currentPage, out var page) && page is PackagePageView ppv)
            {
                ppv.TriggerSelectAll();
                e.Handled = true;
            }
        }
    }

    private void NavigateCycle(int direction)
    {
        int idx = Array.IndexOf(_cyclePages, _currentPage);
        if (idx < 0) idx = 0;
        int next = ((idx + direction) % _cyclePages.Length + _cyclePages.Length) % _cyclePages.Length;
        NavigateTo(_cyclePages[next]);
    }

    // ── Updates badge ─────────────────────────────────────────────────────────

    private void AttachUpdatesBadge()
    {
        UpgradablePackagesLoader.Instance.PackagesChanged += OnUpgradablePackagesChanged;
        OnUpgradablePackagesChanged(null, default!);
    }

    private void OnUpgradablePackagesChanged(object? sender, PackagesChangedEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            int count = UpgradablePackagesLoader.Instance.Packages.Count;
            UpdatesBadgeHost.IsVisible = count > 0;
            UpdatesBadgeCountText.Text = count > 99 ? "99+" : count.ToString();

            // Refresh tray tooltip whenever the update count changes
            if (Application.Current?.ApplicationLifetime
                    is IClassicDesktopStyleApplicationLifetime { MainWindow: UniGetUI.Avalonia.MainWindow mw })
                mw.UpdateSystemTrayStatus();
        });
    }

    // ── Operations panel ──────────────────────────────────────────────────────

    private void AttachOperationsPanel()
    {
        OperationsListControl.ItemsSource = AvaloniaOperationRegistry.Operations;
        AvaloniaOperationRegistry.Operations.CollectionChanged += (_, _) =>
            Dispatcher.UIThread.Post(UpdateOperationsPanelVisibility);
        UpdateOperationsPanelVisibility();
        OperationsBulkMenuBtn.Click += OperationsBulkMenuBtn_OnClick;
    }

    private void OperationsBulkMenuBtn_OnClick(object? sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            Items =
            {
                new MenuItem
                {
                    Header = CoreTools.Translate("Retry failed operations"),
                    Command = new RelayCommand(RetryFailedOps)
                },
                new MenuItem
                {
                    Header = CoreTools.Translate("Clear successful operations"),
                    Command = new RelayCommand(ClearSuccessfulOps)
                },
                new MenuItem
                {
                    Header = CoreTools.Translate("Clear finished operations"),
                    Command = new RelayCommand(ClearFinishedOps)
                },
                new Separator(),
                new MenuItem
                {
                    Header = CoreTools.Translate("Cancel all operations"),
                    Command = new RelayCommand(CancelAllOps)
                }
            }
        };
        menu.Open(OperationsBulkMenuBtn);
    }

    private void CancelAllOps()
    {
        foreach (var op in AvaloniaOperationRegistry.Operations.ToList())
        {
            if (op.Status is OperationStatus.InQueue or OperationStatus.Running)
                op.Cancel();
        }
    }

    private void RetryFailedOps()
    {
        foreach (var op in AvaloniaOperationRegistry.Operations.ToList())
        {
            if (op.Status == OperationStatus.Failed)
                op.Retry(AbstractOperation.RetryMode.Retry);
        }
    }

    private void ClearSuccessfulOps()
    {
        foreach (var op in AvaloniaOperationRegistry.Operations.ToList())
        {
            if (op.Status == OperationStatus.Succeeded)
                AvaloniaOperationRegistry.Operations.Remove(op);
        }
    }

    private void ClearFinishedOps()
    {
        foreach (var op in AvaloniaOperationRegistry.Operations.ToList())
        {
            if (op.Status is OperationStatus.Succeeded or OperationStatus.Failed or OperationStatus.Canceled)
                AvaloniaOperationRegistry.Operations.Remove(op);
        }
    }

    private void UpdateOperationsPanelVisibility()
    {
        OperationsPanelHost.IsVisible = AvaloniaOperationRegistry.Operations.Count > 0;
    }

    // ── Auto-update banner ────────────────────────────────────────────────────

    private void AttachUpdateBanner()
    {
        AvaloniaAutoUpdater.UpdateAvailable += OnUpdateAvailable;
    }

    private void OnUpdateAvailable(string versionName)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateBannerTextBlock.Text = CoreTools.Translate(
                "UniGetUI {0} is ready to be installed. Click \"Update now\" to restart and update.",
                versionName
            );
            UpdateBannerBorderHost.IsVisible = true;
        });
    }

    private void UpdateBannerButton_OnClick(object? sender, RoutedEventArgs e)
    {
        AvaloniaAutoUpdater.TriggerInstall();
        UpdateBannerBorderHost.IsVisible = false;
    }

    private void UpdateBannerDismissButton_OnClick(object? sender, RoutedEventArgs e)
    {
        UpdateBannerBorderHost.IsVisible = false;
    }

    // ── Startup checks ────────────────────────────────────────────────────────

    private void AttachStartupChecks()
    {
        if (CoreTools.IsAdministrator() && !Settings.Get(Settings.K.AlreadyWarnedAboutAdmin))
        {
            Settings.Set(Settings.K.AlreadyWarnedAboutAdmin, true);
            _ = ShowStartupDialogAsync(new AdminWarningWindow());
        }

        if (!Settings.Get(Settings.K.ShownTelemetryBanner))
        {
            _ = ShowStartupDialogAsync(new TelemetryConsentWindow());
        }

        ProcessStartupArgs();
    }

    /// <summary>
    /// Called by <c>Program.OnIncomingArgs</c> when a second instance forwards its
    /// command-line arguments via the named pipe.  Must be invoked on the UI thread.
    /// </summary>
    public void ProcessIncomingArgs(string[] args) => ProcessArgs(args);

    private void ProcessStartupArgs() => ProcessArgs(Environment.GetCommandLineArgs().Skip(1));

    private void ProcessArgs(IEnumerable<string> rawArgs)
    {
        foreach (var rawArg in rawArgs)
        {
            string arg = rawArg.Trim('\'').Trim('"');
            if (string.IsNullOrWhiteSpace(arg)) continue;

            if (arg.StartsWith("--"))
            {
                if (arg == "--help")
                    NavigateTo(ShellPageType.Help);
                else if (arg == "--updateapps")
                    _ = AvaloniaPackageOperationHelper.UpdateAllAsync();
                // --daemon and other startup-only flags are handled in MainWindow
            }
            else if (arg.StartsWith("unigetui://", StringComparison.OrdinalIgnoreCase))
            {
                HandleDeepLink(arg);
            }
            else if (Path.IsPathFullyQualified(arg) && File.Exists(arg))
            {
                string ext = Path.GetExtension(arg).ToLowerInvariant();
                if (ext is ".ubundle" or ".json" or ".xml" or ".yaml")
                {
                    NavigateTo(ShellPageType.Bundles);
                    if (_pageCache.TryGetValue(ShellPageType.Bundles, out var page)
                        && page is BundlesPageView bpv)
                    {
                        _ = bpv.OpenFromFileAsync(arg);
                    }
                }
                else
                {
                    Logger.Warn($"Attempted to open unrecognized file: {arg}");
                }
            }
        }
    }

    private void HandleDeepLink(string link)
    {
        try
        {
            string baseUrl = Uri.UnescapeDataString(link["unigetui://".Length..]);

            if (baseUrl.StartsWith("showPackage", StringComparison.OrdinalIgnoreCase))
            {
                string id = TryGetDeepLinkParam(baseUrl, "id");
                string combinedManagerName = TryGetDeepLinkParam(baseUrl, "combinedManagerName");
                string managerName = TryGetDeepLinkParam(baseUrl, "managerName");
                string sourceName = TryGetDeepLinkParam(baseUrl, "sourceName");

                if (id.Length > 0 && combinedManagerName.Length > 0
                    && managerName.Length == 0 && sourceName.Length == 0)
                {
                    Logger.Warn($"URI {link} follows old showPackage scheme");
                    OpenSharedPackage(id, combinedManagerName);
                }
                else if (id.Length > 0 && managerName.Length > 0 && sourceName.Length > 0)
                {
                    OpenSharedPackage(id, managerName, sourceName);
                }
                else
                {
                    throw new UriFormatException($"Malformed showPackage deep link: {link}");
                }
            }
            else if (baseUrl.StartsWith("showDiscoverPage", StringComparison.OrdinalIgnoreCase))
            {
                NavigateTo(ShellPageType.Discover);
            }
            else if (baseUrl.StartsWith("showUpdatesPage", StringComparison.OrdinalIgnoreCase))
            {
                NavigateTo(ShellPageType.Updates);
            }
            else if (baseUrl.StartsWith("showInstalledPage", StringComparison.OrdinalIgnoreCase))
            {
                NavigateTo(ShellPageType.Installed);
            }
            else
            {
                Logger.Warn($"Unhandled deep link: {link}");
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to handle deep link: {link}");
            Logger.Error(ex);
        }
    }

    private static string TryGetDeepLinkParam(string baseUrl, string parameterName)
    {
        var match = Regex.Match(
            baseUrl,
            parameterName + "=([^&]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
        );

        return match.Success ? Uri.UnescapeDataString(match.Groups[1].Value) : string.Empty;
    }

    private async Task ShowSharedPackageAsync(
        string id,
        string managerName,
        string sourceName,
        string eventSource
    )
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(managerName))
        {
            Logger.Warn("ShowSharedPackage requested with incomplete parameters.");
            return;
        }

        try
        {
            NavigateTo(ShellPageType.Discover);
            MainWindow.Instance?.ShowFromTray();

            var findResult = await Task.Run(() =>
                DiscoverablePackagesLoader.Instance.GetPackageFromIdAndManager(
                    id,
                    managerName,
                    sourceName
                )
            );

            if (findResult.Item1 is null)
            {
                throw new KeyNotFoundException(findResult.Item2 ?? CoreTools.Translate("An unexpected error occurred:"));
            }

            TelemetryHandler.SharedPackage(findResult.Item1, eventSource);

            var detailsWindow = new PackageDetailsWindow(findResult.Item1, PackagePageMode.Discover);
            if (GetHostWindow() is { } owner)
            {
                await detailsWindow.ShowDialog(owner);
            }
            else
            {
                detailsWindow.Show();
            }
        }
        catch (Exception ex)
        {
            Logger.Error($"An error occurred while attempting to show the package with id {id}");
            Logger.Error(ex);

            await ShowSimpleDialogAsync(
                CoreTools.Translate("Package not found"),
                CoreTools.Translate(
                    "An error occurred when attempting to show the package with Id {0}",
                    id
                ) + "\n" + ex.Message
            );
        }
    }

    private async Task ShowSimpleDialogAsync(string title, string message)
    {
        if (GetHostWindow() is not { } owner)
        {
            return;
        }

        var dialog = new Window
        {
            Width = 520,
            Height = 220,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = title,
            Content = new StackPanel
            {
                Spacing = 14,
                Margin = new Thickness(18),
                Children =
                {
                    new TextBlock
                    {
                        Text = message,
                        TextWrapping = global::Avalonia.Media.TextWrapping.Wrap,
                    },
                    new Button
                    {
                        Content = CoreTools.Translate("OK"),
                        HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right,
                        MinWidth = 100,
                    },
                },
            },
        };

        if (dialog.Content is StackPanel sp && sp.Children.LastOrDefault() is Button okButton)
        {
            okButton.Click += (_, _) => dialog.Close();
        }

        await dialog.ShowDialog(owner);
    }

    private async Task ShowStartupDialogAsync(Window dialog)
    {
        if (GetHostWindow() is { } owner)
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
        }
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private T GetControl<T>(string name)
        where T : Control
    {
        return this.FindControl<T>(name)
            ?? throw new InvalidOperationException($"Control '{name}' was not found.");
    }
}
