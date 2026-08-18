using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
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
    }

    public void Dispose()
    {
        VM.Dispose();
    }
}
