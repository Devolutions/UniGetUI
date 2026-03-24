using CommunityToolkit.Mvvm.ComponentModel;
using UniGetUI.Avalonia.ViewModels;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class UpdatesViewModel : ViewModelBase
{
    [ObservableProperty] private bool _isAutoCheckEnabled;
}
