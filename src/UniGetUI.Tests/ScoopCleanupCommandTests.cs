using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Tests;

public class ScoopCleanupCommandTests
{
    [Fact]
    public async Task TheSettingsCleanupRunsTheManagersOwnCleanupAction()
    {
        var manager = new CleanupManager();

        bool completed = await PackageManagerViewModel.RunScoopCleanupAsync(manager, _ => Task.FromResult(true));

        Assert.True(completed);
        Assert.Equal(1, manager.CleanupRuns);
        Assert.Equal(1, manager.Initializations);
    }

    [Fact]
    public async Task AFailedCleanupIsReportedInsteadOfCompleted()
    {
        var manager = new CleanupManager { Failure = new InvalidOperationException("The maintenance command exited with code 1.") };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PackageManagerViewModel.RunScoopCleanupAsync(manager, _ => Task.FromResult(true)));

        Assert.Equal("The maintenance command exited with code 1.", ex.Message);
        Assert.Equal(1, manager.CleanupRuns);
    }

    [Fact]
    public async Task ADeclinedConfirmationRunsNothing()
    {
        var manager = new CleanupManager();

        bool completed = await PackageManagerViewModel.RunScoopCleanupAsync(manager, _ => Task.FromResult(false));

        Assert.False(completed);
        Assert.Equal(0, manager.CleanupRuns);
    }

    [Fact]
    public async Task AManagerWithoutTheCleanupActionIsReportedAsNotReady()
    {
        var manager = new CleanupManager { OffersCleanup = false };
        bool asked = false;

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => PackageManagerViewModel.RunScoopCleanupAsync(manager, _ =>
            {
                asked = true;
                return Task.FromResult(true);
            }));

        Assert.False(asked);
    }

    private sealed class CleanupManager : NullPackageManager, IPackageManager
    {
        public bool OffersCleanup { get; init; } = true;
        public Exception? Failure { get; init; }
        public int CleanupRuns { get; private set; }
        public int Initializations { get; private set; }

        public new void Initialize() => Initializations++;

        public new IReadOnlyList<ManagerMaintenanceAction> GetMaintenanceActions() =>
            OffersCleanup
                ?
                [
                    new ManagerMaintenanceAction
                    {
                        Id = ManagerMaintenanceActionIds.ScoopCleanup,
                        Title = "Clear cache",
                        Description = "Remove old app versions and the download cache.",
                        RunAsync = () =>
                        {
                            CleanupRuns++;
                            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
                        },
                    },
                ]
                : [];
    }
}
