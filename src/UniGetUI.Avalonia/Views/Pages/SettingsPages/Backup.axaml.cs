using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
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

        ((BackupViewModel)DataContext).RestartRequired += (s, e) => RestartRequired?.Invoke(s, e);
    }
}
