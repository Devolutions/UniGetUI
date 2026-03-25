using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class SettingsBasePageViewModel : ViewModelBase
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private bool _isRestartBannerVisible;

    public event EventHandler? BackRequested;

    public string RestartBannerText => CoreTools.Translate("Restart UniGetUI to fully apply changes");
    public string RestartButtonText => CoreTools.Translate("Restart UniGetUI");

    [RelayCommand]
    private void Back() => BackRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private static void RestartApp()
    {
        var exe = Environment.ProcessPath;
        if (exe is not null)
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = true });
        (global::Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)
            ?.Shutdown();
    }
}
