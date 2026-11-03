using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Avalonia.ViewModels.Pages;

public partial class ManagerHealthViewModel : ViewModelBase
{
    public ObservableCollection<ManagerHealthRowViewModel> Managers { get; } = [];

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _hasNoMatches;

    private string _filter = "";

    private int _refreshQueued;

    public bool IsActive { get; set; }

    public ManagerHealthViewModel()
    {
        ManagerHealthStore.HealthChanged += (_, _) => QueueRefresh();
        Refresh();
    }

    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1)
            return;

        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (IsActive)
                Refresh();
        }, DispatcherPriority.Background);
    }

    public void Refresh()
    {
        var reports = PEInterface.Managers
            .Select(ManagerHealthEvaluator.Evaluate)
            .OrderByDescending(report => report.NeedsAttention)
            .ThenBy(report => report.State is ManagerHealthState.Disabled)
            .ThenBy(report => report.Manager.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var report in reports)
        {
            var row = Managers.FirstOrDefault(candidate => candidate.Manager == report.Manager);
            if (row is null)
                Managers.Add(new ManagerHealthRowViewModel(report, this));
            else
                row.Update(report);
        }

        for (int index = 0; index < reports.Length; index++)
        {
            int current = Managers.IndexOf(Managers.First(row => row.Manager == reports[index].Manager));
            if (current != index)
                Managers.Move(current, index);
        }

        ApplyFilter();

        int attention = reports.Count(report => report.NeedsAttention);
        Summary = attention == 0
            ? CoreTools.Translate("No package manager needs attention.")
            : CoreTools.Translate("{0} package manager(s) need attention.", attention);
    }

    public void SetFilter(string query)
    {
        _filter = query.Trim();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        foreach (var row in Managers)
            row.IsShown = row.Matches(_filter);
        HasNoMatches = Managers.Count > 0 && !Managers.Any(row => row.IsShown);
    }

    [RelayCommand]
    private void CheckAgain()
    {
        _ = InstalledPackagesLoader.Instance.ReloadPackages();
        _ = UpgradablePackagesLoader.Instance.ReloadPackages();
    }

    internal async Task RunActionAsync(IPackageManager manager, ManagerMaintenanceAction action)
    {
        if (
            action.RequiresConfirmation
            && !await ConfirmationDialog.ShowAsync(
                CoreTools.Translate("Run \"{0}\" on {1}?", action.Title, manager.DisplayName)
                    + "\n\n"
                    + action.Description
            )
        )
        {
            return;
        }

        try
        {
            await manager.RunAsync(action);

            MainWindow.Instance?.ShowBanner(
                CoreTools.Translate("{0} completed", action.Title),
                CoreTools.Translate("{0} will be checked again now.", manager.DisplayName),
                MainWindow.RuntimeNotificationLevel.Success
            );
            CheckAgain();
        }
        catch (Exception ex)
        {
            Logger.Error($"Maintenance action {action.Id} failed on manager {manager.Name}");
            Logger.Error(ex);
            MainWindow.Instance?.ShowBanner(
                CoreTools.Translate("{0} failed", action.Title),
                ex.Message,
                MainWindow.RuntimeNotificationLevel.Error
            );
        }
        finally
        {
            Refresh();
        }
    }
}

public partial class ManagerHealthRowViewModel : ObservableObject
{
    private readonly ManagerHealthViewModel _owner;

    public IPackageManager Manager { get; }
    public string DisplayName => Manager.DisplayName;
    public ObservableCollection<ManagerHealthActionViewModel> Actions { get; } = [];
    public ObservableCollection<ManagerListingHealthViewModel> Listings { get; } = [];

    [ObservableProperty] private string _stateLabel = "";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] private bool _isHealthy;
    [ObservableProperty] private bool _needsAttention;
    [ObservableProperty] private bool _isWarning;
    [ObservableProperty] private bool _isShown = true;

    public ManagerHealthRowViewModel(ManagerHealthReport report, ManagerHealthViewModel owner)
    {
        Manager = report.Manager;
        _owner = owner;
        Update(report);
    }

    public bool Matches(string filter) =>
        filter.Length == 0
        || new[] { DisplayName, Manager.Name, Manager.Id, StateLabel, Description }
            .Any(text => text.Contains(filter, StringComparison.OrdinalIgnoreCase));

    public void Update(ManagerHealthReport report)
    {
        StateLabel = ManagerHealthEvaluator.StateName(report.State);
        Description = ManagerHealthEvaluator.Describe(report);
        IsHealthy = report.State is ManagerHealthState.Healthy;
        NeedsAttention = report.NeedsAttention;
        IsWarning = !report.NeedsAttention
            && report.State is ManagerHealthState.Failing or ManagerHealthState.NotFound;

        var kinds = Enum.GetValues<ManagerListingKind>();
        for (int index = 0; index < kinds.Length; index++)
        {
            var listing = new ManagerListingHealthViewModel(kinds[index], report.Record[kinds[index]]);
            if (index < Listings.Count)
                Listings[index] = listing;
            else
                Listings.Add(listing);
        }

        var actions = Manager.GetMaintenanceActions();
        for (int index = Actions.Count - 1; index >= 0; index--)
        {
            if (actions.All(action => action.Id != Actions[index].Id))
                Actions.RemoveAt(index);
        }

        for (int index = 0; index < actions.Count; index++)
        {
            var action = actions[index];
            int existing = -1;
            for (int candidate = 0; candidate < Actions.Count; candidate++)
            {
                if (Actions[candidate].Id == action.Id)
                {
                    existing = candidate;
                    break;
                }
            }

            if (existing < 0)
                Actions.Insert(index, new ManagerHealthActionViewModel(action, () => _owner.RunActionAsync(Manager, action)));
            else if (existing != index)
                Actions.Move(existing, index);
        }
    }
}

public sealed class ManagerListingHealthViewModel
{
    public string Name { get; }
    public string LastSuccess { get; }
    public string LastFailure { get; }
    public string Duration { get; }
    public bool HasFailure { get; }

    public ManagerListingHealthViewModel(ManagerListingKind kind, ManagerListingHealth listing)
    {
        Name = ManagerHealthEvaluator.ListingName(kind);
        LastSuccess = listing.LastSuccessUtc is { } success
            ? CoreTools.Translate("Last success: {0}", success.ToLocalTime().ToString("g"))
            : CoreTools.Translate("Last success: never");
        HasFailure = listing.LastFailureUtc is not null;
        LastFailure = listing.LastFailureUtc is { } failure
            ? CoreTools.Translate("Last failure: {0}", failure.ToLocalTime().ToString("g"))
                + (listing.LastFailureReason is { } reason ? $" ({reason})" : "")
            : "";
        Duration = listing.RecentDurationsMs.Count > 0
            ? CoreTools.Translate(
                "Average duration: {0} s",
                (listing.RecentDurationsMs.Average() / 1000d).ToString("0.0")
            )
            : "";
    }
}

public sealed class ManagerHealthActionViewModel
{
    public string Id { get; }
    public string Title { get; }
    public string Description { get; }
    public IAsyncRelayCommand RunCommand { get; }

    public ManagerHealthActionViewModel(ManagerMaintenanceAction action, Func<Task> run)
    {
        Id = action.Id;
        Title = action.Title;
        Description = action.Description;
        RunCommand = new AsyncRelayCommand(run);
    }
}
