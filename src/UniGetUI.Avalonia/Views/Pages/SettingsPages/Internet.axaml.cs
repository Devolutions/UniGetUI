using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Avalonia.Views.Controls.Settings;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using CoreSettings = global::UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class Internet : UserControl, ISettingsPage
{
    public bool CanGoBack => true;
    public string ShortTitle => CoreTools.Translate("Internet and proxy settings");

    public event EventHandler? RestartRequired;
    public event EventHandler<Type>? NavigationRequested { add { } remove { } }

    public Internet()
    {
        DataContext = new InternetViewModel();
        InitializeComponent();

        var vm = (InternetViewModel)DataContext;
        vm.RestartRequired += (s, e) => RestartRequired?.Invoke(s, e);

        CredentialsHolder.Content = vm.BuildCredentialsCard();
        ProxyCompatTableHolder.Content = vm.BuildProxyCompatTable();
    }
}
