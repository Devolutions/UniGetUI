using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Platform.Storage;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.Views.DialogPages;
using UniGetUI.Avalonia.Views.Pages.SettingsPages;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using CoreSettings = global::UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class GeneralViewModel : ViewModelBase
{
    [RelayCommand]
    private static async Task ShowTelemetryDialog(Visual? visual)
    {
        if (visual is null || TopLevel.GetTopLevel(visual) is not Window owner) return;
        var dialog = new TelemetryDialog();
        await dialog.ShowDialog(owner);
        if (dialog.Result.HasValue)
            CoreSettings.Set(CoreSettings.K.DisableTelemetry, !dialog.Result.Value);
    }

    [RelayCommand]
    private async Task ImportSettings(Visual? visual)
    {
        if (visual is null || TopLevel.GetTopLevel(visual) is not { } topLevel) return;
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Settings JSON") { Patterns = ["*.json"] }],
        });
        if (files is not [{ } file]) return;
        var path = file.TryGetLocalPath();
        if (path is null) return;
        await Task.Run(() => CoreSettings.ImportFromFile_JSON(path));
        OnRestartRequired();
    }

    [RelayCommand]
    private static async Task ExportSettings(Visual? visual)
    {
        if (visual is null || TopLevel.GetTopLevel(visual) is not { } topLevel) return;
        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = CoreTools.Translate("UniGetUI Settings") + ".json",
            FileTypeChoices = [new FilePickerFileType("Settings JSON") { Patterns = ["*.json"] }],
        });
        if (file is null) return;
        var path = file.TryGetLocalPath();
        if (path is null) return;
        try { await Task.Run(() => CoreSettings.ExportToFile_JSON(path)); }
        catch (Exception ex) { Logger.Error(ex); }
    }

    [RelayCommand]
    private void ResetSettings(Visual? _)
    {
        try { CoreSettings.ResetSettings(); }
        catch (Exception ex) { Logger.Error(ex); }
        OnRestartRequired();
    }

    public event EventHandler? RestartRequired;
    public event EventHandler<Type>? NavigationRequested;

    private void OnRestartRequired() => RestartRequired?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void ShowRestartRequired() => RestartRequired?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void NavigateToInterface() => NavigationRequested?.Invoke(this, typeof(Interface_P));
}
