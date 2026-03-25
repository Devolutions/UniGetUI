using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.Views.Pages.SettingsPages;
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

    [RelayCommand]
    private void UpdateMaxOperations()
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
