using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    private bool _focusSidebarSelectionOnNextPageChange;
    private WindowsTrayService? _trayService;
    private bool _allowClose;

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

        if (OperatingSystem.IsWindows())
        {
            _trayService = new WindowsTrayService(this);
            _trayService.UpdateStatus();
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_allowClose && OperatingSystem.IsWindows() && !Settings.Get(Settings.K.DisableSystemTray))
        {
            e.Cancel = true;
            Hide();
            return;
        }

        AvaloniaAutoUpdater.ReleaseLockForAutoupdate_Window = true;
        if (OperatingSystem.IsWindows())
        {
            _trayService?.Dispose();
            _trayService = null;
        }
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
                MaximizeIcon.Data = Geometry.Parse(
                    state == WindowState.Maximized
                        ? "M2,0 H10 V8 H2 Z M0,2 H8 V10 H0 Z"
                        : "M0,0 H10 V10 H0 Z");
                ToolTip.SetTip(
                    MaximizeButton,
                    CoreTools.Translate(state == WindowState.Maximized ? "Restore" : "Maximize"));
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
                MaximizeIcon.Data = Geometry.Parse(
                    state == WindowState.Maximized
                        ? "M2,0 H10 V8 H2 Z M0,2 H8 V10 H0 Z"  // restore: two overlapping squares
                        : "M0,0 H10 V10 H0 Z");                  // maximise: single square
                ToolTip.SetTip(
                    MaximizeButton,
                    CoreTools.Translate(state == WindowState.Maximized ? "Restore" : "Maximize"));
            });
        }
    }

    private static bool IsRunningUnderWsl()
    {
        string? wslDistro = Environment.GetEnvironmentVariable("WSL_DISTRO_NAME");
        string? wslInterop = Environment.GetEnvironmentVariable("WSL_INTEROP");
        return !string.IsNullOrWhiteSpace(wslDistro) || !string.IsNullOrWhiteSpace(wslInterop);
    }

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object? sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

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

    public void UpdateSystemTrayStatus()
    {
        if (OperatingSystem.IsWindows())
            _trayService?.UpdateStatus();
    }

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
