using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Avalonia.Views.Controls.Settings;
using UniGetUI.Core.Tools;
using CoreSettings = global::UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class Backup : UserControl, ISettingsPage
{
    private BackupViewModel VM => (BackupViewModel)DataContext!;

    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("Backup and Restore");

    public event EventHandler? RestartRequired;
    public event EventHandler<Type>? NavigationRequested { add { } remove { } }

    private void ShowRestartBanner(object? sender, EventArgs e) => RestartRequired?.Invoke(this, e);

    public Backup()
    {
        DataContext = new BackupViewModel();
        InitializeComponent();

        ChangeBackupDirectory.Description = VM.BackupDirectoryLabel;
        VM.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(VM.BackupDirectoryLabel))
                ChangeBackupDirectory.Description = VM.BackupDirectoryLabel;
        };

        BuildBackupInfoCard();

        EnablePackageBackupCheckBox_LOCAL.SettingName = CoreSettings.K.EnablePackageBackup_LOCAL;
        EnablePackageBackupCheckBox_LOCAL.Text = "Periodically perform a local backup of the installed packages";
        EnablePackageBackupCheckBox_LOCAL.StateChanged += (_, _) =>
        {
            VM.IsLocalBackupEnabled = EnablePackageBackupCheckBox_LOCAL.Checked;
            ShowRestartBanner(this, EventArgs.Empty);
        };
        VM.IsLocalBackupEnabled = EnablePackageBackupCheckBox_LOCAL.Checked;

        ChangeBackupFileNameTextBox.SettingName = CoreSettings.K.ChangeBackupFileName;
        ChangeBackupFileNameTextBox.Text = "Set a custom backup file name";
        ChangeBackupFileNameTextBox.Placeholder = "Leave empty for default";

        EnableBackupTimestamping.SettingName = CoreSettings.K.EnableBackupTimestamping;
        EnableBackupTimestamping.Text = "Add a timestamp to the backup file names";
    }

    private void BuildBackupInfoCard()
    {
        var stack = new StackPanel { Orientation = Orientation.Vertical };
        foreach (var line in new[]
        {
            "The backup will include the complete list of the installed packages and their installation options. Ignored updates and skipped versions will also be saved.",
            "The backup will NOT include any binary file nor any program's saved data.",
            "The size of the backup is estimated to be less than 1MB.",
            "The backup will be performed after login.",
        })
        {
            stack.Children.Add(new TextBlock
            {
                Text = " \u25cf " + CoreTools.Translate(line),
                TextWrapping = TextWrapping.Wrap,
            });
        }
        BackupInfoCardHolder.Content = new SettingsCard
        {
            CornerRadius = new CornerRadius(8),
            Description = stack,
        };
    }
}
