using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class SettingsHomepage : UserControl, ISettingsPage
{
    public bool CanGoBack => false;
    public string ShortTitle => CoreTools.Translate("UniGetUI Settings");

    public event EventHandler? RestartRequired { add { } remove { } }
    public event EventHandler<Type>? NavigationRequested;

    public SettingsHomepage()
    {
        DataContext = new SettingsHomepageViewModel();
        InitializeComponent();

        GeneralButton.Click += (_, _) => NavigationRequested?.Invoke(this, typeof(General));
        InterfaceButton.Click += (_, _) => NavigationRequested?.Invoke(this, typeof(Interface_P));
        NotificationsButton.Click += (_, _) => NavigationRequested?.Invoke(this, typeof(Notifications));
        UpdatesButton.Click += (_, _) => NavigationRequested?.Invoke(this, typeof(Updates));
        OperationsButton.Click += (_, _) => NavigationRequested?.Invoke(this, typeof(Operations));
        InternetButton.Click += (_, _) => NavigationRequested?.Invoke(this, typeof(Internet));
        BackupButton.Click += (_, _) => NavigationRequested?.Invoke(this, typeof(Backup));
        AdministratorButton.Click += (_, _) => NavigationRequested?.Invoke(this, typeof(Administrator));
        ExperimentalButton.Click += (_, _) => NavigationRequested?.Invoke(this, typeof(Experimental));
        ManagersButton.Click += (_, _) => NavigationRequested?.Invoke(this, typeof(ManagersHomepage));
    }
}
