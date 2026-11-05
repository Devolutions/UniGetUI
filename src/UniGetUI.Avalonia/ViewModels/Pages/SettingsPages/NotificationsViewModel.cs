using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.ViewModels;
using CoreSettings = global::UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class NotificationsViewModel : ViewModelBase
{
    [ObservableProperty] private bool _isBackgroundModeEnabled;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPersistentUpdateNotificationsEnabled))]
    private bool _isNotificationsEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPersistentUpdateNotificationsEnabled))]
    private bool _isUpdatesNotificationsEnabled;

    public bool IsPersistentUpdateNotificationsEnabled => IsNotificationsEnabled && IsUpdatesNotificationsEnabled;

    /// <summary>True when closing the window quits UniGetUI, which turns notifications off.</summary>
    public bool IsSystemTrayWarningVisible => !IsBackgroundModeEnabled;

    // Per-type notifications are delivered only by the Windows/macOS bridges; Linux has no
    // delivery path, so the whole "Notification types" group is hidden there.
    public bool AreNotificationTypesSupported { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public bool IsPersistentNotificationsSupported { get; } = OperatingSystem.IsWindows();

    public NotificationsViewModel()
    {
        _isBackgroundModeEnabled = !CoreSettings.Get(CoreSettings.K.DisableSystemTray);
        _isNotificationsEnabled = !CoreSettings.Get(CoreSettings.K.DisableNotifications);
        _isUpdatesNotificationsEnabled = !CoreSettings.Get(CoreSettings.K.DisableUpdatesNotifications);
    }

    [RelayCommand]
    private void UpdateNotificationsEnabled()
    {
        IsNotificationsEnabled = !CoreSettings.Get(CoreSettings.K.DisableNotifications);
    }

    [RelayCommand]
    private void UpdateUpdatesNotificationsEnabled()
    {
        IsUpdatesNotificationsEnabled = !CoreSettings.Get(CoreSettings.K.DisableUpdatesNotifications);
    }
}
