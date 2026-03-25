using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class Operations : UserControl, ISettingsPage
{
    private OperationsViewModel VM => (OperationsViewModel)DataContext!;

    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("Package operation preferences");

    public event EventHandler? RestartRequired;
    public event EventHandler<Type>? NavigationRequested;

    public Operations()
    {
        DataContext = new OperationsViewModel();
        InitializeComponent();

        VM.RestartRequired += (s, e) => RestartRequired?.Invoke(s, e);
        VM.NavigationRequested += (s, t) => NavigationRequested?.Invoke(s, t);

        foreach (var v in VM.ParallelOpCounts)
            ParallelOperationCount.AddItem(v, v, false);
        ParallelOperationCount.ShowAddedItems();
    }
}
