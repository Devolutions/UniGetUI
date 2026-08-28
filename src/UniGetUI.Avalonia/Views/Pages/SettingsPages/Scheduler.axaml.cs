using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class Scheduler : UserControl, ISettingsPage, IDisposable
{
    private SchedulerViewModel VM => (SchedulerViewModel)DataContext!;

    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("Scheduled maintenance");

    public event EventHandler? RestartRequired { add { } remove { } }
    public event EventHandler<Type>? NavigationRequested;

    public Scheduler()
    {
        DataContext = new SchedulerViewModel();
        InitializeComponent();

        VM.NavigationRequested += (s, t) => NavigationRequested?.Invoke(s, t);
        VM.ManageAutoUpdatesRequested += async (_, _) =>
        {
            if (MainWindow.Instance is not { } win) return;
            await win.ShowManageAutoUpdatesAsync();
            VM.RefreshTasks();
        };
    }

    public void Dispose()
    {
        VM.Dispose();
    }
}
