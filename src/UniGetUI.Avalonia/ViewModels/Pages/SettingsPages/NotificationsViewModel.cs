using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.ViewModels;
using CoreSettings = global::UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class NotificationsViewModel : ViewModelBase
{
    [ObservableProperty] private bool _isSystemTrayEnabled;
    [ObservableProperty] private bool _isNotificationsEnabled;

    /// <summary>True when the system-tray-disabled warning should be shown (Windows only).</summary>
    public bool IsSystemTrayWarningVisible => OperatingSystem.IsWindows() && !IsSystemTrayEnabled;

    public NotificationsViewModel()
    {
        _isSystemTrayEnabled = !CoreSettings.Get(CoreSettings.K.DisableSystemTray);
        _isNotificationsEnabled = !CoreSettings.Get(CoreSettings.K.DisableNotifications);
    }

    [RelayCommand]
    private void UpdateNotificationsEnabled()
    {
        IsNotificationsEnabled = !CoreSettings.Get(CoreSettings.K.DisableNotifications);
    }
}
