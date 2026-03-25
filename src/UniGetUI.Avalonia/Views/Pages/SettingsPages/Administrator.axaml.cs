using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class Administrator : UserControl, ISettingsPage
{
    private AdministratorViewModel VM => (AdministratorViewModel)DataContext!;

    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("Administrator rights and other dangerous settings");

    public event EventHandler? RestartRequired;
    public event EventHandler<Type>? NavigationRequested { add { } remove { } }

    public Administrator()
    {
        DataContext = new AdministratorViewModel();
        InitializeComponent();
        VM.RestartRequired += (s, e) => RestartRequired?.Invoke(s, e);
    }
}
