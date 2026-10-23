using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.ViewModels;
using CoreSettings = global::UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class ExperimentalViewModel : ViewModelBase
{
    public bool IsWindows { get; } = OperatingSystem.IsWindows();

    // The Catalog Editor is a sub-option of the Software Catalog.
    [ObservableProperty]
    private bool _isSoftwareCatalogEnabled = CoreSettings.Get(CoreSettings.K.EnableSoftwareCatalog);

    public event EventHandler? RestartRequired;

    [RelayCommand]
    private void ShowRestartRequired() => RestartRequired?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void SoftwareCatalogChanged()
    {
        IsSoftwareCatalogEnabled = CoreSettings.Get(CoreSettings.K.EnableSoftwareCatalog);
        ShowRestartRequired();
    }
}
