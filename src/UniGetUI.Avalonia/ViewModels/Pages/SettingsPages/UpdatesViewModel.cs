using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.Views.Pages.SettingsPages;
using UniGetUI.Core.Tools;
using CoreSettings = global::UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class UpdatesViewModel : ViewModelBase
{
    public event EventHandler? RestartRequired;
    public event EventHandler<Type>? NavigationRequested;

    [ObservableProperty] private bool _isAutoCheckEnabled;

    /// <summary>Items for the update interval ComboboxCard, in display/value pairs.</summary>
    public IReadOnlyList<(string Name, string Value)> IntervalItems { get; } =
    [
        (CoreTools.Translate("{0} minutes", 10), "600"),
        (CoreTools.Translate("{0} minutes", 30), "1800"),
        (CoreTools.Translate("1 hour"),           "3600"),
        (CoreTools.Translate("{0} hours", 2),    "7200"),
        (CoreTools.Translate("{0} hours", 4),    "14400"),
        (CoreTools.Translate("{0} hours", 8),    "28800"),
        (CoreTools.Translate("{0} hours", 12),   "43200"),
        (CoreTools.Translate("1 day"),            "86400"),
        (CoreTools.Translate("{0} days", 2),    "172800"),
        (CoreTools.Translate("{0} days", 3),    "259200"),
        (CoreTools.Translate("1 week"),          "604800"),
    ];

    public UpdatesViewModel()
    {
        _isAutoCheckEnabled = !CoreSettings.Get(CoreSettings.K.DisableAutoCheckforUpdates);
    }

    [RelayCommand]
    private void UpdateAutoCheckEnabled()
    {
        IsAutoCheckEnabled = !CoreSettings.Get(CoreSettings.K.DisableAutoCheckforUpdates);
    }

    [RelayCommand]
    private void ShowRestartRequired() => RestartRequired?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void NavigateToOperations() => NavigationRequested?.Invoke(this, typeof(Operations));

    [RelayCommand]
    private void NavigateToAdministrator() => NavigationRequested?.Invoke(this, typeof(Administrator));
}
