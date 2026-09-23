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
using UniGetUI.PackageEngine.Enums;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Theme;
using UniGetUI.Tui.Views.Pages;

namespace UniGetUI.Tui.Views;

/// <summary>
/// The TUI's single top-level window: a Devolutions-branded header, a left sidebar of navigation
/// entries, and a content host that swaps page content as the selection changes. Built entirely in
/// code (no AXAML) to sidestep Avalonia 12 compiled-binding pitfalls in the Consolonia fork.
/// </summary>
internal sealed class MainWindow : Window
{
    private sealed record NavEntry(string Id, string Label);

    private sealed record MenuDefinition(string Header, IReadOnlyList<MenuAction> Actions);

    private sealed record MenuAction(string Label, Action? Execute)
    {
        public bool IsSeparator => Execute is null;
    }

    private static readonly NavEntry[] NavEntries =
    [
        new("discover", "Discover"),
        new("installed", "Installed"),
        new("updates", "Updates"),
        new("operations", "Operations"),
        new("bundles", "Bundles"),
        new("managers", "Managers"),
        new("settings", "Settings"),
        new("logs", "Logs"),
        new("about", "About"),
    ];

    private readonly ContentControl _contentHost;
    private readonly ListBox _navList;
    private readonly MenuDefinition[] _menuDefinitions;
    private readonly StackPanel _menuBar;
    private readonly Border _menuDropDown;
    private readonly StackPanel _menuDropDownItems;
    private readonly Border _notificationHost;
    private readonly TextBlock _notificationText;
    private readonly DispatcherTimer _notificationTimer;
    private readonly List<Border> _menuHeaderBorders = new();
    private readonly List<Border> _menuActionBorders = new();
    private readonly Dictionary<string, Control> _pageCache = new();

    private enum ActivePane
    {
        Sidebar,
        Content
    }

    private ActivePane _activePane = ActivePane.Sidebar;

    // True while the top menu bar owns the keyboard/mouse. Implemented inline (no Avalonia PopupRoot)
    // because Consolonia's popup-based MenuItem mouse path can capture input indefinitely in Win32 terminals.
    private bool _menuActive;
    private ActivePane _paneBeforeMenu = ActivePane.Sidebar;
    private int _openMenuIndex = -1;
    private int _selectedMenuActionIndex;

    public MainWindow()
    {
        Title = $"UniGetUI TUI  ·  v{CoreData.VersionName}";

        _contentHost = new ContentControl
        {
            Margin = new Thickness(1, 0, 1, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        _navList = BuildNavList();
        var sidebar = WrapSidebar(_navList);
        var header = BuildHeader();
        var footer = BuildFooter();
        _menuDefinitions = BuildMenuDefinitions();
        _menuBar = BuildMenuBar();
        _menuDropDownItems = new StackPanel { Spacing = 0 };
        _menuDropDown = BuildMenuDropDown(_menuDropDownItems);
        var menuBar = WrapMenu(_menuBar);
        _notificationText = new TextBlock { TextWrapping = TextWrapping.NoWrap };
        _notificationHost = BuildNotificationHost(_notificationText);
        _notificationTimer = new DispatcherTimer();
        _notificationTimer.Tick += (_, _) => HideNotification();

        var root = new DockPanel { LastChildFill = true };

        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(menuBar, Dock.Top);
        DockPanel.SetDock(_menuDropDown, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        DockPanel.SetDock(_notificationHost, Dock.Bottom);
        DockPanel.SetDock(sidebar, Dock.Left);

        root.Children.Add(header);
        root.Children.Add(menuBar);
        root.Children.Add(_menuDropDown);
        root.Children.Add(footer);
        root.Children.Add(_notificationHost);
        root.Children.Add(sidebar);
        root.Children.Add(_contentHost);

        Content = root;

        // KeyDown routes only when some element owns focus, so reliably seat focus on the sidebar
        // once the tree is attached. PTYs can drop a single synchronous Focus() in Opened, so retry
        // on the dispatcher; keep the window itself focusable as a last-resort focus target.
        Focusable = true;
        Opened += (_, _) =>
        {
            // Dev/test hook: boot straight into a page with its filter box focused. This exists
            // purely so the filter/search path can be exercised over PTY harnesses that can only
            // deliver printable characters (no Tab/Enter/Esc to transfer focus). Not documented;
            // no effect unless the env var names a known nav entry.
            string? bootPage = Environment.GetEnvironmentVariable("UNIGETUI_TUI_DEBUG_BOOTPAGE");
            if (!string.IsNullOrWhiteSpace(bootPage))
            {
                int idx = Array.FindIndex(NavEntries, n => n.Id == bootPage);
                if (idx >= 0)
                {
                    _navList.SelectedIndex = idx;
                    Dispatcher.UIThread.Post(() => FocusContent(), DispatcherPriority.Background);
                    return;
                }
            }

            // Dev/test hook: enqueue a bogus winget operation that fails instantly (no system change)
            // and jump to the Operations page. Lets the full operation pipeline — options loading, op
            // construction, registry tracking, process spawn, stdout/stderr streaming, status→Failed,
            // and log rendering — be exercised over a PTY harness without installing anything.
            string? debugOp = Environment.GetEnvironmentVariable("UNIGETUI_TUI_DEBUG_OP");
            if (debugOp == "fail")
            {
                Dispatcher.UIThread.Post(() => DebugEnqueueFailingOp(), DispatcherPriority.Background);
            }
            else if (debugOp == "sim")
            {
                Dispatcher.UIThread.Post(() => DebugEnqueueSimOp(), DispatcherPriority.Background);
            }

            // Dev/test hook: import a bundle file on boot and jump to the Bundles page. Lets the bundle
            // import + render path be exercised over a PTY harness (which can't type a file path + Enter).
            string? bundleFile = Environment.GetEnvironmentVariable("UNIGETUI_TUI_DEBUG_BUNDLE");
            if (!string.IsNullOrWhiteSpace(bundleFile))
            {
                Dispatcher.UIThread.Post(() => DebugImportBundle(bundleFile), DispatcherPriority.Background);
            }

            if (!_navList.Focus())
            {
                Dispatcher.UIThread.Post(() =>
                {
                    if (!_navList.Focus()) Focus();
                }, DispatcherPriority.Background);
            }
        };

        // Drive navigation from window-level key handling (tunnel) rather than relying on the
        // ListBox owning focus — this is robust across terminals/PTYs where focus routing varies.
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        // Punctuation like "/" often arrives without a usable virtual-key code over a PTY, so it is
        // only reliably observable as a TextInput character. Use that to drive the "/" filter jump.
        AddHandler(TextInputEvent, OnWindowTextInput, RoutingStrategies.Tunnel);

        // There is no popup/light-dismiss layer. Click-away is just an outside press in the same visual tree.
        AddHandler(PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel);

        // Pages don't hold a window reference; they request navigation through the decoupled shell bus
        // (e.g. PackageListPage jumps here to "operations" after enqueueing an operation).
        TuiShell.NavigationRequested += OnNavigationRequested;
        TuiNotifications.NotificationRaised += OnNotificationRaised;
        Closed += (_, _) =>
        {
            TuiShell.NavigationRequested -= OnNavigationRequested;
            TuiNotifications.NotificationRaised -= OnNotificationRaised;
            _notificationTimer.Stop();
        };
    }

    private void OnNavigationRequested(string pageId)
    {
        Dispatcher.UIThread.Post(() =>
        {
            int idx = Array.FindIndex(NavEntries, n => n.Id == pageId);
            if (idx >= 0)
            {
                _navList.SelectedIndex = idx;
                _activePane = ActivePane.Sidebar;
            }
        });
    }

    private void OnWindowTextInput(object? sender, TextInputEventArgs e)
    {
        if (_menuActive) return;
        if (IsContentActive()) return;
        if (e.Text == "/" && FocusContent())
        {
            e.Handled = true;
        }
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+Q quits from anywhere, even while typing in a search box.
        if (e.Key == Key.Q && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Quit();
            e.Handled = true;
            return;
        }

        // F10 toggles the top menu bar (classic terminal-app idiom; no built-in F10 in the fork).
        if (e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.None)
        {
            ToggleMenu();
            e.Handled = true;
            return;
        }

        // While the inline menu owns the keyboard, route arrows/Enter/Esc locally. This avoids the
        // popup-based Avalonia Menu code path entirely, which is the real-terminal mouse freeze vector.
        if (_menuActive)
        {
            HandleMenuKey(e);
            return;
        }

        // Tab toggles between the sidebar and the active page's primary control.
        if (e.Key == Key.Tab
            && !e.KeyModifiers.HasFlag(KeyModifiers.Control)
            && !e.KeyModifiers.HasFlag(KeyModifiers.Alt))
        {
            if (IsContentActive()) FocusSidebar();
            else FocusContent();
            e.Handled = true;
            return;
        }

        // When the content pane owns focus, let its controls handle typing/list navigation;
        // only Esc is intercepted, to bounce focus back to the sidebar.
        if (IsContentActive())
        {
            if (e.Key == Key.Escape)
            {
                FocusSidebar();
                e.Handled = true;
            }

            return;
        }

        // "/" jumps straight to the active page's filter box (the less/vim idiom); Enter "opens"
        // the selected section by moving focus into its primary control. Enter is the robust path —
        // some terminals/PTYs drop punctuation like "/" through Console.ReadKey, but Enter always
        // survives.
        if ((e.Key is Key.OemQuestion or Key.Oem2 or Key.Enter or Key.Return)
            && e.KeyModifiers == KeyModifiers.None)
        {
            if (FocusContent())
            {
                e.Handled = true;
                return;
            }
        }

        HandleSidebarKey(e);
    }

    // The explicit pane mode is the source of truth. A focused TextBox is the only focus-based
    // override (covers the Discover search box receiving focus before the mode flips); a content
    // ListBox auto-grabbing keyboard focus must NOT count as "content active", otherwise sidebar
    // nav keys (digits/arrows) would be wrongly passed through to the list.
    private bool IsContentActive()
    {
        if (_activePane == ActivePane.Content) return true;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        return focused is TextBox or AutoCompleteBox;
    }

    private void FocusSidebar()
    {
        _menuActive = false;
        _activePane = ActivePane.Sidebar;
        SeatSidebarFocus(isRetry: false);
    }

    // Reliably move keyboard focus onto the sidebar nav list. A bare _navList.Focus() does NOT pull
    // focus out of a TextBox over a PTY (proven), which left the user stuck in a search/filter box.
    // Focusing the selected item's realized ListBoxItem container does work; verify via the
    // FocusManager that focus actually landed on the sidebar and post one retry if it didn't (mirrors
    // the startup double-post pattern, since PTYs can drop a single synchronous Focus()).
    private void SeatSidebarFocus(bool isRetry)
    {
        int idx = _navList.SelectedIndex < 0 ? 0 : _navList.SelectedIndex;
        Control? container = _navList.ContainerFromIndex(idx);
        if (container?.Focus() != true)
            _navList.Focus();

        if (FocusIsOnSidebar() || isRetry) return;
        Dispatcher.UIThread.Post(() => SeatSidebarFocus(isRetry: true), DispatcherPriority.Background);
    }

    private bool FocusIsOnSidebar()
    {
        Visual? focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        while (focused is not null)
        {
            if (ReferenceEquals(focused, _navList)) return true;
            focused = focused.GetVisualParent();
        }

        return false;
    }

    // Dev/test hook: enqueue a SUCCEEDING fake operation (no system change) and jump to the Operations
    // page. Mirrors DebugEnqueueFailingOp but exercises the SimulatedOperation success path. Needed
    // because the PTY harness cannot move keyboard focus into the package ListBox to press i/u on a row.
    private static void DebugEnqueueSimOp()
    {
        TuiOperationRegistry.Start(
            new UniGetUI.Tui.Infrastructure.SimulatedOperation("Uninstall", "Validation Package"));
        TuiShell.Navigate("operations");
    }

    private static async void DebugEnqueueFailingOp()
    {
        try
        {
            // In simulation mode, never spawn a real process — enqueue a failing fake op instead.
            if (UniGetUI.Tui.Infrastructure.SimulatedOperation.IsEnabled)
            {
                TuiOperationRegistry.Start(
                    new UniGetUI.Tui.Infrastructure.SimulatedOperation("Install", "Nonexistent Validation Package"));
                TuiShell.Navigate("operations");
                return;
            }

            var pkg = new UniGetUI.PackageEngine.PackageClasses.Package(
                "Nonexistent Validation Package",
                "UniGetUI.TuiValidation.DoesNotExist",
                "0.0.0",
                UniGetUI.PackageEngine.PEInterface.WinGet.DefaultSource,
                UniGetUI.PackageEngine.PEInterface.WinGet);

            var opts = await UniGetUI.PackageEngine.PackageClasses.InstallOptionsFactory
                .LoadApplicableAsync(pkg, elevated: false, interactive: false);

            var op = new UniGetUI.PackageEngine.Operations.InstallPackageOperation(pkg, opts);
            TuiOperationRegistry.Start(op);
            TuiShell.Navigate("operations");
        }
        catch (Exception ex)
        {
            UniGetUI.Core.Logging.Logger.Error(ex);
        }
    }

    private static async void DebugImportBundle(string path)
    {
        try
        {
            await UniGetUI.Tui.Infrastructure.TuiBundleService.ImportFileAsync(path);
            TuiShell.Navigate("bundles");
        }
        catch (Exception ex)
        {
            UniGetUI.Core.Logging.Logger.Error(ex);
        }
    }

    private bool FocusContent()
    {
        if (_contentHost.Content is IFocusablePage page && page.FocusPrimary())
        {
            _activePane = ActivePane.Content;
            return true;
        }

        return false;
    }

    private void HandleSidebarKey(KeyEventArgs e)
    {
        int count = NavEntries.Length;
        int current = _navList.SelectedIndex < 0 ? 0 : _navList.SelectedIndex;

        switch (e.Key)
        {
            case Key.Down:
            case Key.Right:
                _navList.SelectedIndex = (current + 1) % count;
                e.Handled = true;
                break;
            case Key.Up:
            case Key.Left:
                _navList.SelectedIndex = (current - 1 + count) % count;
                e.Handled = true;
                break;
            case Key.Home:
                _navList.SelectedIndex = 0;
                e.Handled = true;
                break;
            case Key.End:
                _navList.SelectedIndex = count - 1;
                e.Handled = true;
                break;
            case Key.Escape:
                Quit();
                e.Handled = true;
                break;
            default:
                // Number keys 1-9 jump directly to a nav entry (both the top-row and numpad digits).
                int? digit = e.Key switch
                {
                    >= Key.D1 and <= Key.D9 => e.Key - Key.D1,
                    >= Key.NumPad1 and <= Key.NumPad9 => e.Key - Key.NumPad1,
                    _ => null,
                };
                if (digit is int navIdx && navIdx < count)
                {
                    _navList.SelectedIndex = navIdx;
                    e.Handled = true;
                }

                break;
        }
    }

    private Control GetPage(string id)
    {
        if (_pageCache.TryGetValue(id, out Control? cached)) return cached;
        Control page = TuiPages.Build(id);
        _pageCache[id] = page;
        return page;
    }

    private static void Quit()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.Shutdown();
        }
    }

    private PackageListPage? CurrentPackageList => _contentHost.Content as PackageListPage;
    private OperationsPage? CurrentOperations => _contentHost.Content as OperationsPage;

    private void ToggleMenu()
    {
        if (_menuActive) CloseMenu();
        else OpenMenu();
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
            case Key.Home:
                _selectedMenuActionIndex = FirstSelectableActionIndex(_openMenuIndex);
                RenderMenu();
                break;
            case Key.End:
                _selectedMenuActionIndex = LastSelectableActionIndex(_openMenuIndex);
                RenderMenu();
                break;
            case Key.Enter:
            case Key.Space:
                ExecuteSelectedMenuAction();
                break;
        }

        e.Handled = true;
    }

    private void OnMenuHeaderPointerPressed(int index, PointerPressedEventArgs e)
    {
        e.Handled = true;
        if (_menuActive && _openMenuIndex == index) CloseMenu();
        else OpenMenu(index);
    }

    private void OnMenuActionPointerPressed(int index, PointerPressedEventArgs e)
    {
        e.Handled = true;
        _selectedMenuActionIndex = index;
        RenderMenu();
        ExecuteSelectedMenuAction();
    }

    // Click-away dismissal. The menu is an ordinary in-window visual tree (no PopupRoot), so outside
    // detection is just an ancestor walk against the menu bar and inline dropdown.
    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!_menuActive) return;
        if (IsWithinMenuArea(e.Source)) return;
        CloseMenu();
        e.Handled = true;
    }

    private bool IsWithinMenuArea(object? source)
    {
        return IsVisualWithin(source, _menuBar) || IsVisualWithin(source, _menuDropDown);
    }

    private static bool IsVisualWithin(object? source, Visual root)
    {
        for (var v = source as Visual; v is not null; v = v.GetVisualParent())
        {
            if (ReferenceEquals(v, root)) return true;
        }

        return false;
    }

    private void OpenMenu() => OpenMenu(0);

    private void OpenMenu(int index)
    {
        if (!_menuActive)
            _paneBeforeMenu = _activePane;

        _menuActive = true;
        _openMenuIndex = Math.Clamp(index, 0, _menuDefinitions.Length - 1);
        _selectedMenuActionIndex = FirstSelectableActionIndex(_openMenuIndex);
        RenderMenu();
    }

    private void CloseMenu()
    {
        if (!_menuActive) return;

        _menuActive = false;
        _openMenuIndex = -1;
        _selectedMenuActionIndex = -1;
        RenderMenu();

        if (_paneBeforeMenu == ActivePane.Content && FocusContent()) return;
        FocusSidebar();
    }

    private void OpenBundlePicker()
    {
        TuiShell.Navigate("bundles");
        Dispatcher.UIThread.Post(() =>
        {
            // The picker is launched from the Bundles page; keep content mode so focus returns to the
            // bundle grid/filter instead of the sidebar once the managed picker closes.
            _activePane = ActivePane.Content;
            (_contentHost.Content as BundlesPage)?.OpenBundlePicker();
        }, DispatcherPriority.Background);
    }

    private void SaveBundlePicker()
    {
        TuiShell.Navigate("bundles");
        Dispatcher.UIThread.Post(() =>
        {
            _activePane = ActivePane.Content;
            (_contentHost.Content as BundlesPage)?.SaveBundlePicker();
        }, DispatcherPriority.Background);
    }

    // Classic Borland/RDM-style top menu bar, rendered inline to avoid Consolonia popup focus capture.
    // Commands are wired to the existing navigation/operation hooks; package actions target the current page.
    private MenuDefinition[] BuildMenuDefinitions()
    {
        var view = new List<MenuAction>();
        for (int i = 0; i < NavEntries.Length; i++)
        {
            NavEntry entry = NavEntries[i];
            string hint = i < 9 ? $"  ({i + 1})" : string.Empty;
            view.Add(new MenuAction($"{entry.Label}{hint}", () => TuiShell.Navigate(entry.Id)));
        }

        return
        [
            new MenuDefinition("File",
            [
                new("Open bundle...", OpenBundlePicker),
                new("Save bundle...", SaveBundlePicker),
                new(string.Empty, null),
                new("Exit  (Ctrl+Q)", Quit),
            ]),
            new MenuDefinition("Packages",
            [
                new("Install selected  (i)", () => CurrentPackageList?.MenuOperation(OperationType.Install)),
                new("Update selected  (u)", () => CurrentPackageList?.MenuOperation(OperationType.Update)),
                new("Uninstall selected", () => CurrentPackageList?.MenuOperation(OperationType.Uninstall)),
                new(string.Empty, null),
                new("Add to bundle  (b)", () => CurrentPackageList?.MenuAddToBundle()),
                new("Copy selected  (Ctrl+C)", () => CurrentPackageList?.MenuCopySelected()),
                new("Reload", () => CurrentPackageList?.MenuReload()),
            ]),
            new MenuDefinition("View", view),
            new MenuDefinition("Tools",
            [
                new("Cancel current op  (c)", () => CurrentOperations?.MenuCancelSelected()),
                new("Clear finished  (x)", () => CurrentOperations?.MenuClearFinished()),
                new("Cancel all operations", () => CurrentOperations?.MenuCancelAll()),
                new("Copy output  (Ctrl+C)", () => CurrentOperations?.MenuCopyOutput()),
            ]),
            new MenuDefinition("Help",
            [
                new("About", () => TuiShell.Navigate("about")),
            ]),
        ];
    }

    private StackPanel BuildMenuBar()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        for (int i = 0; i < _menuDefinitions.Length; i++)
        {
            int menuIndex = i;
            var text = new TextBlock
            {
                Text = $" {_menuDefinitions[i].Header} ", Foreground = DevolutionsPalette.OnBrandBrush,
            };
            var item = new Border { Background = Brushes.Transparent, Child = text, };
            item.PointerPressed += (_, e) => OnMenuHeaderPointerPressed(menuIndex, e);
            item.PointerReleased += (_, e) => e.Handled = true;
            _menuHeaderBorders.Add(item);
            panel.Children.Add(item);
        }

        return panel;
    }

    private static Border BuildMenuDropDown(Control child)
    {
        return new Border
        {
            IsVisible = false,
            Background = DevolutionsPalette.BrandSurfaceBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            BorderBrush = new SolidColorBrush(Color.Parse("#3A3A3A")),
            Padding = new Thickness(1, 0, 1, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = child,
        };
    }

    private static Border BuildNotificationHost(TextBlock text)
    {
        return new Border
        {
            IsVisible = false,
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = new SolidColorBrush(Color.Parse("#3A3A3A")),
            Padding = new Thickness(1, 0, 1, 0),
            Child = text,
        };
    }

    private void OnNotificationRaised(TuiNotification notification)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnNotificationRaised(notification), DispatcherPriority.Background);
            return;
        }

        _notificationTimer.Stop();
        _notificationText.Text =
            $"{NotificationPrefix(notification.Severity)} {notification.Title}: {notification.Message}";
        _notificationText.Foreground = NotificationForeground(notification.Severity);
        _notificationHost.Background = NotificationBackground(notification.Severity);
        _notificationHost.IsVisible = true;
        _notificationTimer.Interval = notification.Duration;
        _notificationTimer.Start();
    }

    private void HideNotification()
    {
        _notificationTimer.Stop();
        _notificationHost.IsVisible = false;
    }

    private static string NotificationPrefix(TuiNotificationSeverity severity) => severity switch
    {
        TuiNotificationSeverity.Success => "[OK]",
        TuiNotificationSeverity.Warning => "[WARN]",
        TuiNotificationSeverity.Error => "[ERROR]",
        _ => "[INFO]",
    };

    private static IBrush NotificationForeground(TuiNotificationSeverity severity) => severity switch
    {
        TuiNotificationSeverity.Success => DevolutionsPalette.SuccessTextBrush,
        TuiNotificationSeverity.Warning => new SolidColorBrush(Color.Parse("#FFE19A")),
        TuiNotificationSeverity.Error => DevolutionsPalette.ErrorTextBrush,
        _ => DevolutionsPalette.BrandBrush,
    };

    private static IBrush NotificationBackground(TuiNotificationSeverity severity) => severity switch
    {
        TuiNotificationSeverity.Success => new SolidColorBrush(Color.Parse("#102A17")),
        TuiNotificationSeverity.Warning => new SolidColorBrush(Color.Parse("#332600")),
        TuiNotificationSeverity.Error => new SolidColorBrush(Color.Parse("#3A1010")),
        _ => new SolidColorBrush(Color.Parse("#0C2040")),
    };

    private void RenderMenu()
    {
        var activeBrush = new SolidColorBrush(DevolutionsPalette.ActionBackgroundColor);
        for (int i = 0; i < _menuHeaderBorders.Count; i++)
        {
            _menuHeaderBorders[i].Background = _menuActive && i == _openMenuIndex
                ? activeBrush
                : Brushes.Transparent;
        }

        _menuDropDownItems.Children.Clear();
        _menuActionBorders.Clear();

        if (!_menuActive || _openMenuIndex < 0)
        {
            _menuDropDown.IsVisible = false;
            return;
        }

        _menuDropDown.IsVisible = true;
        IReadOnlyList<MenuAction> actions = _menuDefinitions[_openMenuIndex].Actions;
        for (int i = 0; i < actions.Count; i++)
        {
            MenuAction action = actions[i];
            int actionIndex = i;
            var text = new TextBlock
            {
                Text = action.IsSeparator ? " ------------------------ " : $" {action.Label} ",
                Foreground = action.IsSeparator
                    ? new SolidColorBrush(Color.Parse("#BBBBBB"))
                    : DevolutionsPalette.OnBrandBrush,
            };
            var row = new Border
            {
                Background = !action.IsSeparator && i == _selectedMenuActionIndex
                    ? activeBrush
                    : Brushes.Transparent,
                Child = text,
            };
            if (!action.IsSeparator)
            {
                row.PointerPressed += (_, e) => OnMenuActionPointerPressed(actionIndex, e);
                row.PointerReleased += (_, e) => e.Handled = true;
            }

            _menuActionBorders.Add(row);
            _menuDropDownItems.Children.Add(row);
        }
    }

    private int FirstSelectableActionIndex(int menuIndex)
    {
        if (menuIndex < 0 || menuIndex >= _menuDefinitions.Length) return -1;
        IReadOnlyList<MenuAction> actions = _menuDefinitions[menuIndex].Actions;
        for (int i = 0; i < actions.Count; i++)
        {
            if (!actions[i].IsSeparator) return i;
        }

        return -1;
    }

    private int LastSelectableActionIndex(int menuIndex)
    {
        if (menuIndex < 0 || menuIndex >= _menuDefinitions.Length) return -1;
        IReadOnlyList<MenuAction> actions = _menuDefinitions[menuIndex].Actions;
        for (int i = actions.Count - 1; i >= 0; i--)
        {
            if (!actions[i].IsSeparator) return i;
        }

        return -1;
    }

    private void MoveMenuSelection(int delta)
    {
        if (_openMenuIndex < 0) return;
        IReadOnlyList<MenuAction> actions = _menuDefinitions[_openMenuIndex].Actions;
        if (actions.Count == 0) return;

        int index = _selectedMenuActionIndex;
        if (index < 0 || index >= actions.Count)
            index = FirstSelectableActionIndex(_openMenuIndex);

        for (int attempt = 0; attempt < actions.Count; attempt++)
        {
            index = (index + delta + actions.Count) % actions.Count;
            if (!actions[index].IsSeparator)
            {
                _selectedMenuActionIndex = index;
                RenderMenu();
                return;
            }
        }
    }

    private void ExecuteSelectedMenuAction()
    {
        if (_openMenuIndex < 0) return;
        IReadOnlyList<MenuAction> actions = _menuDefinitions[_openMenuIndex].Actions;
        if (_selectedMenuActionIndex < 0 || _selectedMenuActionIndex >= actions.Count) return;
        Action? execute = actions[_selectedMenuActionIndex].Execute;
        if (execute is null) return;

        CloseMenu();
        execute();
    }

    private static Control WrapMenu(Control menu)
    {
        return new Border { Background = DevolutionsPalette.BrandSurfaceBrush, Child = menu, };
    }

    private static Control BuildHeader()
    {
        var border = new Border
        {
            Background = DevolutionsPalette.BrandSurfaceBrush, Padding = new Thickness(1, 0, 1, 0),
        };
        border.Child = new TextBlock
        {
            Text = "  UniGetUI  ·  Terminal UI",
            Foreground = DevolutionsPalette.OnBrandBrush,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return border;
    }

    private static Control BuildFooter()
    {
        var border = new Border { Padding = new Thickness(1, 0, 1, 0), };
        border.Child = new TextBlock
        {
            Text =
                "↑/↓ navigate   ·   Enter open   ·   / filter   ·   Tab switch pane   ·   F10 menu   ·   Ctrl+Q quit",
            Foreground = new SolidColorBrush(Color.Parse("#C0C0C0")),
        };
        return border;
    }

    private ListBox BuildNavList()
    {
        var list = new ListBox { Width = 16, Margin = new Thickness(0, 1, 0, 0), Background = Brushes.Transparent, };

        foreach (var entry in NavEntries)
        {
            list.Items.Add(new ListBoxItem { Content = entry.Label, Tag = entry.Id });
        }

        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is ListBoxItem { Tag: string id })
            {
                _contentHost.Content = GetPage(id);
                // Navigating via the sidebar keeps the sidebar active so nav keys keep working;
                // the user presses Tab (or Down in a search box) to dive into the page.
                _activePane = ActivePane.Sidebar;
            }
        };

        list.SelectedIndex = 0;
        return list;
    }

    private static Control WrapSidebar(ListBox list)
    {
        return new Border
        {
            BorderThickness = new Thickness(0, 0, 1, 0),
            BorderBrush = new SolidColorBrush(Color.Parse("#3A3A3A")),
            Child = list,
        };
    }
}
