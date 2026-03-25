using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class Updates : UserControl, ISettingsPage
{
    private UpdatesViewModel VM => (UpdatesViewModel)DataContext!;

    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("Package update preferences");

    public event EventHandler? RestartRequired;
    public event EventHandler<Type>? NavigationRequested;

    public Updates()
    {
        DataContext = new UpdatesViewModel();
        InitializeComponent();

        VM.RestartRequired += (s, e) => RestartRequired?.Invoke(s, e);
        VM.NavigationRequested += (s, t) => NavigationRequested?.Invoke(s, t);

        foreach (var (name, val) in VM.IntervalItems)
            UpdatesCheckIntervalSelector.AddItem(name, val, false);
        UpdatesCheckIntervalSelector.ShowAddedItems();
    }
}
