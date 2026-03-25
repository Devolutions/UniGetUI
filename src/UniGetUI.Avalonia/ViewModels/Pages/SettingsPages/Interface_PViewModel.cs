using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using global::Avalonia;
using global::Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class Interface_PViewModel : ViewModelBase
{
    [ObservableProperty] private string _iconCacheSizeText = "";

    public event EventHandler? RestartRequired;

    [RelayCommand]
    private void ShowRestartRequired() => RestartRequired?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private static void EditAutostartSettings()
        => CoreTools.Launch("ms-settings:startupapps");

    [RelayCommand]
    private async Task ResetIconCache(Visual? _)
    {
        try { Directory.Delete(CoreData.UniGetUICacheDirectory_Icons, true); }
        catch (Exception ex) { Logger.Error(ex); }
        RestartRequired?.Invoke(this, EventArgs.Empty);
        await LoadIconCacheSize();
    }

    public async Task LoadIconCacheSize()
    {
        double realSize = (await Task.Run(() =>
            Directory.GetFiles(CoreData.UniGetUICacheDirectory_Icons, "*", SearchOption.AllDirectories)
                     .Sum(f => new FileInfo(f).Length))) / 1048576d;
        double rounded = ((int)(realSize * 100)) / 100d;
        IconCacheSizeText = CoreTools.Translate("The local icon cache currently takes {0} MB", rounded);
    }
}
