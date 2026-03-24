using CommunityToolkit.Mvvm.ComponentModel;
using UniGetUI.Avalonia.ViewModels;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class NotificationsViewModel : ViewModelBase
{
    [ObservableProperty] private bool _isSystemTrayEnabled = true;
    [ObservableProperty] private bool _isNotificationsEnabled;
}
