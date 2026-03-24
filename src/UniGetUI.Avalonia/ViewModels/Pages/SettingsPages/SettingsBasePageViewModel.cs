using CommunityToolkit.Mvvm.ComponentModel;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class SettingsBasePageViewModel : ViewModelBase
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private bool _isRestartBannerVisible;

    public string RestartBannerText => CoreTools.Translate("Restart UniGetUI to fully apply changes");
    public string RestartButtonText => CoreTools.Translate("Restart UniGetUI");
}
