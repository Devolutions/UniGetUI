using CommunityToolkit.Mvvm.ComponentModel;
using UniGetUI.Avalonia.ViewModels;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class AdministratorViewModel : ViewModelBase
{
    /// <summary>
    /// True when elevation is NOT prohibited — controls enabled-state of the cache-admin-rights cards.
    /// </summary>
    [ObservableProperty] private bool _isElevationEnabled = true;
}
