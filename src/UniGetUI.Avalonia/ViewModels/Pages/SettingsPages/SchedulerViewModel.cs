using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Views.Pages.SettingsPages;
using UniGetUI.Core.Tools;
using UniGetUI.Core.Tools.Scheduling;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

public partial class SchedulerViewModel : ViewModelBase, IDisposable
{
    private bool _isDisposed;

    public event EventHandler<Type>? NavigationRequested;

    public ObservableCollection<ScheduledTaskViewModel> Tasks { get; } = [];

    public SchedulerViewModel()
    {
        Tasks.Add(new ScheduledTaskViewModel(
            MaintenanceTaskKind.CheckForUpdates,
            CoreTools.Translate("Check for package updates"),
            CoreTools.Translate("Look for new versions of the installed packages"),
            "reload"));

        Tasks.Add(new ScheduledTaskViewModel(
            MaintenanceTaskKind.InstallUpdates,
            CoreTools.Translate("Install available updates"),
            CoreTools.Translate("Update every upgradable package, respecting the battery and metered connection restrictions"),
            "update"));

        Tasks.Add(new ScheduledTaskViewModel(
            MaintenanceTaskKind.LocalBackup,
            CoreTools.Translate("Local package backup"),
            CoreTools.Translate("Save the list of installed packages to the local backup directory"),
            "disk"));

        Tasks.Add(new ScheduledTaskViewModel(
            MaintenanceTaskKind.CloudBackup,
            CoreTools.Translate("Cloud package backup"),
            CoreTools.Translate("Save the list of installed packages to the configured cloud backup"),
            "share"));

        MaintenanceScheduler.TaskFinished += OnTaskFinished;
    }

    private void OnTaskFinished(object? sender, MaintenanceTaskKind kind)
    {
        if (_isDisposed) return;

        foreach (var task in Tasks.Where(t => t.Kind == kind))
            task.Refresh();
    }

    [RelayCommand]
    private void NavigateToUpdates() => NavigationRequested?.Invoke(this, typeof(Updates));

    [RelayCommand]
    private void NavigateToBackup() => NavigationRequested?.Invoke(this, typeof(Backup));

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        MaintenanceScheduler.TaskFinished -= OnTaskFinished;

    }
}
