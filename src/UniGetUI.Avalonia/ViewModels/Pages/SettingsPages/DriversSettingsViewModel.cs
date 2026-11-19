using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class DriversSettingsViewModel : ViewModelBase
{
    [ObservableProperty]
    private bool _isEnabled = AppExtensions.Drivers.IsEnabled;

    public string ElevationStatus => Settings.Get(Settings.K.ProhibitElevation)
        ? CoreTools.Translate("Administrator rights are prohibited in UniGetUI's settings, so drivers can be listed but not installed.")
        : CoreTools.Translate("Installing a driver asks for administrator rights.");

    [RelayCommand]
    private void EnabledChanged()
    {
        IsEnabled = AppExtensions.Drivers.IsEnabled;
        AppExtensions.Drivers.ApplyEnabled(IsEnabled);
    }

    [RelayCommand]
    private static void Open() => MainWindow.Instance?.Navigate(PageType.Drivers);
}
