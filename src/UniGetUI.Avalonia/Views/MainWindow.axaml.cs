using Avalonia.Controls;
using Avalonia.Input;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.Views.Pages;

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

    private MainWindowViewModel ViewModel => (MainWindowViewModel)DataContext!;

    public MainWindow()
    {
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
        // TODO: implement in-app notification display
    }

    public void UpdateSystemTrayStatus()
    {
        // TODO: implement tray status update
    }

    public void ShowRuntimeNotification(string title, string message, RuntimeNotificationLevel level) =>
        ShowBanner(title, message, level);
}
