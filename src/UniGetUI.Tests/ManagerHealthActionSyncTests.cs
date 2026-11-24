using System.Collections.ObjectModel;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Tests;

public sealed class ManagerHealthActionSyncTests
{
    private static ManagerMaintenanceAction Action(string id, string description) =>
        new()
        {
            Id = id,
            Title = "Uninstall casks Homebrew can no longer find",
            Description = description,
            RunAsync = () => Task.CompletedTask,
        };

    [Fact]
    public async Task AnActionWhoseTargetsChangedRunsTheNewTargets()
    {
        var rows = new ObservableCollection<ManagerHealthActionViewModel>();
        var ran = new List<string>();
        Func<ManagerMaintenanceAction, Func<Task>> run = action => () =>
        {
            ran.Add(action.Description);
            return Task.CompletedTask;
        };

        ManagerHealthRowViewModel.SyncActions(rows, [Action("clear-cache", "cache"), Action("broken", "a")], run);
        ManagerHealthActionViewModel cache = rows[0];
        ManagerHealthRowViewModel.SyncActions(rows, [Action("clear-cache", "cache"), Action("broken", "b")], run);

        Assert.Same(cache, rows[0]);
        Assert.Equal("b", rows[1].Description);
        await rows[1].RunCommand.ExecuteAsync(null);
        Assert.Equal(["b"], ran);
    }

    [Fact]
    public void ActionsFollowTheManagersOrderAndDisappearWhenGone()
    {
        var rows = new ObservableCollection<ManagerHealthActionViewModel>();
        Func<ManagerMaintenanceAction, Func<Task>> run = _ => () => Task.CompletedTask;

        ManagerHealthRowViewModel.SyncActions(rows, [Action("a", "a"), Action("b", "b")], run);
        ManagerHealthRowViewModel.SyncActions(rows, [Action("b", "b"), Action("c", "c")], run);

        Assert.Equal(["b", "c"], rows.Select(row => row.Id));
    }
}
