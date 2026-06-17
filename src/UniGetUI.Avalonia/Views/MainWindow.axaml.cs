using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.Views.Pages;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Avalonia.Views;

public enum PageType
{
    Discover,
    Updates,
    Installed,
    Bundles,
    Settings,
    Managers,
    OwnLog,
    ManagerLog,
    OperationHistory,
    Help,
    ReleaseNotes,
    About,
    Quit,
    Null, // Used for initializers
}

public partial class MainWindow : Window
{
    private const string FORCE_NATIVE_LINUX_DECORATIONS_ENVIRONMENT_VARIABLE = "UNIGETUI_FORCE_NATIVE_LINUX_DECORATIONS";

    // Workaround for Avalonia 12 issue #21160 / #21212: BorderOnly + ExtendClientArea
    // strips WS_CAPTION / WS_THICKFRAME, which makes DWM disable Aero Snap drag-to-top,
    // Win+Up, and the maximize/minimize/restore animations. Re-add those bits on every
    // style change. WM_GETMINMAXINFO is also overridden because Avalonia's default values
    // on the primary monitor make Aero Snap maximize to the current window size (no-op).
    // Targeted upstream fix in Avalonia 12.1.
    private const uint WM_STYLECHANGING = 0x007C;
    private const uint WM_GETMINMAXINFO = 0x0024;
    private const uint WM_NCCALCSIZE = 0x0083;
    private const int GWL_STYLE = -16;
    private const uint WS_CAPTION = 0x00C00000;
    private const uint WS_THICKFRAME = 0x00040000;
    private const uint WS_MINIMIZEBOX = 0x00020000;
    private const uint WS_MAXIMIZEBOX = 0x00010000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    // DWM attributes for the native Windows 11 Mica look: rounded corners and an
    // accent-colored window border that tracks focus.
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUND = 2;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    private bool _focusSidebarSelectionOnNextPageChange;
    private TrayService? _trayService;
    private bool _allowClose;
    private int _isQuitting;

    // Last user-chosen height (px) of the operations panel; restored when re-expanded.
    private double _operationsPanelHeight = 240;

    public enum RuntimeNotificationLevel
    {
        Progress,
        Success,
        Error,
    }

    public static MainWindow? Instance { get; private set; }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;
    public PageType CurrentPage => ViewModel.CurrentPage_t;

    public MainWindow()
    {
        Instance = this;
        DataContext = new MainWindowViewModel();
        InitializeComponent();
        SetupTitleBar();

        RestoreGeometry();

        KeyDown += Window_KeyDown;
        ViewModel.CurrentPageChanged += OnCurrentPageChanged;
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateOperationsPanelRow();

        Resized += (_, _) => _ = SaveGeometryAsync();
        PositionChanged += (_, _) => _ = SaveGeometryAsync();
        this.GetObservable(WindowStateProperty).Subscribe(state => { _ = SaveGeometryAsync(); });

        _trayService = new TrayService(this);
        _trayService.UpdateStatus();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (!OperatingSystem.IsWindows())
            return;

        // Install the hook so future style-change attempts by Avalonia can't re-strip our bits.
        Win32Properties.AddWndProcHookCallback(this, OnWindowsWndProc);

        // The initial strip already happened during Show() (before this hook could catch it),
        // so manually OR our bits back into the current style. DWM picks them up immediately
        // and starts honouring Aero Snap / Win+Up / native maximize animations again.
        if (TryGetPlatformHandle()?.Handle is { } handle && handle != 0)
        {
            nint current = NativeMethods.GetWindowLongPtr(handle, GWL_STYLE);
            nint updated = (nint)((nuint)current | WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX);
            if (updated != current)
            {
                NativeMethods.SetWindowLongPtr(handle, GWL_STYLE, updated);
                // Trigger WM_NCCALCSIZE so our hook runs against the new style.
                NativeMethods.SetWindowPos(handle, 0, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
            }
        }

        SetupMicaAndAccentBorder();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_allowClose && (OperatingSystem.IsMacOS() || !Settings.Get(Settings.K.DisableSystemTray)))
        {
            e.Cancel = true;
            Hide();
            return;
        }

        e.Cancel = true;
        QuitApplication();
    }

    private void ReleaseWindowResources()
    {
        SaveGeometryNow();
        AvaloniaAutoUpdater.ReleaseLockForAutoupdate_Window = true;
        _trayService?.Dispose();
        _trayService = null;
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        bool isCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool isShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (e.Key == Key.Tab && isCtrl)
        {
            _focusSidebarSelectionOnNextPageChange = true;
            ViewModel.NavigateTo(isShift
                ? MainWindowViewModel.GetPreviousPage(ViewModel.CurrentPage_t)
                : MainWindowViewModel.GetNextPage(ViewModel.CurrentPage_t));
        }
        else if (!isCtrl && !isShift && e.Key == Key.F1)
        {
            ViewModel.NavigateTo(PageType.Help);
        }
        else if ((e.Key is Key.Q or Key.W) && isCtrl)
        {
            Close();
        }
        else if (e.Key == Key.F5 || (e.Key == Key.R && isCtrl))
        {
            (ViewModel.CurrentPageContent as IKeyboardShortcutListener)?.ReloadTriggered();
        }
        else if (e.Key == Key.F && isCtrl)
        {
            (ViewModel.CurrentPageContent as IKeyboardShortcutListener)?.SearchTriggered();
        }
        else if (e.Key == Key.A && isCtrl)
        {
            (ViewModel.CurrentPageContent as IKeyboardShortcutListener)?.SelectAllTriggered();
        }
        else if (isCtrl && !isShift && e.Key is Key.D1 or Key.D2 or Key.D3 or Key.D4 or Key.D5 or Key.D6)
        {
            _focusSidebarSelectionOnNextPageChange = true;
            ViewModel.NavigateTo(e.Key switch
            {
                Key.D1 => PageType.Discover,
                Key.D2 => PageType.Updates,
                Key.D3 => PageType.Installed,
                Key.D4 => PageType.Bundles,
                Key.D5 => PageType.Settings,
                _ => PageType.Managers,
            });
            e.Handled = true;
        }
        else if (isCtrl && !isShift && e.Key == Key.D)
        {
            (ViewModel.CurrentPageContent as IKeyboardShortcutListener)?.DetailsTriggered();
            e.Handled = true;
        }
    }

    private void OnCurrentPageChanged(object? sender, PageType pageType)
    {
        if (!_focusSidebarSelectionOnNextPageChange)
            return;

        _focusSidebarSelectionOnNextPageChange = false;
        Dispatcher.UIThread.Post(() =>
        {
            var sidebar = this.GetVisualDescendants().OfType<SidebarView>().FirstOrDefault();
            sidebar?.FocusSelectedItem();
        }, DispatcherPriority.Background);
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.OperationsPanelExpanded)
                           or nameof(MainWindowViewModel.OperationsPanelVisible))
            UpdateOperationsPanelRow();
    }

    // Drive the operations-panel grid row: a resizable pixel height while the panel is
    // open, Auto otherwise so it collapses to just the toolbar (or vanishes when empty).
    // The GridSplitter writes the row height directly, so we read it back to preserve the
    // user's chosen size across collapse/expand.
    private void UpdateOperationsPanelRow()
    {
        if (MainContentGrid.RowDefinitions.Count < 3)
            return;

        RowDefinition row = MainContentGrid.RowDefinitions[2];
        if (ViewModel.OperationsPanelVisible && ViewModel.OperationsPanelExpanded)
        {
            row.MinHeight = 80;
            row.Height = new GridLength(_operationsPanelHeight, GridUnitType.Pixel);
        }
        else
        {
            if (row.Height.IsAbsolute && row.Height.Value > 0)
                _operationsPanelHeight = row.Height.Value;
            row.MinHeight = 0;
            row.Height = GridLength.Auto;
        }
    }

    private void SetupTitleBar()
    {
        if (OperatingSystem.IsMacOS())
        {
            // macOS: extend into the native title bar area.
            // WindowDecorationMargin.Top drives TitleBarGrid.Height via binding.
            // Traffic lights sit on the left → keep the 65 px HamburgerPanel margin.
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaTitleBarHeightHint = -1;

            // In fullscreen the native title bar is hidden and WindowDecorationMargin
            // collapses to 0, which would clip the search box and hamburger. Use a fixed
            // title bar height in that state, and drop the traffic-light reservation
            // since the traffic lights aren't shown either.
            this.GetObservable(WindowStateProperty).Subscribe(state =>
            {
                if (state == WindowState.FullScreen)
                {
                    TitleBarGrid.ClearValue(HeightProperty);
                    TitleBarGrid.Height = 44;
                    MainContentGrid.ClearValue(MarginProperty);
                    MainContentGrid.Margin = new Thickness(0, 44, 0, 0);
                    HamburgerPanel.Margin = new Thickness(10, 0, 8, 0);
                }
                else
                {
                    TitleBarGrid.Bind(HeightProperty, new Binding("WindowDecorationMargin.Top")
                    {
                        RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(Window) },
                    });
                    MainContentGrid.Bind(MarginProperty, new Binding("WindowDecorationMargin")
                    {
                        RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor) { AncestorType = typeof(Window) },
                    });
                    HamburgerPanel.Margin = new Thickness(65, 0, 8, 0);
                }
            });
        }
        else if (OperatingSystem.IsWindows())
        {
            WindowDecorations = WindowDecorations.BorderOnly;
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaTitleBarHeightHint = -1;
            // Request the Win11 Mica backdrop only when it should actually be used
            // (Windows 11 + "Transparency effects" enabled). Otherwise leave the default
            // so the window stays solid. The transparent-background switch happens in
            // SetupMicaAndAccentBorder() once the native handle exists.
            if (MicaWindowHelper.IsMicaEnabled())
                TransparencyLevelHint = new[] { WindowTransparencyLevel.Mica };
            TitleBarGrid.ClearValue(HeightProperty);
            TitleBarGrid.Height = 44;
            HamburgerPanel.Margin = new Thickness(10, 0, 8, 0);
            WindowButtons.IsVisible = true;
            MainContentGrid.Margin = new Thickness(0, 44, 0, 0);
            this.GetObservable(WindowStateProperty).Subscribe(state =>
            {
                UpdateMaximizeButtonState(state == WindowState.Maximized);
            });
        }
        else if (OperatingSystem.IsLinux())
        {
            // WSLg and SSH-forwarded X11 can report incorrect maximize/input bounds
            // with frameless windows. Keep native decorations for those environments.
            bool useNativeDecorations = ShouldUseNativeLinuxWindowDecorations(out string decorationReason);
            Logger.Info($"Linux window decorations: {(useNativeDecorations ? "native" : "custom")} ({decorationReason})");
            WindowDecorations = useNativeDecorations ? WindowDecorations.Full : WindowDecorations.None;
            TitleBarGrid.ClearValue(HeightProperty);
            TitleBarGrid.Height = 44;
            HamburgerPanel.Margin = new Thickness(10, 0, 8, 0);
            WindowButtons.IsVisible = !useNativeDecorations;
            MainContentGrid.Margin = new Thickness(0, 44, 0, 0);
            // Keep maximize icon in sync with window state
            this.GetObservable(WindowStateProperty).Subscribe(state =>
            {
                UpdateMaximizeButtonState(state == WindowState.Maximized);
            });

            // Avalonia's X11 backend treats BorderOnly as None (no decorations at all).
            // Add invisible resize grips so the user can still resize by dragging edges.
            if (!useNativeDecorations)
            {
                CreateResizeGrips();
            }
        }
    }

    private static bool ShouldUseNativeLinuxWindowDecorations(out string reason)
    {
        if (TryGetNativeLinuxDecorationsOverride(out bool forceNativeDecorations))
        {
            reason = $"{FORCE_NATIVE_LINUX_DECORATIONS_ENVIRONMENT_VARIABLE}={(forceNativeDecorations ? "true" : "false")}";
            return forceNativeDecorations;
        }

        if (IsRunningUnderWsl())
        {
            reason = "WSL environment";
            return true;
        }

        if (IsRunningUnderSshX11Forwarding())
        {
            reason = "SSH X11 forwarding";
            return true;
        }

        reason = "default Linux desktop";
        return false;
    }

    private static bool TryGetNativeLinuxDecorationsOverride(out bool forceNativeDecorations)
    {
        forceNativeDecorations = false;

        string? overrideValue = Environment.GetEnvironmentVariable(FORCE_NATIVE_LINUX_DECORATIONS_ENVIRONMENT_VARIABLE);
        if (string.IsNullOrWhiteSpace(overrideValue))
        {
            return false;
        }

        switch (overrideValue.Trim().ToLowerInvariant())
        {
            case "1":
            case "true":
            case "on":
            case "yes":
            case "enabled":
                forceNativeDecorations = true;
                return true;

            case "0":
            case "false":
            case "off":
            case "no":
            case "disabled":
                forceNativeDecorations = false;
                return true;

            default:
                Logger.Warn($"Ignoring invalid {FORCE_NATIVE_LINUX_DECORATIONS_ENVIRONMENT_VARIABLE} value '{overrideValue}'. Use true/false.");
                return false;
        }
    }

    private static bool IsRunningUnderWsl()
    {
        string? wslDistro = Environment.GetEnvironmentVariable("WSL_DISTRO_NAME");
        string? wslInterop = Environment.GetEnvironmentVariable("WSL_INTEROP");
        return !string.IsNullOrWhiteSpace(wslDistro) || !string.IsNullOrWhiteSpace(wslInterop);
    }

    private static bool IsRunningUnderSshX11Forwarding()
    {
        if (!OperatingSystem.IsLinux())
        {
            return false;
        }

        string? display = Environment.GetEnvironmentVariable("DISPLAY");
        if (string.IsNullOrWhiteSpace(display))
        {
            return false;
        }

        bool hasSshSession =
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SSH_CONNECTION")) ||
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SSH_CLIENT"));
        if (!hasSshSession)
        {
            return false;
        }

        string normalizedDisplay = display.Trim();
        if (normalizedDisplay.StartsWith(":", StringComparison.Ordinal) ||
            normalizedDisplay.StartsWith("unix/", StringComparison.OrdinalIgnoreCase) ||
            normalizedDisplay.StartsWith("unix:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return normalizedDisplay.Contains(':');
    }

    /// <summary>
    /// Creates invisible resize-grip borders at the edges and corners of the window,
    /// enabling mouse-driven resize on platforms where native decorations are absent
    /// (e.g. Linux with WindowDecorations.None).
    /// </summary>
    private void CreateResizeGrips()
    {
        if (this.Content is not Panel panel)
        {
            return;
        }

        const int edgeThickness = 5;
        const int cornerSize = 8;

        // Edge strips
        panel.Children.Add(MakeGrip(this, double.NaN, edgeThickness,
            HorizontalAlignment.Stretch, VerticalAlignment.Top,
            StandardCursorType.SizeNorthSouth, WindowEdge.North));

        panel.Children.Add(MakeGrip(this, double.NaN, edgeThickness,
            HorizontalAlignment.Stretch, VerticalAlignment.Bottom,
            StandardCursorType.SizeNorthSouth, WindowEdge.South));

        panel.Children.Add(MakeGrip(this, edgeThickness, double.NaN,
            HorizontalAlignment.Left, VerticalAlignment.Stretch,
            StandardCursorType.SizeWestEast, WindowEdge.West));

        panel.Children.Add(MakeGrip(this, edgeThickness, double.NaN,
            HorizontalAlignment.Right, VerticalAlignment.Stretch,
            StandardCursorType.SizeWestEast, WindowEdge.East));

        // Corner squares
        panel.Children.Add(MakeGrip(this, cornerSize, cornerSize,
            HorizontalAlignment.Left, VerticalAlignment.Top,
            StandardCursorType.TopLeftCorner, WindowEdge.NorthWest));

        panel.Children.Add(MakeGrip(this, cornerSize, cornerSize,
            HorizontalAlignment.Right, VerticalAlignment.Top,
            StandardCursorType.TopRightCorner, WindowEdge.NorthEast));

        panel.Children.Add(MakeGrip(this, cornerSize, cornerSize,
            HorizontalAlignment.Left, VerticalAlignment.Bottom,
            StandardCursorType.BottomLeftCorner, WindowEdge.SouthWest));

        panel.Children.Add(MakeGrip(this, cornerSize, cornerSize,
            HorizontalAlignment.Right, VerticalAlignment.Bottom,
            StandardCursorType.BottomRightCorner, WindowEdge.SouthEast));
        return;

        static Border MakeGrip(MainWindow window, double width, double height,
            HorizontalAlignment hAlign, VerticalAlignment vAlign,
            StandardCursorType cursorType, WindowEdge edge)
        {
            var grip = new Border
            {
                Width = width,
                Height = height,
                HorizontalAlignment = hAlign,
                VerticalAlignment = vAlign,
                Background = Brushes.Transparent,
                Cursor = new Cursor(cursorType),
                IsHitTestVisible = true,
            };
            grip.PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(window).Properties.IsLeftButtonPressed)
                {
                    window.BeginResizeDrag(edge, e);
                    e.Handled = true;
                }
            };
            return grip;
        }
    }

    private async Task SaveGeometryAsync()
    {
        try
        {
            int oldWidth = (int)Width;
            int oldHeight = (int)Height;
            PixelPoint oldPosition = Position;
            WindowState oldState = WindowState;
            await Task.Delay(100);

            if (oldWidth != (int)Width || oldHeight != (int)Height
                || oldPosition != Position || oldState != WindowState)
                return;

            SaveGeometryNow();
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    private void SaveGeometryNow()
    {
        try
        {
            int state = WindowState == WindowState.Maximized ? 1 : 0;
            string geometry = $"v2,{Position.X},{Position.Y},{(int)Width},{(int)Height},{state}";
            Settings.SetValue(Settings.K.WindowGeometry, geometry);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

    private void RestoreGeometry()
    {
        string geometry = Settings.GetValue(Settings.K.WindowGeometry);
        if (string.IsNullOrEmpty(geometry))
            return;

        string[] items = geometry.Split(',');
        if (items.Length is not (5 or 6))
        {
            Logger.Warn($"The restored geometry did not have a supported item count (found length was {items.Length})");
            return;
        }

        int x, y, width, height, state;
        try
        {
            if (items.Length == 6 && items[0] == "v2")
            {
                x = int.Parse(items[1]);
                y = int.Parse(items[2]);
                width = int.Parse(items[3]);
                height = int.Parse(items[4]);
                state = int.Parse(items[5]);
            }
            else
            {
                x = int.Parse(items[0]);
                y = int.Parse(items[1]);
                width = int.Parse(items[2]);
                height = int.Parse(items[3]);
                state = int.Parse(items[4]);
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Could not parse window geometry integers");
            Logger.Error(ex);
            return;
        }

        WindowStartupLocation = WindowStartupLocation.Manual;

        if (state == 1)
        {
            // Mirror WinUI behaviour: don't reapply the saved (maximized) bounds, just
            // maximize. The OS / Avalonia picks a sensible un-maximize restore size.
            WindowState = WindowState.Maximized;
        }
        else if (IsRectangleFullyVisible(x, y, width, height))
        {
            Width = width;
            Height = height;
            Position = new PixelPoint(x, y);
        }
        else
        {
            Logger.Warn("Restored geometry was outside of desktop bounds");
        }
    }

    private bool IsRectangleFullyVisible(int x, int y, int width, int height)
    {
        // Position is in screen pixels, Width/Height are DIPs. Scale width/height
        // by the DPI of the screen that contains the saved position before comparing
        // against the union of all monitor bounds (which Avalonia reports in pixels).
        var screens = Screens?.All;
        if (screens is null || screens.Count == 0)
            return true;

        int minX = int.MaxValue, minY = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue;
        double hostScaling = 1.0;
        bool foundHost = false;

        foreach (var screen in screens)
        {
            var bounds = screen.Bounds;
            if (bounds.X < minX) minX = bounds.X;
            if (bounds.Y < minY) minY = bounds.Y;
            if (bounds.X + bounds.Width > maxX) maxX = bounds.X + bounds.Width;
            if (bounds.Y + bounds.Height > maxY) maxY = bounds.Y + bounds.Height;

            if (!foundHost && bounds.Contains(new PixelPoint(x, y)))
            {
                hostScaling = screen.Scaling;
                foundHost = true;
            }
        }

        if (!foundHost)
            hostScaling = Screens?.Primary?.Scaling ?? 1.0;

        int widthPx = (int)(width * hostScaling);
        int heightPx = (int)(height * hostScaling);

        if (x + 10 < minX || x + widthPx - 10 > maxX || y + 10 < minY || y + heightPx - 10 > maxY)
            return false;

        return true;
    }

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    private void UpdateMaximizeButtonState(bool isMaximized)
    {
        MaximizeIcon.Data = Geometry.Parse(
            isMaximized
                ? "M2,0 H10 V8 H2 Z M0,2 H8 V10 H0 Z"
                : "M0,0 H10 V10 H0 Z");
        ToolTip.SetTip(
            MaximizeButton,
            CoreTools.Translate(isMaximized ? "Restore" : "Maximize"));
    }

    // Applies the Windows 11 Mica look when it's actually usable (Win11 + transparency on):
    // a transparent window so the backdrop shows, native rounded corners, and no accent
    // border (it reads as out of place on the large main window). Otherwise the window keeps
    // its solid background. Must run after the native handle exists (OnOpened); Windows-only.
    private void SetupMicaAndAccentBorder()
    {
        if (!OperatingSystem.IsWindows())
            return;

        if (TryGetPlatformHandle()?.Handle is not { } handle || handle == 0)
            return;

        if (!MicaWindowHelper.IsMicaEnabled())
        {
            // No Mica (Windows 10, transparency off, etc.): keep the solid window background.
            // Styles.WindowsMica is not merged in this case, so the surfaces stay opaque too.
            if (this.TryFindResource("AppWindowBackground", ActualThemeVariant, out var bg) && bg is IBrush brush)
                Background = brush;
            return;
        }

        // The custom NCCALCSIZE frame keeps WS_THICKFRAME, so DWM still has a frame to round.
        int corner = DWMWCP_ROUND;
        NativeMethods.DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

        // Transparent window + transparent MicaPageBackground (from Styles.WindowsMica) let
        // the backdrop show through the chrome and page area.
        Background = Brushes.Transparent;

        // Suppress the window border colour so the main window doesn't get the accent edge
        // (the dialogs keep it via MicaWindowHelper).
        int noBorder = DWMWA_COLOR_NONE;
        NativeMethods.DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref noBorder, sizeof(int));
    }

    private static nint OnWindowsWndProc(nint hWnd, uint msg, nint wParam, nint lParam, ref bool handled)
    {
        // Force client = full window rect. Avalonia's ExtendClientArea handler only overrides
        // the top inset, leaving the WS_THICKFRAME left/right/bottom resize border as glass.
        if (msg == WM_NCCALCSIZE && wParam.ToInt64() != 0)
        {
            handled = true;
            return 0;
        }

        // Intercept SetWindowLong(GWL_STYLE, ...) attempts and OR our required bits back into
        // the new style before Windows accepts the change. lParam points to a STYLESTRUCT
        // whose styleNew member is the proposed new style. We modify it in place and let the
        // chain continue (no handled=true) so Avalonia / DefWindowProc still process the
        // (now-corrected) message.
        if (msg == WM_STYLECHANGING && wParam.ToInt64() == GWL_STYLE)
        {
            var ss = Marshal.PtrToStructure<NativeMethods.STYLESTRUCT>(lParam);
            uint preserved = ss.styleNew | WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX;
            if (preserved != ss.styleNew)
            {
                ss.styleNew = preserved;
                Marshal.StructureToPtr(ss, lParam, false);
            }
        }

        // Override the max-size / max-position Avalonia would otherwise provide. On the
        // primary monitor (where the taskbar lives) Avalonia's defaults can leave ptMaxSize
        // equal to the current window size, so Aero Snap drag-to-top "maximizes" to the same
        // bounds and the window appears not to resize. We always report the current monitor's
        // work area, which is what Windows actually uses for native maximize.
        // handled = true so Avalonia's own WM_GETMINMAXINFO handler can't run after us and
        // overwrite the values we just set.
        if (msg == WM_GETMINMAXINFO)
        {
            nint monitor = NativeMethods.MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != 0)
            {
                var mi = new NativeMethods.MONITORINFO { cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
                if (NativeMethods.GetMonitorInfo(monitor, ref mi))
                {
                    var mmi = Marshal.PtrToStructure<NativeMethods.MINMAXINFO>(lParam);
                    mmi.ptMaxPosition.X = mi.rcWork.Left - mi.rcMonitor.Left;
                    mmi.ptMaxPosition.Y = mi.rcWork.Top - mi.rcMonitor.Top;
                    mmi.ptMaxSize.X = mi.rcWork.Right - mi.rcWork.Left;
                    mmi.ptMaxSize.Y = mi.rcWork.Bottom - mi.rcWork.Top;
                    if (mmi.ptMaxTrackSize.X < mmi.ptMaxSize.X) mmi.ptMaxTrackSize.X = mmi.ptMaxSize.X;
                    if (mmi.ptMaxTrackSize.Y < mmi.ptMaxSize.Y) mmi.ptMaxTrackSize.Y = mmi.ptMaxSize.Y;
                    // Set ptMinTrackSize to MinWidth/MinHeight in DIPs plus the real
                    // WS_THICKFRAME inset. Avalonia's own handler would omit the inset for
                    // BorderOnly (BorderThickness returns 0), letting the outer window shrink
                    // below the client minimum — Avalonia then grows it back via SetWindowPos
                    // pinning x, pushing the right edge → the window slides past MinWidth.
                    if (Instance is { } w)
                    {
                        uint dpi = NativeMethods.GetDpiForWindow(hWnd);
                        if (dpi == 0) dpi = 96;
                        double scale = dpi / 96.0;
                        uint style = (uint)NativeMethods.GetWindowLongPtr(hWnd, GWL_STYLE).ToInt64();

                        var frame = default(NativeMethods.RECT);
                        int frameW = 0, frameH = 0;
                        if (NativeMethods.AdjustWindowRectExForDpi(ref frame, style, false, 0, dpi))
                        {
                            frameW = (-frame.Left) + frame.Right;
                            frameH = (-frame.Top) + frame.Bottom;
                        }

                        int minX = (int)Math.Ceiling(w.MinWidth * scale) + frameW;
                        int minY = (int)Math.Ceiling(w.MinHeight * scale) + frameH;
                        if (mmi.ptMinTrackSize.X < minX) mmi.ptMinTrackSize.X = minX;
                        if (mmi.ptMinTrackSize.Y < minY) mmi.ptMinTrackSize.Y = minY;
                    }

                    Marshal.StructureToPtr(mmi, lParam, false);
                    handled = true;
                    return 0;
                }
            }
        }
        return 0;
    }

    // P/Invokes compile on any platform; they are only called from code paths guarded by
    // OperatingSystem.IsWindows(), so non-Windows targets never invoke user32.dll at runtime.
    private static class NativeMethods
    {
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        public static extern nint GetWindowLongPtr(nint hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        public static extern nint SetWindowLongPtr(nint hWnd, int nIndex, nint dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(nint hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AdjustWindowRectExForDpi(ref RECT lpRect, uint dwStyle, [MarshalAs(UnmanagedType.Bool)] bool bMenu, uint dwExStyle, uint dpi);

        [DllImport("user32.dll")]
        public static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

        [DllImport("dwmapi.dll")]
        public static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

        [StructLayout(LayoutKind.Sequential)]
        public struct STYLESTRUCT
        {
            public uint styleOld;
            public uint styleNew;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MINMAXINFO
        {
            public POINT ptReserved;
            public POINT ptMaxSize;
            public POINT ptMaxPosition;
            public POINT ptMinTrackSize;
            public POINT ptMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
        => Close();

    // Manual title-bar drag state. Only used for touch/pen on Windows (see TitleBar_PointerPressed),
    // where Avalonia's BeginMoveDrag drives an OS modal loop the finger can't feed. Bound to the
    // owning pointer so a second contact can't hijack or end an in-progress drag.
    private IPointer? _titleBarDragPointer;
    private Point _titleBarDragOrigin;
    private bool _restoreThenDrag;   // press began while maximized → restore on first real move

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Manual drag only on Windows + touch/pen. Mouse keeps the OS move (Aero Snap), and on
        // macOS/Linux the native BeginMoveDrag already handles touch (and Wayland forbids self-positioning).
        bool manualDrag = OperatingSystem.IsWindows() && e.Pointer.Type != PointerType.Mouse;

        if (!manualDrag && !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        if (!manualDrag)
        {
            BeginMoveDrag(e);
            return;
        }

        // Touch/pen on Windows: move the window manually (issue #4866).
        if (_titleBarDragPointer is not null)
            return;

        _titleBarDragPointer = e.Pointer;
        _titleBarDragOrigin = e.GetPosition(this);
        // Dragging a maximized window restores it first (matches the native mouse gesture).
        _restoreThenDrag = WindowState == WindowState.Maximized;
        e.Pointer.Capture(TitleBarDragArea);
        e.Handled = true;
    }

    private void TitleBar_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (e.Pointer != _titleBarDragPointer)
            return;

        Vector delta = e.GetPosition(this) - _titleBarDragOrigin;

        // Started on a maximized window: ignore tiny jitter (so a tap doesn't restore), then
        // restore-and-reposition under the finger before the normal drag takes over.
        if (_restoreThenDrag)
        {
            if (Math.Abs(delta.X) < 4 && Math.Abs(delta.Y) < 4)
                return;
            RestoreForTouchDrag(e.GetPosition(this));
            e.Handled = true;
            return;
        }

        if (delta.X == 0 && delta.Y == 0)
            return;

        // GetPosition is window-relative and Position is in screen pixels; scale converts between
        // them. Closed loop: the origin stays fixed and delta is re-measured against the moved
        // window each event, so the sub-pixel remainder is held in the geometry and re-applied
        // rather than accumulating. Round (not truncate) to keep the residual symmetric and <0.5px.
        double scale = RenderScaling;
        Position += new PixelVector((int)Math.Round(delta.X * scale), (int)Math.Round(delta.Y * scale));
        e.Handled = true;
    }

    // Restores a maximized window mid-drag and re-anchors it under the finger, matching the native
    // mouse gesture. The finger's screen point and its horizontal fraction of the title bar are
    // captured while still maximized; after WindowState.Normal the window is placed so that same
    // fraction of the (now restored) width sits under the finger. On Win32 the restore is
    // synchronous — ShowWindow sends WM_SIZE inline, so Bounds already reflects the restored size.
    private void RestoreForTouchDrag(Point grab)
    {
        double fraction = Bounds.Width > 0 ? Math.Clamp(grab.X / Bounds.Width, 0, 1) : 0.5;
        double titleY = grab.Y;
        PixelPoint fingerScreen = this.PointToScreen(grab);

        _restoreThenDrag = false;
        WindowState = WindowState.Normal;

        double scale = RenderScaling;
        var origin = new Point(fraction * Bounds.Width, titleY);
        Position = fingerScreen - new PixelVector(
            (int)Math.Round(origin.X * scale),
            (int)Math.Round(origin.Y * scale));
        _titleBarDragOrigin = origin;
    }

    private void TitleBar_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.Pointer != _titleBarDragPointer)
            return;

        _titleBarDragPointer = null;
        _restoreThenDrag = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void TitleBar_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (e.Pointer == _titleBarDragPointer)
        {
            _titleBarDragPointer = null;
            _restoreThenDrag = false;
        }
    }

    private void SearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            ViewModel.SubmitGlobalSearch();
    }

    // ─── Public navigation API ────────────────────────────────────────────────
    public void Navigate(PageType type) => ViewModel.NavigateTo(type);
    public void OpenManagerLogs(IPackageManager? manager = null) => ViewModel.OpenManagerLogs(manager);
    public void OpenManagerSettings(IPackageManager? manager = null) =>
        ViewModel.OpenManagerSettings(manager);
    public void ShowHelp(string uriAttachment = "") => ViewModel.ShowHelp(uriAttachment);

    /// <summary>
    /// Focuses the global search box and optionally pre-fills a character typed
    /// while the package list had focus (type-to-search).
    /// </summary>
    public void FocusGlobalSearch(string prefill = "")
    {
        if (!string.IsNullOrEmpty(prefill))
        {
            ViewModel.GlobalSearchText = prefill;
            // Place cursor at end so the user can keep typing
            GlobalSearchBox.CaretIndex = prefill.Length;
        }
        GlobalSearchBox.Focus();
    }

    // ─── Public API (legacy compat) ───────────────────────────────────────────
    public void ShowBanner(string title, string message, RuntimeNotificationLevel level)
    {
        if (level == RuntimeNotificationLevel.Progress) return;

        var severity = level switch
        {
            RuntimeNotificationLevel.Error => InfoBarSeverity.Error,
            RuntimeNotificationLevel.Success => InfoBarSeverity.Success,
            _ => InfoBarSeverity.Informational,
        };
        ViewModel.ErrorBanner.ActionButtonText = "";
        ViewModel.ErrorBanner.ActionButtonCommand = null;
        ViewModel.ErrorBanner.Title = title;
        ViewModel.ErrorBanner.Message = message;
        ViewModel.ErrorBanner.Severity = severity;
        ViewModel.ErrorBanner.IsOpen = true;
    }

    public void UpdateSystemTrayStatus() => _trayService?.UpdateStatus();

    public void ShowRuntimeNotification(string title, string message, RuntimeNotificationLevel level) =>
        ShowBanner(title, message, level);

    // ─── BackgroundAPI integration ────────────────────────────────────────────
    public void ShowFromTray()
    {
        if (!IsVisible)
            Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
    }

    public bool IsQuitting => Interlocked.CompareExchange(ref _isQuitting, 0, 0) == 1;

    public void QuitApplication()
    {
        if (Interlocked.Exchange(ref _isQuitting, 1) == 1)
            return;

        _allowClose = true;
        ReleaseWindowResources();

        if (IsVisible)
            Hide();

        _ = QuitApplicationAsync();
    }

    private static async Task QuitApplicationAsync()
    {
        Logger.Warn("Quitting UniGetUI");
        try
        {
            await AvaloniaBootstrapper.StopIpcApiAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException ex)
        {
            Logger.Warn("Timed out while stopping Avalonia IPC API during shutdown");
            Logger.Warn(ex);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }

        Environment.Exit(0);
    }

    public static void ApplyProxyVariableToProcess()
    {
        try
        {
            var proxyUri = Settings.GetProxyUrl();
            if (proxyUri is null || !Settings.Get(Settings.K.EnableProxy))
            {
                Environment.SetEnvironmentVariable("HTTP_PROXY", "", EnvironmentVariableTarget.Process);
                return;
            }

            string content;
            if (!Settings.Get(Settings.K.EnableProxyAuth))
            {
                content = proxyUri.ToString();
            }
            else
            {
                var creds = Settings.GetProxyCredentials();
                if (creds is null)
                {
                    content = proxyUri.ToString();
                }
                else
                {
                    content = $"{proxyUri.Scheme}://{Uri.EscapeDataString(creds.UserName)}"
                            + $":{Uri.EscapeDataString(creds.Password)}"
                            + $"@{proxyUri.AbsoluteUri.Replace($"{proxyUri.Scheme}://", "")}";
                }
            }

            Environment.SetEnvironmentVariable("HTTP_PROXY", content, EnvironmentVariableTarget.Process);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to apply proxy settings:");
            Logger.Error(ex);
        }
    }
}
