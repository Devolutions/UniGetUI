using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class Notifications : UserControl, ISettingsPage
{
    private NotificationsViewModel VM => (NotificationsViewModel)DataContext!;

    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("Notification preferences");

    public event EventHandler? RestartRequired { add { } remove { } }
    public event EventHandler<Type>? NavigationRequested { add { } remove { } }

    public Notifications()
    {
        DataContext = new NotificationsViewModel();
        InitializeComponent();

        // Assign setting names to named controls
        DisableNotifications.SettingName = Settings.K.DisableNotifications;
        DisableUpdatesNotifications.SettingName = Settings.K.DisableUpdatesNotifications;
        DisableProgressNotifications.SettingName = Settings.K.DisableProgressNotifications;
        DisableErrorNotifications.SettingName = Settings.K.DisableUpdatesNotifications;
        DisableSuccessNotifications.SettingName = Settings.K.DisableSuccessNotifications;

        DisableNotifications.Text = "Enable WingetUI notifications";
        DisableUpdatesNotifications.Text = "Show a notification when there are available updates";
        DisableProgressNotifications.Text = "Show a silent notification when an operation is running";
        DisableErrorNotifications.Text = "Show a notification when an operation fails";
        DisableSuccessNotifications.Text = "Show a notification when an operation finishes successfully";

        TrayWarningText.Text = CoreTools.Translate("The system tray icon must be enabled in order for notifications to work");

        // Mirror WinUI OnNavigatedTo: disable all when tray is off
        bool trayEnabled = !Settings.Get(Settings.K.DisableSystemTray);
        VM.IsSystemTrayEnabled = trayEnabled;

        // Set initial notifications-enabled state from the master toggle
        VM.IsNotificationsEnabled = DisableNotifications._checkbox.IsChecked ?? false;

        // React to changes on the master notifications toggle
        DisableNotifications._checkbox.IsCheckedChanged += (_, _) =>
            VM.IsNotificationsEnabled = DisableNotifications._checkbox.IsChecked ?? false;
    }
}
