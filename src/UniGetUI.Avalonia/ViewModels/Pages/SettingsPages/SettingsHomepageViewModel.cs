using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.Views.Pages.SettingsPages;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class SettingsHomepageViewModel : ViewModelBase
{
    public event EventHandler<Type>? NavigationRequested;

    [RelayCommand] private void NavigateToGeneral() => NavigationRequested?.Invoke(this, typeof(General));
    [RelayCommand] private void NavigateToInterface() => NavigationRequested?.Invoke(this, typeof(Interface_P));
    [RelayCommand] private void NavigateToNotifications() => NavigationRequested?.Invoke(this, typeof(Notifications));
    [RelayCommand] private void NavigateToUpdates() => NavigationRequested?.Invoke(this, typeof(Updates));
    [RelayCommand] private void NavigateToOperations() => NavigationRequested?.Invoke(this, typeof(Operations));
    [RelayCommand] private void NavigateToInternet() => NavigationRequested?.Invoke(this, typeof(Internet));
    [RelayCommand] private void NavigateToBackup() => NavigationRequested?.Invoke(this, typeof(Backup));
    [RelayCommand] private void NavigateToAdministrator() => NavigationRequested?.Invoke(this, typeof(Administrator));
    [RelayCommand] private void NavigateToExperimental() => NavigationRequested?.Invoke(this, typeof(Experimental));
    [RelayCommand] private void NavigateToManagers() => NavigationRequested?.Invoke(this, typeof(ManagersHomepage));
}
