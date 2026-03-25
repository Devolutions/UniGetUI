using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.ViewModels;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class ExperimentalViewModel : ViewModelBase
{
    public event EventHandler? RestartRequired;

    [RelayCommand]
    private void ShowRestartRequired() => RestartRequired?.Invoke(this, EventArgs.Empty);
}
