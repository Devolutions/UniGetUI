using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.PackageEngine.Enums;

namespace UniGetUI.Tests;

public class ShutdownWhenFinishedControllerTests
{
    private sealed class Harness
    {
        public readonly List<OperationStatus> Batch = [];
        public bool RestartPending;
        public readonly HashSet<int> RestartRequiredAt = [];
        public readonly List<CancellationToken> Tokens = [];
        public readonly List<bool> Countdowns = [];
        public readonly List<Func<bool>> ActiveChecks = [];
        public TaskCompletionSource<ShutdownCountdownOutcome> Pending = new();
        public int FailureReports;
        public int StateChanges;
        public readonly Queue<Action> Posted = new();
        public readonly ShutdownWhenFinishedController Controller;

        public void RunPosted()
        {
            while (Posted.TryDequeue(out Action? action))
                action();
        }

        public Harness(bool supported = true)
        {
            Controller = new ShutdownWhenFinishedController(
                supported,
                () => Batch.Select((status, i) => new BatchOperationState(status, RestartRequiredAt.Contains(i))).ToList(),
                () => RestartPending,
                (restart, active, token) =>
                {
                    Countdowns.Add(restart);
                    ActiveChecks.Add(active);
                    Tokens.Add(token);
                    return Pending.Task;
                },
                () => FailureReports++,
                Posted.Enqueue);
            Controller.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ShutdownWhenFinishedController.IsArmed)) StateChanges++;
            };
        }

        public void Set(params OperationStatus[] statuses)
        {
            Batch.Clear();
            Batch.AddRange(statuses);
            Controller.OnOperationsChanged();
        }

        public async Task FinishCountdownAsync(ShutdownCountdownOutcome outcome)
        {
            var finishing = Controller.CountdownTask;
            var current = Pending;
            Pending = new TaskCompletionSource<ShutdownCountdownOutcome>();
            current.SetResult(outcome);
            await finishing;
        }
    }

    [Fact]
    public void WithNothingRunning_TheOptionCannotBeTicked()
    {
        var h = new Harness();
        h.Batch.Add(OperationStatus.Succeeded);

        Assert.False(h.Controller.CanToggle);
    }

    [Fact]
    public void ArmingAfterEverythingFinished_StartsTheCountdownOnTheNextPass()
    {
        var h = new Harness();
        h.Batch.Add(OperationStatus.Succeeded);

        h.Controller.SetArmed(true);

        Assert.True(h.Controller.IsArmed);
        Assert.Empty(h.Countdowns);
        Assert.True(h.StateChanges >= 1);

        h.RunPosted();

        Assert.Equal([false], h.Countdowns);
        Assert.False(h.Controller.IsArmed);
    }

    [Fact]
    public void Arming_IsRefusedWhereShutdownIsUnsupported()
    {
        var h = new Harness(supported: false);
        h.Batch.Add(OperationStatus.Running);

        h.Controller.SetArmed(true);

        Assert.False(h.Controller.IsArmed);
    }

    [Fact]
    public void WhileOperationsRun_TheOptionCanBeTickedAndUnticked()
    {
        var h = new Harness();
        h.Batch.Add(OperationStatus.Running);

        Assert.True(h.Controller.CanToggle);
        h.Controller.SetArmed(true);
        Assert.True(h.Controller.IsArmed);
        h.Controller.SetArmed(false);
        Assert.False(h.Controller.IsArmed);
    }

    [Fact]
    public void FinishingTheBatch_StartsOneShutdownCountdown()
    {
        var h = new Harness();
        h.Set(OperationStatus.Running, OperationStatus.InQueue);
        h.Controller.SetArmed(true);

        h.Set(OperationStatus.Succeeded, OperationStatus.Running);
        Assert.Empty(h.Countdowns);

        h.Set(OperationStatus.Succeeded, OperationStatus.Succeeded);
        h.Set(OperationStatus.Succeeded, OperationStatus.Succeeded);

        Assert.Equal([false], h.Countdowns);
        Assert.False(h.Controller.IsArmed);
    }

    [Fact]
    public void APendingReboot_AsksForARestartCountdown()
    {
        var h = new Harness { RestartPending = true };
        h.Set(OperationStatus.Running);
        h.Controller.SetArmed(true);

        h.Set(OperationStatus.Succeeded);

        Assert.Equal([true], h.Countdowns);
    }

    [Fact]
    public void AFailedOperation_ReportsInsteadOfShuttingDown()
    {
        var h = new Harness();
        h.Set(OperationStatus.Running, OperationStatus.Running);
        h.Controller.SetArmed(true);

        h.Set(OperationStatus.Succeeded, OperationStatus.Failed);

        Assert.Empty(h.Countdowns);
        Assert.Equal(1, h.FailureReports);
        Assert.False(h.Controller.IsArmed);
    }

    [Fact]
    public void CancelAll_DisarmsBeforeTheBatchFinishes()
    {
        var h = new Harness();
        h.Set(OperationStatus.Running);
        h.Controller.SetArmed(true);

        h.Controller.SetArmed(false);
        h.Set(OperationStatus.Canceled);

        Assert.Empty(h.Countdowns);
        Assert.Equal(0, h.FailureReports);
    }

    [Fact]
    public async Task OperationsStartedDuringTheCountdown_RearmAndShutDownAfterThem()
    {
        var h = new Harness();
        h.Set(OperationStatus.Running);
        h.Controller.SetArmed(true);
        h.Set(OperationStatus.Succeeded);
        Assert.Equal([false], h.Countdowns);

        h.Batch.Add(OperationStatus.Running);
        Assert.True(h.ActiveChecks[0]());
        await h.FinishCountdownAsync(ShutdownCountdownOutcome.Deferred);

        Assert.True(h.Controller.IsArmed);
        Assert.Single(h.Countdowns);

        h.Set(OperationStatus.Succeeded, OperationStatus.Succeeded);

        Assert.Equal([false, false], h.Countdowns);
    }

    [Fact]
    public async Task StatusChangesDuringTheCountdown_DoNotStartASecondOne()
    {
        var h = new Harness();
        h.Set(OperationStatus.Running);
        h.Controller.SetArmed(true);
        h.Set(OperationStatus.Succeeded);

        h.Batch.Add(OperationStatus.Running);
        h.Controller.SetArmed(true);
        Assert.True(h.Controller.IsArmed);
        h.Set(OperationStatus.Succeeded, OperationStatus.Succeeded);

        Assert.Single(h.Countdowns);
        await h.FinishCountdownAsync(ShutdownCountdownOutcome.Canceled);
    }

    [Theory]
    [InlineData((int)ShutdownCountdownOutcome.Canceled)]
    [InlineData((int)ShutdownCountdownOutcome.Requested)]
    [InlineData((int)ShutdownCountdownOutcome.Failed)]
    public async Task AnyOtherOutcome_LeavesTheOptionOff(int outcomeValue)
    {
        var outcome = (ShutdownCountdownOutcome)outcomeValue;
        var h = new Harness();
        h.Set(OperationStatus.Running);
        h.Controller.SetArmed(true);
        h.Set(OperationStatus.Succeeded);

        await h.FinishCountdownAsync(outcome);
        h.Set(OperationStatus.Succeeded);

        Assert.False(h.Controller.IsArmed);
        Assert.Single(h.Countdowns);
    }

    [Fact]
    public void ARestartFlagOnAFinishedOperation_AsksForARestartBeforeTheStoreHasIt()
    {
        var h = new Harness();
        h.Set(OperationStatus.Running, OperationStatus.Running);
        h.Controller.SetArmed(true);

        h.RestartRequiredAt.Add(1);
        h.Set(OperationStatus.Succeeded, OperationStatus.Succeeded);

        Assert.Equal([true], h.Countdowns);
    }

    [Fact]
    public async Task WorkThatStartsAndFailsDuringTheCountdown_CancelsTheShutdown()
    {
        var h = new Harness();
        h.Set(OperationStatus.Running);
        h.Controller.SetArmed(true);
        h.Set(OperationStatus.Succeeded);
        Assert.False(h.Tokens[0].IsCancellationRequested);

        h.Set(OperationStatus.Succeeded, OperationStatus.InQueue);
        Assert.True(h.Tokens[0].IsCancellationRequested);
        await h.FinishCountdownAsync(ShutdownCountdownOutcome.Deferred);
        Assert.True(h.Controller.IsArmed);

        h.Set(OperationStatus.Succeeded, OperationStatus.Failed);

        Assert.Single(h.Countdowns);
        Assert.Equal(1, h.FailureReports);
        Assert.False(h.Controller.IsArmed);
    }

    [Fact]
    public async Task WorkThatStartsDuringTheCountdown_ReconsidersARestart()
    {
        var h = new Harness();
        h.Set(OperationStatus.Running);
        h.Controller.SetArmed(true);
        h.Set(OperationStatus.Succeeded);
        Assert.Equal([false], h.Countdowns);

        h.Set(OperationStatus.Succeeded, OperationStatus.Running);
        await h.FinishCountdownAsync(ShutdownCountdownOutcome.Deferred);
        h.RestartRequiredAt.Add(1);
        h.Set(OperationStatus.Succeeded, OperationStatus.Succeeded);

        Assert.Equal([false, true], h.Countdowns);
    }

    [Fact]
    public async Task StatusChangesWithoutNewWork_LeaveTheCountdownRunning()
    {
        var h = new Harness();
        h.Set(OperationStatus.Running);
        h.Controller.SetArmed(true);
        h.Set(OperationStatus.Succeeded);

        h.Set(OperationStatus.Succeeded);

        Assert.False(h.Tokens[0].IsCancellationRequested);
        await h.FinishCountdownAsync(ShutdownCountdownOutcome.Canceled);
    }

    [Fact]
    public async Task AnOperationThatFailsBeforeItIsSeen_StillCancelsTheCountdown()
    {
        var h = new Harness();
        h.Set(OperationStatus.Running);
        h.Controller.SetArmed(true);
        h.Set(OperationStatus.Succeeded);

        h.Batch.Clear();
        h.Batch.Add(OperationStatus.Failed);
        h.Controller.OnOperationJoined();
        Assert.True(h.Tokens[0].IsCancellationRequested);

        await h.FinishCountdownAsync(ShutdownCountdownOutcome.Deferred);

        Assert.Single(h.Countdowns);
        Assert.Equal(1, h.FailureReports);
        Assert.False(h.Controller.IsArmed);
    }

    [Fact]
    public void AnOperationJoiningOutsideACountdown_ChangesNothing()
    {
        var h = new Harness();
        h.Set(OperationStatus.Running);
        h.Controller.SetArmed(true);

        h.Controller.OnOperationJoined();

        Assert.True(h.Controller.IsArmed);
        Assert.Empty(h.Countdowns);
    }
}
