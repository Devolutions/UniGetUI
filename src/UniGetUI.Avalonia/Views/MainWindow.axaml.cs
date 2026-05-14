using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
    private const int GWL_STYLE = -16;
    private const uint WS_CAPTION = 0x00C00000;
    private const uint WS_THICKFRAME = 0x00040000;
    private const uint WS_MINIMIZEBOX = 0x00020000;
    private const uint WS_MAXIMIZEBOX = 0x00010000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private bool _focusSidebarSelectionOnNextPageChange;
    private TrayService? _trayService;
    private bool _allowClose;

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

        KeyDown += Window_KeyDown;
        ViewModel.CurrentPageChanged += OnCurrentPageChanged;

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
                NativeMethods.SetWindowLongPtr(handle, GWL_STYLE, updated);
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_allowClose && !Settings.Get(Settings.K.DisableSystemTray))
        {
            e.Cancel = true;
            Hide();
            return;
        }

        AvaloniaAutoUpdater.ReleaseLockForAutoupdate_Window = true;
        _trayService?.Dispose();
        _trayService = null;
        base.OnClosing(e);
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
            TitleBarGrid.ClearValue(HeightProperty);
            TitleBarGrid.Height = 44;
            HamburgerPanel.Margin = new Thickness(10, 0, 8, 0);
            LinuxWindowButtons.IsVisible = true;
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
            LinuxWindowButtons.IsVisible = !useNativeDecorations;
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

    private static nint OnWindowsWndProc(nint hWnd, uint msg, nint wParam, nint lParam, ref bool handled)
    {
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

        [DllImport("user32.dll")]
        public static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

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

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            BeginMoveDrag(e);
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

    public void QuitApplication()
    {
        _allowClose = true;
        _ = QuitApplicationAsync();
    }

    private async Task QuitApplicationAsync()
    {
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

        Dispatcher.UIThread.Post(() =>
            (global::Avalonia.Application.Current?.ApplicationLifetime
                as IClassicDesktopStyleApplicationLifetime)?.Shutdown());
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
