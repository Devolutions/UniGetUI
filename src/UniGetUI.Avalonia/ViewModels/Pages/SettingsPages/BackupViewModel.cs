using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using global::Avalonia;
using global::Avalonia.Controls;
using global::Avalonia.Platform.Storage;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Core.Data;
using CoreSettings = global::UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class BackupViewModel : ViewModelBase
{
    public event EventHandler? RestartRequired;

    [ObservableProperty] private bool _isLocalBackupEnabled;
    [ObservableProperty] private string _backupDirectoryLabel = "";

    public BackupViewModel()
    {
        _isLocalBackupEnabled = CoreSettings.Get(CoreSettings.K.EnablePackageBackup_LOCAL);
        RefreshDirectoryLabel();
    }

    [RelayCommand]
    private void EnableLocalBackupChanged()
    {
        IsLocalBackupEnabled = CoreSettings.Get(CoreSettings.K.EnablePackageBackup_LOCAL);
        RestartRequired?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshDirectoryLabel()
    {
        string dir = CoreSettings.GetValue(CoreSettings.K.ChangeBackupOutputDirectory);
        BackupDirectoryLabel = string.IsNullOrEmpty(dir) ? CoreData.UniGetUI_DefaultBackupDirectory : dir;
    }

    [RelayCommand]
    private async Task PickBackupDirectory(Visual? visual)
    {
        if (visual is null || TopLevel.GetTopLevel(visual) is not { } topLevel) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
        });
        if (folders is not [{ } folder]) return;
        var path = folder.TryGetLocalPath();
        if (path is null) return;
        CoreSettings.SetValue(CoreSettings.K.ChangeBackupOutputDirectory, path);
        RefreshDirectoryLabel();
    }

    [RelayCommand]
    private static async Task DoLocalBackup(Visual? _)
    {
        // TODO: wire up to InstalledPackagesPage.BackupPackages_LOCAL() when available
        await Task.CompletedTask;
    }
}
