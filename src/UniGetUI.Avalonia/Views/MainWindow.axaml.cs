using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
    private const int SW_RESTORE = 9;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int SM_CXSIZEFRAME = 32;
    private const int SM_CYSIZEFRAME = 33;
    private const int SM_CXPADDEDBORDER = 92;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private bool _focusSidebarSelectionOnNextPageChange;
    private TrayService? _trayService;
    private bool _allowClose;
    private NativeMethods.RECT? _windowsRestoreBoundsBeforeManualMaximize;

    public enum RuntimeNotificationLevel
    {
        Progress,
        Success,
        Error,
    }

    public static MainWindow? Instance { get; private set; }

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

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
                UpdateMaximizeButtonState(state == WindowState.Maximized || _windowsRestoreBoundsBeforeManualMaximize is not null);
            });
        }
        else if (OperatingSystem.IsLinux())
        {
            // WSLg can report incorrect maximize/input bounds with frameless windows.
            // Keep native decorations there and use the in-app toolbar only.
            bool isWsl = IsRunningUnderWsl();
            WindowDecorations = isWsl ? WindowDecorations.Full : WindowDecorations.None;
            TitleBarGrid.ClearValue(HeightProperty);
            TitleBarGrid.Height = 44;
            HamburgerPanel.Margin = new Thickness(10, 0, 8, 0);
            LinuxWindowButtons.IsVisible = !isWsl;
            MainContentGrid.Margin = new Thickness(0, 44, 0, 0);
            // Keep maximize icon in sync with window state
            this.GetObservable(WindowStateProperty).Subscribe(state =>
            {
                UpdateMaximizeButtonState(state == WindowState.Maximized);
            });

            // Avalonia's X11 backend treats BorderOnly as None (no decorations at all).
            // Add invisible resize grips so the user can still resize by dragging edges.
            if (!isWsl)
            {
                CreateResizeGrips();
            }

        }
    }

    private static bool IsRunningUnderWsl()
    {
        string? wslDistro = Environment.GetEnvironmentVariable("WSL_DISTRO_NAME");
        string? wslInterop = Environment.GetEnvironmentVariable("WSL_INTEROP");
        return !string.IsNullOrWhiteSpace(wslDistro) || !string.IsNullOrWhiteSpace(wslInterop);
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
        if (OperatingSystem.IsWindows() && TryGetNativeWindowHandle() is { } handle)
        {
            ToggleWindowsManualMaximize(handle);
            return;
        }

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

    private nint? TryGetNativeWindowHandle()
    {
        var handle = TryGetPlatformHandle()?.Handle ?? 0;
        return handle == 0 ? null : handle;
    }

    private void ToggleWindowsManualMaximize(nint handle)
    {
        if (_windowsRestoreBoundsBeforeManualMaximize is { } restoreBounds)
        {
            if (SetWindowsWindowBounds(handle, restoreBounds))
            {
                _windowsRestoreBoundsBeforeManualMaximize = null;
                UpdateMaximizeButtonState(false);
            }
            return;
        }

        if (NativeMethods.IsZoomed(handle))
        {
            _ = NativeMethods.ShowWindow(handle, SW_RESTORE);
            UpdateMaximizeButtonState(false);
            return;
        }

        if (!NativeMethods.GetWindowRect(handle, out NativeMethods.RECT currentBounds))
        {
            Logger.Warn("Could not get the window bounds before maximizing.");
            return;
        }

        var monitor = NativeMethods.MonitorFromWindow(handle, MONITOR_DEFAULTTONEAREST);
        if (monitor == 0)
        {
            Logger.Warn("Could not find a monitor for the window before maximizing.");
            return;
        }

        var monitorInfo = new NativeMethods.MONITORINFO
        {
            cbSize = Marshal.SizeOf<NativeMethods.MONITORINFO>(),
        };
        if (!NativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            Logger.Warn("Could not get monitor bounds before maximizing.");
            return;
        }

        if (SetWindowsWindowBounds(handle, GetMaximizedWindowBounds(handle, monitorInfo.rcWork)))
        {
            _windowsRestoreBoundsBeforeManualMaximize = currentBounds;
            UpdateMaximizeButtonState(true);
        }
    }

    private static NativeMethods.RECT GetMaximizedWindowBounds(nint handle, NativeMethods.RECT workArea)
    {
        uint dpi = NativeMethods.GetDpiForWindow(handle);
        if (dpi == 0)
            dpi = NativeMethods.GetDpiForSystem();

        int frameX = NativeMethods.GetSystemMetricsForDpi(SM_CXSIZEFRAME, dpi)
            + NativeMethods.GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);
        int frameY = NativeMethods.GetSystemMetricsForDpi(SM_CYSIZEFRAME, dpi)
            + NativeMethods.GetSystemMetricsForDpi(SM_CXPADDEDBORDER, dpi);

        frameX = Math.Max(frameX, 8);
        frameY = Math.Max(frameY, 8);

        return new NativeMethods.RECT
        {
            Left = workArea.Left - frameX,
            Top = workArea.Top - frameY,
            Right = workArea.Right + frameX,
            Bottom = workArea.Bottom + frameY,
        };
    }

    private static bool SetWindowsWindowBounds(nint handle, NativeMethods.RECT bounds)
    {
        bool result = NativeMethods.SetWindowPos(
            handle,
            0,
            bounds.Left,
            bounds.Top,
            bounds.Right - bounds.Left,
            bounds.Bottom - bounds.Top,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        if (!result)
            Logger.Warn($"Could not set window bounds. Win32 error: {Marshal.GetLastWin32Error()}");
        return result;
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool IsZoomed(nint hWnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool ShowWindow(nint hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(nint hwnd);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForSystem();

        [DllImport("user32.dll")]
        public static extern int GetSystemMetricsForDpi(int nIndex, uint dpi);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetWindowRect(nint hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        public static extern nint MonitorFromWindow(nint hwnd, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool GetMonitorInfo(nint hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowPos(
            nint hWnd,
            nint hWndInsertAfter,
            int X,
            int Y,
            int cx,
            int cy,
            uint uFlags);

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
        (global::Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
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
