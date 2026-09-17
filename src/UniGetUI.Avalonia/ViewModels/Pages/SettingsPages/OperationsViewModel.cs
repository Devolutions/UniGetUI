using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.Views.Pages.SettingsPages;
using UniGetUI.Core.Tools;
using UniGetUI.PackageOperations;
using CoreSettings = global::UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class OperationsViewModel : ViewModelBase
{
    public event EventHandler? RestartRequired;
    public event EventHandler<Type>? NavigationRequested;

    /// <summary>Items for the parallel operation count ComboboxCard.</summary>
    public IReadOnlyList<string> ParallelOpCounts { get; } =
        [.. Enumerable.Range(1, 10).Select(i => i.ToString()), "15", "20", "30", "50", "75", "100"];

    [ObservableProperty] private string _downloadDirectoryLabel = "";
    [ObservableProperty] private bool _isCustomDownloadDirectorySet;

    public OperationsViewModel()
    {
        RefreshDownloadDirectory();
    }

    private void RefreshDownloadDirectory()
    {
        DownloadDirectoryLabel = InstallerDownloadLocation.GetCustomDirectory()
            ?? CoreTools.Translate("Not set");
        IsCustomDownloadDirectorySet = InstallerDownloadLocation.IsCustomDirectorySet;
    }

    [RelayCommand]
    private async Task PickDownloadDirectory(Visual? visual)
    {
        if (visual is null || TopLevel.GetTopLevel(visual) is not { } topLevel) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            AllowMultiple = false,
            SuggestedStartLocation =
                await AvaloniaPackageOperationHelper.GetDefaultDownloadFolderAsync(topLevel),
        });

        if (folders is not [{ } folder]) return;
        var path = folder.TryGetLocalPath();
        if (string.IsNullOrEmpty(path)) return;

        InstallerDownloadLocation.SetCustomDirectory(path);
        RefreshDownloadDirectory();
    }

    [RelayCommand]
    private void ResetDownloadDirectory()
    {
        InstallerDownloadLocation.SetCustomDirectory(null);
        RefreshDownloadDirectory();
    }

    [RelayCommand]
    private async Task OpenDownloadDirectory()
    {
        string directory = await Task.Run(InstallerDownloadLocation.ResolveExistingDirectory);
        CoreTools.Launch(directory);
    }

    [RelayCommand]
    private static void UpdateMaxOperations()
    {
        if (int.TryParse(CoreSettings.GetValue(CoreSettings.K.ParallelOperationCount), out int value))
            AbstractOperation.MAX_OPERATIONS = value;
    }

    [RelayCommand]
    private void ShowRestartRequired() => RestartRequired?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void NavigateToUpdates() => NavigationRequested?.Invoke(this, typeof(Updates));

    [RelayCommand]
    private void NavigateToAdministrator() => NavigationRequested?.Invoke(this, typeof(Administrator));
}
