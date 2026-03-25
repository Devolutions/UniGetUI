using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Avalonia.Views.Controls.Settings;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class Backup : UserControl, ISettingsPage
{
    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("Backup and Restore");

    public event EventHandler? RestartRequired;
    public event EventHandler<Type>? NavigationRequested { add { } remove { } }

    public Backup()
    {
        DataContext = new BackupViewModel();
        InitializeComponent();

        var vm = (BackupViewModel)DataContext;
        vm.RestartRequired += (s, e) => RestartRequired?.Invoke(s, e);

        BuildBackupInfoCard();
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
