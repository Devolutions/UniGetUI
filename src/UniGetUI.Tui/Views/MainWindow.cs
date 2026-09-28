using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Core.Tools.Scheduling;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.Tui.FakeData;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Theme;
using UniGetUI.Tui.Views.Controls;
using UniGetUI.Tui.Views.Dialogs;
using UniGetUI.Tui.Views.Pages;

namespace UniGetUI.Tui.Views;

/// <summary>
/// The TUI's single window: header (with the fake-data badge and live counts), an inline menu bar
/// (F10 / Alt+letter), a one-line page tab strip, the page host, a notification line, the key-hint footer
/// and the overlay layer every dialog is drawn in. Keyboard focus always lives in the page. Built in code (no AXAML).
/// </summary>
internal sealed class MainWindow : Window
{
    private sealed record MenuDefinition(string Header, Func<IReadOnlyList<TuiAction>> Actions);

    private readonly ContentControl _contentHost;
    private readonly StackPanel _tabStrip;
    private readonly TextBlock _headerText;
    private readonly TextBlock _headerStatus;
    private readonly TuiFunctionBar _functionBar;
    private readonly MenuDefinition[] _menuDefinitions;
    private readonly StackPanel _menuBar;
    private readonly Border _menuDropDown;
    private readonly StackPanel _menuDropDownItems;
    private readonly DispatcherTimer _notificationTimer;
    private readonly List<Border> _menuHeaderBorders = [];
    private readonly Dictionary<string, Control> _pageCache = new();
    private readonly Panel _modalLayer;

    private string? _currentPageId;
    private bool _menuActive;
    private IInputElement? _focusBeforeMenu;
    private DispatcherTimer? _focusSettleTimer;
    private bool _userMovedSinceNavigation;
    private int _openMenuIndex = -1;
    private int _selectedMenuActionIndex;
    private IReadOnlyList<TuiAction> _openMenuActions = [];
    private int[] _headerMnemonics = [];
    private bool _startupHandled;

    public MainWindow()
    {
        UpdateTitle();
        _contentHost = new ContentControl
        {
            Margin = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        _tabStrip = new StackPanel { Orientation = Orientation.Horizontal };
        _headerText = new TextBlock { Foreground = TuiPalette.ChromeText, FontWeight = FontWeight.Bold };
        _headerStatus = new TextBlock { Foreground = TuiPalette.ChromeText, HorizontalAlignment = HorizontalAlignment.Right };
        _functionBar = new TuiFunctionBar();
        _menuDefinitions = BuildMenuDefinitions();
        _menuBar = BuildMenuBar();
        _menuDropDownItems = new StackPanel { Spacing = 0 };
        _menuDropDown = new Border
        {
            IsVisible = false,
            Background = TuiPalette.DropDown,
            BorderThickness = new Thickness(1),
            BorderBrush = TuiPalette.Border,
            Padding = new Thickness(1, 0, 1, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = _menuDropDownItems,
        };
        _notificationTimer = new DispatcherTimer();
        _notificationTimer.Tick += (_, _) => HideNotification();

        var header = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_headerStatus, Dock.Right);
        header.Children.Add(_headerStatus);
        header.Children.Add(_headerText);
        var headerBorder = new Border { Background = TuiPalette.Chrome, Padding = new Thickness(1, 0, 1, 0), Child = header };
        var menuBorder = new Border { Background = TuiPalette.MenuBar, Child = _menuBar };
        var tabBorder = new Border { Background = TuiPalette.TabBar, ClipToBounds = true, Child = _tabStrip };
        tabBorder.SizeChanged += (_, _) => RefreshTabs();

        var main = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(headerBorder, Dock.Top);
        DockPanel.SetDock(menuBorder, Dock.Top);
        DockPanel.SetDock(_functionBar, Dock.Bottom);
        DockPanel.SetDock(tabBorder, Dock.Top);
        main.Children.Add(headerBorder);
        main.Children.Add(menuBorder);
        main.Children.Add(tabBorder);
        main.Children.Add(_functionBar);
        main.Children.Add(_contentHost);

        // The menu dropdown floats over the page (in-tree, no popup), just below the menu bar.
        var menuLayer = new Canvas { IsHitTestVisible = true };
        Canvas.SetTop(_menuDropDown, 2);
        menuLayer.Children.Add(_menuDropDown);

        _modalLayer = new Panel { IsVisible = false };
        TuiModal.Attach(_modalLayer);

        var root = new Panel();
        root.Children.Add(main);
        root.Children.Add(menuLayer);
        root.Children.Add(_modalLayer);
        Content = root;

        Focusable = true;
        Opened += (_, _) => OnOpened();
        // Developer-only input trace (UNIGETUI_TUI_KEYLOG=<file>): records every key and text event as the
        // terminal delivered it, which is how console-driver key mapping problems are diagnosed.
        if (Environment.GetEnvironmentVariable("UNIGETUI_TUI_KEYLOG") is { Length: > 0 } keyLog)
        {
            AddHandler(KeyDownEvent, (_, e) => File.AppendAllText(keyLog, $"down {e.Key} {e.KeyModifiers} sym={e.KeySymbol} handled={e.Handled}\n"),
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
            AddHandler(TextInputEvent, (_, e) => File.AppendAllText(keyLog, $"text '{e.Text}' handled={e.Handled}\n"),
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        }

        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnWindowKeyDownUnhandled, RoutingStrategies.Bubble);
        AddHandler(TextInputEvent, OnWindowTextInput, RoutingStrategies.Tunnel);
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel);

        TuiShell.NavigationRequested += OnNavigationRequested;
        TuiShell.ChromeInvalidated += OnChromeInvalidated;
        TuiShell.BundleOpenRequested += OnBundleOpenRequested;
        TuiShell.PagesInvalidated += OnPagesInvalidated;
        TuiNotifications.NotificationRaised += OnNotificationRaised;
        TuiPalette.ThemeChanged += OnThemeChanged;
        TuiOperationRegistry.Changed += OnChromeInvalidated;
        TuiBundleService.UnsavedChangesStateChanged += OnChromeInvalidated;
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            TuiShell.NavigationRequested -= OnNavigationRequested;
            TuiShell.ChromeInvalidated -= OnChromeInvalidated;
            TuiShell.BundleOpenRequested -= OnBundleOpenRequested;
            TuiShell.PagesInvalidated -= OnPagesInvalidated;
            TuiNotifications.NotificationRaised -= OnNotificationRaised;
            TuiPalette.ThemeChanged -= OnThemeChanged;
            TuiOperationRegistry.Changed -= OnChromeInvalidated;
            TuiBundleService.UnsavedChangesStateChanged -= OnChromeInvalidated;
            _notificationTimer.Stop();
        };

        RefreshChrome();
    }

    public string CurrentPageId => _currentPageId ?? TuiPageIds.Discover;

    public Control CurrentPage => _contentHost.Content as Control ?? new Panel();

    public T GetPage<T>(string id) where T : Control => (T)GetPage(id);

    /// <summary>Drops every cached page so the next navigation builds it fresh (test isolation).</summary>
    internal void ResetPages()
    {
        _contentHost.Content = null;
        _pageCache.Clear();
        _currentPageId = null;
    }

    // ─── Startup ──────────────────────────────────────────────────────────

    private void OnOpened()
    {
        if (!_startupHandled)
        {
            _startupHandled = true;
            NavigateTo(StartupPage());
            TuiMaintenanceScheduler.Start();
            WatchUpdateLoads();
            _ = RunStartupTasksAsync();
        }

        FocusPageSoon();
    }

    private static string StartupPage()
    {
        if (TuiStartup.Current?.StartupPage is { } requested) return requested;
        if (TuiStartup.Current?.BundleFiles.Count > 0) return TuiPageIds.Bundles;
        string setting = Settings.GetValue(Settings.K.StartupPage);
        if (setting is TuiPageIds.Discover or TuiPageIds.Updates or TuiPageIds.Installed or TuiPageIds.Bundles or TuiPageIds.Settings)
            return setting;
        return UpgradablePackagesLoader.Instance?.Packages.Count > 0 ? TuiPageIds.Updates : TuiPageIds.Discover;
    }

    /// <summary>
    /// Runs the "updates loaded" policy (auto-install, --updateapps, notification) after every update
    /// check, whichever page is showing. The first check may already be done when the window opens.
    /// </summary>
    private void WatchUpdateLoads()
    {
        bool first = true;
        void Handle() => Dispatcher.UIThread.Post(() =>
        {
            bool isFirst = first;
            first = false;
            _ = TuiUpdatesPolicy.OnUpdatesLoadedAsync(isFirst);
        });

        UpgradablePackagesLoader.Instance.FinishedLoading += (_, _) => Handle();
        if (UpgradablePackagesLoader.Instance.IsLoaded && !UpgradablePackagesLoader.Instance.IsLoading) Handle();
    }

    private async Task RunStartupTasksAsync()
    {
        try
        {
            if (TuiStartup.Current?.BundleFiles is { Count: > 0 } bundles)
                await GetPage<PackageListPage>(TuiPageIds.Bundles).OpenBundleFileAsync(bundles[0]);

            if (FakeDataEnvironment.Current is { } env)
                TuiNotifications.Warning(CoreTools.Translate("Fake data mode"),
                    CoreTools.Translate("Nothing is installed. Sandbox: {0}", env.SandboxDirectory), TimeSpan.FromSeconds(6));

            // Desktop parity: warn once when UniGetUI runs elevated.
            if (!TuiEngine.IsFakeData && CoreTools.IsAdministrator() && !Settings.Get(Settings.K.AlreadyWarnedAboutAdmin))
            {
                Settings.Set(Settings.K.AlreadyWarnedAboutAdmin, true);
                await TuiPrompts.InfoAsync(CoreTools.Translate("Administrator privileges"),
                    CoreTools.Translate("UniGetUI has been ran as administrator, which is not recommended. When running UniGetUI as administrator, EVERY operation launched from UniGetUI will have administrator privileges. You can still use the program, but we highly recommend not running UniGetUI with administrator privileges."));
            }

            // Desktop parity: a ready WinGet that lists no installed package at all has most likely malfunctioned.
            var installed = InstalledPackagesLoader.Instance;
            if (installed.IsLoading) await installed.WaitForCurrentLoadAsync();
            if (!Settings.Get(Settings.K.DisableWinGetMalfunctionDetector)
                && TuiEngine.FindManager("Winget") is { } winget && winget.IsReady()
                && installed.Packages.All(p => p.Manager != winget))
                TuiNotifications.Warning(CoreTools.Translate("WinGet malfunction detected"),
                    CoreTools.Translate("It looks like WinGet is not working properly. Do you want to attempt to repair WinGet?")
                    + " " + CoreTools.Translate("Managers → WinGet settings → Reset WinGet"), TimeSpan.FromSeconds(12));

            foreach (MaintenanceTaskKind kind in new[] { MaintenanceTaskKind.LocalBackup, MaintenanceTaskKind.CheckForUpdates })
            {
                if (!TuiMaintenanceScheduler.ShouldRunAtAppStart(kind)) continue;
                if (kind is MaintenanceTaskKind.LocalBackup)
                {
                    var loader = InstalledPackagesLoader.Instance;
                    if (loader.IsLoading) await loader.WaitForCurrentLoadAsync();
                }

                await TuiMaintenanceScheduler.RunAsync(kind);
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    // ─── Navigation ───────────────────────────────────────────────────────

    public void NavigateTo(string pageId)
    {
        if (Array.IndexOf(TuiPageIds.All, pageId) < 0) return;
        // Help and About are dialogs over the current page, not pages (`--page help` / `--page about` open them over
        // the default page).
        if (pageId is TuiPageIds.Help or TuiPageIds.About)
        {
            if (_currentPageId is null) NavigateTo(TuiPageIds.Discover);
            _ = pageId == TuiPageIds.Help ? HelpDialog.ShowAsync() : AboutDialog.ShowAsync();
            return;
        }

        _currentPageId = pageId;
        Control page = GetPage(pageId);
        _contentHost.Content = page;
        (page as ITuiPage)?.OnShown();
        RefreshChrome();
        FocusPageSoon();
    }

    private void OnNavigationRequested(string pageId) => Dispatcher.UIThread.Post(() => NavigateTo(pageId));

    private void OnBundleOpenRequested()
        => Dispatcher.UIThread.Post(() => _ = GetPage<PackageListPage>(TuiPageIds.Bundles).OpenBundleAsync(), DispatcherPriority.Background);

    private void OnPagesInvalidated(string[] pageIds) => Dispatcher.UIThread.Post(() =>
    {
        foreach (string id in pageIds)
            if (id != CurrentPageId) _pageCache.Remove(id);
    });

    private Control GetPage(string id)
    {
        if (_pageCache.TryGetValue(id, out Control? cached)) return cached;
        Control page = TuiPages.Build(id);
        _pageCache[id] = page;
        return page;
    }

    /// <summary>The pages, each with a numbered tab (Help and About are dialogs, opened from the Help menu and F1).</summary>
    private static readonly string[] TabPages = TuiPageIds.All.Where(id => id is not (TuiPageIds.Help or TuiPageIds.About)).ToArray();

    private enum TabDensity
    {
        Full,
        Short,
        Shorter,
        NumberOnly,
    }

    /// <summary>Tab text: "2 Updates (13)"; narrower terminals shorten the tabs that are not selected.</summary>
    private static string TabLabel(string id, int index, TabDensity density)
    {
        string badge = id switch
        {
            TuiPageIds.Updates => SafeCount(() => UpgradablePackagesLoader.Instance.Packages.Count) is > 0 and var n ? $" ({n})" : "",
            TuiPageIds.Operations => TuiOperationRegistry.ActiveCount is > 0 and var a ? $" ({a})"
                : TuiOperationRegistry.ErrorsOccurred > 0 ? " (!)" : "",
            TuiPageIds.Bundles => TuiBundleService.HasUnsavedChanges ? " *" : "",
            _ => "",
        };
        string key = index >= 0 ? $"{index + 1} " : "";
        string label = TuiPages.Label(id);
        return density switch
        {
            TabDensity.Full => key + label + badge,
            TabDensity.Short => key + (label.Length > 4 ? label[..4] : label) + badge,
            TabDensity.Shorter => key + (label.Length > 3 ? label[..3] : label) + badge,
            _ => key.TrimEnd() + badge,
        };
    }

    private void RefreshTabs()
    {
        List<(string Id, int Index)> tabs = TabPages.Select((id, i) => (id, i)).ToList();

        double available = (_tabStrip.Parent as Control)?.Bounds.Width ?? 0;
        TabDensity density = TabDensity.Full;
        foreach (TabDensity candidate in Enum.GetValues<TabDensity>())
        {
            density = candidate;
            int width = tabs.Sum(t => TabLabel(t.Id, t.Index, t.Id == _currentPageId ? TabDensity.Full : candidate).Length + 2);
            if (available <= 0 || width <= available) break;
        }

        while (_tabStrip.Children.Count > tabs.Count) _tabStrip.Children.RemoveAt(_tabStrip.Children.Count - 1);
        for (int i = 0; i < tabs.Count; i++)
        {
            (string id, int index) = tabs[i];
            bool active = id == _currentPageId;
            if (i >= _tabStrip.Children.Count)
            {
                var tab = new Border { Child = new TextBlock() };
                tab.PointerPressed += (sender, e) =>
                {
                    if ((sender as Border)?.Tag is not string target) return;
                    e.Handled = true;
                    CloseMenu();
                    NavigateTo(target);
                };
                _tabStrip.Children.Add(tab);
            }

            var border = (Border)_tabStrip.Children[i];
            var text = (TextBlock)border.Child!;
            border.Tag = id;
            border.Background = active ? TuiPalette.Focus : Brushes.Transparent;
            text.Text = " " + TabLabel(id, index, active ? TabDensity.Full : density) + " ";
            text.Foreground = active ? TuiPalette.FocusText : TuiPalette.TextMuted;
            text.FontWeight = active ? FontWeight.Bold : FontWeight.Normal;
        }
    }

    private static int SafeCount(Func<int> getter)
    {
        try
        {
            return getter();
        }
        catch
        {
            return 0;
        }
    }

    private void OnChromeInvalidated() => Dispatcher.UIThread.Post(RefreshChrome, DispatcherPriority.Background);

    /// <summary>
    /// The palette brushes recolour in place; this repaints everything (including cached pages that are
    /// not showing) so no control keeps a stale frame, and re-renders the menu, whose rows are rebuilt.
    /// </summary>
    private void OnThemeChanged()
    {
        RenderMenu();
        RefreshChrome();
        foreach (Visual visual in this.GetVisualDescendants().Prepend(this).Concat(_pageCache.Values.SelectMany(p => p.GetVisualDescendants().Prepend(p))))
            visual.InvalidateVisual();
    }

    private void RefreshChrome()
    {
        RefreshTabs();
        string version = Settings.Get(Settings.K.ShowVersionNumberOnTitlebar) ? $"  v{CoreData.VersionName}" : "";
        _headerText.Text = $" UniGetUI · Terminal UI{version}" + (TuiEngine.IsFakeData ? "   [FAKE DATA]" : "");
        int updates = SafeCount(() => UpgradablePackagesLoader.Instance.Packages.Count);
        int active = TuiOperationRegistry.ActiveCount;
        bool loading = SafeCount(() => (InstalledPackagesLoader.Instance.IsLoading || UpgradablePackagesLoader.Instance.IsLoading) ? 1 : 0) == 1;
        _headerStatus.Text = (loading ? CoreTools.Translate("Loading packages") + "…  " : "")
                             + (active > 0 ? CoreTools.Translate("{0} operations", active) + "  " : "")
                             + (updates > 0 ? CoreTools.Translate("{0} updates", updates) : CoreTools.Translate("Everything is up to date"));
        // Global keys first (always in the same place), then the page's keys when the page does not show them.
        _functionBar.SetKeys([.. GlobalKeyHints, .. ((CurrentPage as ITuiPage)?.KeyHints ?? []).Select(h => h with { Priority = 10 + h.Priority })]);
        _functionBar.SetBadges(TuiEngine.IsFakeData ? [CoreTools.Translate("FAKE DATA")] : []);
        UpdateTitle();
    }

    /// <summary>The window-wide function keys, in display order; the priority decides what a narrow bar drops first.</summary>
    private static IReadOnlyList<TuiKeyHint> GlobalKeyHints =>
    [
        new("F1", CoreTools.Translate("Help"), 0),
        new("F5", CoreTools.Translate("Reload"), 4),
        new("F10", CoreTools.Translate("Menu"), 1),
        new("Alt+1-9", CoreTools.Translate("Pages"), 3),
        new("Ctrl+Q", CoreTools.Translate("Quit"), 2),
    ];

    private void UpdateTitle()
    {
        Title = Settings.Get(Settings.K.ShowVersionNumberOnTitlebar)
            ? $"UniGetUI TUI v{CoreData.VersionName} (build {CoreData.BuildNumber})"
            : "UniGetUI TUI";
    }

    // ─── Keyboard ─────────────────────────────────────────────────────────

    private IInputElement? FocusedElement => TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();

    private void OnWindowTextInput(object? sender, TextInputEventArgs e)
    {
        if (_menuActive || TuiModal.IsOpen || FocusedElement is TextBox) return;
        if (e.Text == "/" && CurrentPage is ITuiPage page && page.FocusSearch())
            e.Handled = true;
    }

    /// <summary>
    /// AltGr characters (\ @ { } [ ] | € … on most non-US layouts) reach us as Ctrl+Alt key presses, and
    /// Consolonia never turns a Ctrl or Alt chord into text input, so they would be dropped. When the key
    /// carries a printable symbol, deliver that symbol as text to the focused control ourselves.
    /// </summary>
    private bool TryDeliverAltGrText(KeyEventArgs e)
    {
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Alt)) != (KeyModifiers.Control | KeyModifiers.Alt)) return false;
        if (e.KeySymbol is not { Length: 1 } symbol || char.IsControl(symbol[0])) return false;
        if (FocusedElement is not Interactive target) return false;
        target.RaiseEvent(new TextInputEventArgs { RoutedEvent = TextInputEvent, Text = symbol, Source = target });
        return true;
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        _userMovedSinceNavigation = true;
        if (TryDeliverAltGrText(e))
        {
            e.Handled = true;
            return;
        }

        KeyModifiers mods = e.KeyModifiers;
        if (e.Key == Key.Q && mods == KeyModifiers.Control)
        {
            _ = QuitAsync();
            e.Handled = true;
            return;
        }

        if (TuiModal.IsOpen)
        {
            if (TuiModal.HandleKey(e)) e.Handled = true;
            return;
        }

        // Alt+1…9 / Ctrl+1…9: jump to the numbered tab (Alt works in every terminal, Ctrl+digit does not).
        if (mods is KeyModifiers.Alt or KeyModifiers.Control && TabIndexFromKey(e.Key) is int tabIndex)
        {
            CloseMenu();
            NavigateTo(TabPages[tabIndex]);
            e.Handled = true;
            return;
        }

        // Access keys: Alt+F File, Alt+P Page, … (also switches menus while one is open).
        if (mods == KeyModifiers.Alt && TuiMnemonics.KeyChar(e) is char accessKey && OpenMenuByAccessKey(accessKey))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.F10 && mods == KeyModifiers.None)
        {
            ToggleMenu();
            e.Handled = true;
            return;
        }

        if (_menuActive)
        {
            HandleMenuKey(e);
            return;
        }

        if (HandleGlobalShortcut(e))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Tab && mods is KeyModifiers.None or KeyModifiers.Shift)
        {
            MoveFocusInPage(mods == KeyModifiers.Shift ? -1 : 1);
            e.Handled = true;
        }
    }

    /// <summary>Esc that no control used brings focus back to the page's main control (e.g. filter → list).</summary>
    private void OnWindowKeyDownUnhandled(object? sender, KeyEventArgs e)
    {
        if (e.Handled || _menuActive || TuiModal.IsOpen) return;
        if (e.Key == Key.Escape && e.KeyModifiers == KeyModifiers.None)
        {
            FocusContent();
            e.Handled = true;
        }
    }

    private static int? TabIndexFromKey(Key key)
    {
        int? digit = key switch
        {
            >= Key.D1 and <= Key.D9 => key - Key.D1,
            >= Key.NumPad1 and <= Key.NumPad9 => key - Key.NumPad1,
            _ => null,
        };
        return digit < TabPages.Length ? digit : null;
    }

    private bool HandleGlobalShortcut(KeyEventArgs e)
    {
        KeyModifiers mods = e.KeyModifiers;
        var page = CurrentPage as ITuiPage;
        if (e.Key == Key.F1 && mods == KeyModifiers.None)
        {
            NavigateTo(TuiPageIds.Help);
            return true;
        }

        if ((e.Key == Key.F5 && mods == KeyModifiers.None) || (e.Key == Key.R && mods == KeyModifiers.Control))
        {
            page?.Reload();
            return true;
        }

        if (e.Key == Key.F && mods == KeyModifiers.Control)
        {
            page?.FocusSearch();
            return true;
        }

        if (e.Key == Key.A && mods == KeyModifiers.Control && FocusedElement is not TextBox)
        {
            page?.ToggleSelectAll();
            return true;
        }

        if (e.Key == Key.Tab && mods.HasFlag(KeyModifiers.Control))
        {
            int count = TabPages.Length;
            int current = Array.IndexOf(TabPages, CurrentPageId);
            int next = mods.HasFlag(KeyModifiers.Shift)
                ? (current < 0 ? count - 1 : (current - 1 + count) % count)
                : (current + 1) % count;
            NavigateTo(TabPages[next]);
            return true;
        }

        return false;
    }

    /// <summary>Focuses the current page's main control (its list, form or first field).</summary>
    internal bool FocusContent()
    {
        if (_contentHost.Content is IFocusablePage page && page.FocusPrimary()) return true;
        IInputElement? first = PageFocusables().FirstOrDefault();
        return first is not null ? FocusElement(first) : Focus();
    }

    /// <summary>
    /// Focuses the page once it is attached and laid out (right after a navigation). Pages pick their
    /// primary control from their content (the list once it has rows, the filter box before), and content
    /// often arrives just after the page is shown, so for a few seconds, until the user presses a key or
    /// clicks, focus parked on an empty filter box is moved to the list as soon as there is one.
    /// </summary>
    private void FocusPageSoon()
    {
        Control? page = _contentHost.Content as Control;
        _userMovedSinceNavigation = false;
        _focusSettleTimer?.Stop();
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(_contentHost.Content, page) || TuiModal.IsOpen || _menuActive) return;
            if (FocusedElement is Visual focused && page is not null && page.IsVisualAncestorOf(focused)) return;
            FocusContent();
        }, DispatcherPriority.Loaded);

        int ticks = 0;
        _focusSettleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _focusSettleTimer.Tick += (sender, _) =>
        {
            bool parked = FocusedElement is null or Window || FocusedElement is TextBox { Text: null or "" };
            if (++ticks > 60 || _userMovedSinceNavigation || !ReferenceEquals(_contentHost.Content, page))
            {
                ((DispatcherTimer)sender!).Stop();
                return;
            }

            if (!parked || TuiModal.IsOpen || _menuActive) return;
            FocusContent();
            if (FocusedElement is not TextBox) ((DispatcherTimer)sender!).Stop();
        };
        _focusSettleTimer.Start();
    }

    /// <summary>Tab / Shift+Tab: the next or previous focusable control of the page, wrapping around.</summary>
    private void MoveFocusInPage(int delta)
    {
        List<InputElement> items = PageFocusables().ToList();
        if (items.Count == 0) return;
        var focused = FocusedElement as Visual;
        int index = items.FindIndex(i => ReferenceEquals(i, focused) || (focused is not null && i.IsVisualAncestorOf(focused)));
        int next = index < 0 ? (delta > 0 ? 0 : items.Count - 1) : (index + delta + items.Count) % items.Count;
        FocusElement(items[next]);
        (items[next] as Control)?.BringIntoView();
    }

    /// <summary>The page's outermost focusable controls, in visual order (a grid counts once, not per cell).</summary>
    private IEnumerable<InputElement> PageFocusables()
    {
        if (_contentHost.Content is not Visual page) yield break;
        var result = new List<InputElement>();
        foreach (InputElement element in page.GetVisualDescendants().OfType<InputElement>())
        {
            if (!element.Focusable || !element.IsEffectivelyVisible || !element.IsEffectivelyEnabled) continue;
            if (!KeyboardNavigation.GetIsTabStop(element)) continue;
            if (result.Any(r => r.IsVisualAncestorOf(element))) continue;
            result.Add(element);
        }

        foreach (InputElement element in result) yield return element;
    }

    private static bool FocusElement(IInputElement element) => element switch
    {
        ListBox list => TuiFocus.SeatListFocus(list),
        DataGrid grid => TuiFocus.SeatDataGridFocus(grid),
        _ => element.Focus(),
    };

    // ─── Quit ─────────────────────────────────────────────────────────────

    private bool _allowClose;

    private void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowClose) return;
        e.Cancel = true;
        _ = QuitAsync();
    }

    public async Task QuitAsync()
    {
        if (TuiBundleService.HasUnsavedChanges && PackageBundlesLoader.Instance.Packages.Count > 0
            && !await TuiPrompts.ConfirmAsync(CoreTools.Translate("Unsaved changes"),
                CoreTools.Translate("The current bundle has unsaved changes. Do you want to discard them?"),
                CoreTools.Translate("Discard"), CoreTools.Translate("Cancel")))
            return;

        if (TuiOperationRegistry.ActiveCount > 0
            && !await TuiPrompts.ConfirmAsync(CoreTools.Translate("Quit"),
                CoreTools.Translate("There are operations in progress. Quitting may cause them to fail. Do you want to continue?")))
            return;

        _allowClose = true;
        TuiModal.CloseAll();
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }

    // ─── Menu bar ─────────────────────────────────────────────────────────

    private MenuDefinition[] BuildMenuDefinitions()
    {
        IReadOnlyList<TuiAction> Pages() => TabPages.Select((id, i) => new TuiAction(TuiPages.Label(id), $"Alt+{i + 1}", () =>
        {
            NavigateTo(id);
            return Task.CompletedTask;
        })).Append(TuiAction.Separator)
            .Append(new TuiAction(CoreTools.Translate("Theme") + "…", null, ChooseThemeAsync)).ToList();

        IReadOnlyList<TuiAction> File() =>
        [
            new(CoreTools.Translate("New bundle"), null, () => WithBundles(p => p.NewBundleAsync())),
            new(CoreTools.Translate("Open existing bundle") + "…", "Ctrl+O", () => WithBundles(p => p.OpenBundleAsync())),
            new(CoreTools.Translate("Save bundle as") + "…", "Ctrl+S", () => WithBundles(p => p.SaveBundleAsync())),
            TuiAction.Separator,
            new(CoreTools.Translate("Quit"), "Ctrl+Q", QuitAsync),
        ];

        IReadOnlyList<TuiAction> Operations() =>
        [
            new(CoreTools.Translate("Update all"), null, async () =>
            {
                int n = await TuiPackageActions.UpdateAllAsync();
                TuiNotifications.Info(CoreTools.Translate("Update all"), CoreTools.Translate("{0} operation(s) added to the queue", n));
            }),
            new(CoreTools.Translate("Retry failed operations"), null, () =>
            {
                TuiOperationRegistry.RetryFailed();
                return Task.CompletedTask;
            }),
            new(CoreTools.Translate("Clear successful operations"), null, () =>
            {
                TuiOperationRegistry.ClearSuccessful();
                return Task.CompletedTask;
            }),
            new(CoreTools.Translate("Clear finished operations"), null, () =>
            {
                TuiOperationRegistry.ClearFinished();
                return Task.CompletedTask;
            }),
            new(CoreTools.Translate("Cancel all operations"), null, () =>
            {
                TuiOperationRegistry.CancelAll();
                return Task.CompletedTask;
            }),
            TuiAction.Separator,
            new(CoreTools.Translate("Manage ignored updates"), null, IgnoredUpdatesDialog.ShowAsync),
            new(CoreTools.Translate("Manage automatic updates"), null, AutoUpdatesDialog.ShowAsync),
        ];

        IReadOnlyList<TuiAction> Help() =>
        [
            new(CoreTools.Translate("Help"), "F1", () =>
            {
                NavigateTo(TuiPageIds.Help);
                return Task.CompletedTask;
            }),
            new(CoreTools.Translate("Release notes"), null, () =>
            {
                TuiPackageActions.OpenExternally(CoreData.ReleaseNotesUrl);
                return Task.CompletedTask;
            }),
            new(CoreTools.Translate("UniGetUI Log"), null, () =>
            {
                NavigateTo(TuiPageIds.Logs);
                return Task.CompletedTask;
            }),
            new(CoreTools.Translate("Operation history"), null, () =>
            {
                NavigateTo(TuiPageIds.History);
                return Task.CompletedTask;
            }),
            new(CoreTools.Translate("About"), null, () =>
            {
                NavigateTo(TuiPageIds.About);
                return Task.CompletedTask;
            }),
        ];

        return
        [
            new(CoreTools.Translate("File"), File),
            new(CoreTools.Translate("Page"), () => (CurrentPage as ITuiPage)?.Actions ?? []),
            new(CoreTools.Translate("View"), Pages),
            new(CoreTools.Translate("Operations"), Operations),
            new(CoreTools.Translate("Help"), Help),
        ];
    }

    private static async Task ChooseThemeAsync()
    {
        int current = Math.Max(0, TuiThemes.All.ToList().IndexOf(TuiPalette.Current));
        int? picked = await TuiPrompts.ChooseAsync(CoreTools.Translate("Theme"),
            TuiThemes.All.Select(t => new TuiChoice(t.Name)).ToList(), current);
        if (picked is int index) TuiPalette.Apply(TuiThemes.All[index], save: true);
    }

    private async Task WithBundles(Func<PackageListPage, Task> action)
    {
        NavigateTo(TuiPageIds.Bundles);
        await action(GetPage<PackageListPage>(TuiPageIds.Bundles));
    }

    private StackPanel BuildMenuBar()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        _headerMnemonics = TuiMnemonics.Assign(_menuDefinitions.Select(d => (string?)d.Header).ToList());
        for (int i = 0; i < _menuDefinitions.Length; i++)
        {
            int menuIndex = i;
            var item = new Border
            {
                Background = Brushes.Transparent,
                Child = TuiMnemonics.Build(" ", _menuDefinitions[i].Header, _headerMnemonics[i], " ", TuiPalette.MenuText),
            };
            item.PointerPressed += (_, e) =>
            {
                e.Handled = true;
                if (_menuActive && _openMenuIndex == menuIndex) CloseMenu();
                else OpenMenu(menuIndex);
            };
            _menuHeaderBorders.Add(item);
            panel.Children.Add(item);
        }

        return panel;
    }

    private void ToggleMenu()
    {
        if (_menuActive) CloseMenu();
        else OpenMenu(1);
    }

    private void HandleMenuKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                CloseMenu();
                break;
            case Key.Left:
                OpenMenu((_openMenuIndex - 1 + _menuDefinitions.Length) % _menuDefinitions.Length);
                break;
            case Key.Right:
                OpenMenu((_openMenuIndex + 1) % _menuDefinitions.Length);
                break;
            case Key.Up:
                MoveMenuSelection(-1);
                break;
            case Key.Down:
                MoveMenuSelection(1);
                break;
            case Key.Enter or Key.Return or Key.Space:
                ExecuteSelectedMenuAction();
                break;
        }

        e.Handled = true;
    }

    private bool OpenMenuByAccessKey(char key)
    {
        for (int i = 0; i < _menuDefinitions.Length; i++)
        {
            if (!TuiMnemonics.Matches(_menuDefinitions[i].Header, _headerMnemonics[i], key)) continue;
            OpenMenu(i);
            return true;
        }

        return false;
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _userMovedSinceNavigation = true;
        if (!_menuActive) return;
        for (var v = e.Source as Visual; v is not null; v = v.GetVisualParent())
            if (ReferenceEquals(v, _menuBar) || ReferenceEquals(v, _menuDropDown)) return;
        CloseMenu();
        e.Handled = true;
    }

    private void OpenMenu(int index)
    {
        if (!_menuActive) _focusBeforeMenu = FocusedElement;
        _menuActive = true;
        _openMenuIndex = Math.Clamp(index, 0, _menuDefinitions.Length - 1);
        _openMenuActions = _menuDefinitions[_openMenuIndex].Actions();
        _selectedMenuActionIndex = NextSelectable(-1, 1);
        RenderMenu();
    }

    private void CloseMenu()
    {
        if (!_menuActive) return;
        _menuActive = false;
        _openMenuIndex = -1;
        RenderMenu();
        // Back to where the user was, unless that control has left the page meanwhile.
        if (_focusBeforeMenu is Visual { IsEffectivelyVisible: true } previous && _contentHost.IsVisualAncestorOf(previous)
            && FocusElement(_focusBeforeMenu)) return;
        FocusContent();
    }

    private int NextSelectable(int from, int delta)
    {
        for (int step = 1; step <= _openMenuActions.Count; step++)
        {
            int i = ((from + delta * step) % _openMenuActions.Count + _openMenuActions.Count) % _openMenuActions.Count;
            if (!_openMenuActions[i].IsSeparator && _openMenuActions[i].IsEnabled) return i;
        }

        return -1;
    }

    private void MoveMenuSelection(int delta)
    {
        if (_openMenuActions.Count == 0) return;
        _selectedMenuActionIndex = NextSelectable(_selectedMenuActionIndex, delta);
        RenderMenu();
    }

    private void ExecuteSelectedMenuAction()
    {
        if (_selectedMenuActionIndex < 0 || _selectedMenuActionIndex >= _openMenuActions.Count) return;
        TuiAction action = _openMenuActions[_selectedMenuActionIndex];
        if (!action.IsEnabled) return;
        CloseMenu();
        _ = RunActionAsync(action);
    }

    private static async Task RunActionAsync(TuiAction action)
    {
        try
        {
            await action.Run();
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            TuiNotifications.Error(action.Label, ex.Message);
        }
    }

    private void RenderMenu()
    {
        double left = 0;
        for (int i = 0; i < _menuHeaderBorders.Count; i++)
        {
            bool open = _menuActive && i == _openMenuIndex;
            _menuHeaderBorders[i].Background = open ? TuiPalette.Focus : Brushes.Transparent;
            _menuHeaderBorders[i].Child = TuiMnemonics.Build(" ", _menuDefinitions[i].Header, _headerMnemonics[i], " ", open ? TuiPalette.FocusText : TuiPalette.MenuText);
            if (i < _openMenuIndex) left += _menuDefinitions[i].Header.Length + 2;
        }

        _menuDropDownItems.Children.Clear();
        if (!_menuActive || _openMenuIndex < 0)
        {
            _menuDropDown.IsVisible = false;
            return;
        }

        Canvas.SetLeft(_menuDropDown, left);
        _menuDropDown.IsVisible = true;
        if (_openMenuActions.Count == 0)
            _menuDropDownItems.Children.Add(new TextBlock { Text = " " + CoreTools.Translate("No actions") + " ", Foreground = TuiPalette.TextDim });

        int width = _openMenuActions.Select(a => a.Label.Length + (a.Shortcut?.Length ?? 0) + 4).DefaultIfEmpty(20).Max();
        for (int i = 0; i < _openMenuActions.Count; i++)
        {
            TuiAction action = _openMenuActions[i];
            int actionIndex = i;
            bool selected = !action.IsSeparator && i == _selectedMenuActionIndex;
            IBrush foreground = action.IsSeparator || !action.IsEnabled ? TuiPalette.TextDim : selected ? TuiPalette.FocusText : TuiPalette.MenuText;
            Control block = action.IsSeparator
                ? new TextBlock { Text = new string('─', width), Foreground = foreground }
                : new TextBlock
                {
                    Text = " " + action.Label + new string(' ', Math.Max(1, width - action.Label.Length - (action.Shortcut?.Length ?? 0) - 2))
                           + (action.Shortcut ?? "") + " ",
                    Foreground = foreground,
                };

            var row = new Border
            {
                Background = selected ? TuiPalette.Focus : Brushes.Transparent,
                Child = block,
            };
            if (!action.IsSeparator)
            {
                row.PointerPressed += (_, e) =>
                {
                    e.Handled = true;
                    _selectedMenuActionIndex = actionIndex;
                    ExecuteSelectedMenuAction();
                };
            }

            _menuDropDownItems.Children.Add(row);
        }
    }

    // ─── Notifications ────────────────────────────────────────────────────

    private void OnNotificationRaised(TuiNotification notification)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnNotificationRaised(notification), DispatcherPriority.Background);
            return;
        }

        if (Settings.Get(Settings.K.DisableNotifications) && notification.Severity != TuiNotificationSeverity.Error) return;
        _notificationTimer.Stop();
        (string badge, IBrush tint, IBrush strip) = notification.Severity switch
        {
            TuiNotificationSeverity.Success => (CoreTools.Translate("OK"), TuiPalette.Success, TuiPalette.SuccessBackground),
            TuiNotificationSeverity.Warning => (CoreTools.Translate("WARNING"), TuiPalette.Warning, TuiPalette.WarningBackground),
            TuiNotificationSeverity.Error => (CoreTools.Translate("ERROR"), TuiPalette.Error, TuiPalette.ErrorBackground),
            _ => (CoreTools.Translate("INFO"), TuiPalette.Brand, TuiPalette.InfoBackground),
        };
        _functionBar.ShowNotification(badge, tint, strip, notification.Title, notification.Message);
        _notificationTimer.Interval = notification.Duration;
        _notificationTimer.Start();
        LastNotification = notification;
    }

    public TuiNotification? LastNotification { get; private set; }

    /// <summary>The highlighted entry of the open menu, if any (for tests and diagnostics).</summary>
    internal string? SelectedMenuActionLabel => _menuActive && _selectedMenuActionIndex >= 0 && _selectedMenuActionIndex < _openMenuActions.Count
        ? _openMenuActions[_selectedMenuActionIndex].Label : null;

    private void HideNotification()
    {
        _notificationTimer.Stop();
        _functionBar.HideNotification();
    }
}
