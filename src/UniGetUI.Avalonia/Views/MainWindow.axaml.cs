using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.Views.Pages;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;

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

        KeyDown += Window_KeyDown;
    }

    private void Window_KeyDown(object? sender, KeyEventArgs e)
    {
        bool isCtrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        bool isShift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (e.Key == Key.Tab && isCtrl)
        {
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
    }

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
        // TODO: implement tray status update
    }

    public void ShowRuntimeNotification(string title, string message, RuntimeNotificationLevel level) =>
        ShowBanner(title, message, level);

    // ─── BackgroundAPI integration ────────────────────────────────────────────
    public void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    public void QuitApplication()
    {
        (global::Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    public void OpenSharedPackage(string managerName, string packageId)
    {
        // TODO: open package details for the shared package
        Logger.Info($"OpenSharedPackage: {managerName}/{packageId}");
        Navigate(PageType.Discover);
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
