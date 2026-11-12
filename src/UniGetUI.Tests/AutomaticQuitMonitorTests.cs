using UniGetUI.Avalonia.Infrastructure;

namespace UniGetUI.Tests;

public class AutomaticQuitMonitorTests
{
    private static readonly AutomaticQuitSnapshot Idle = new(
        IsBusy: false,
        IsEnabled: true,
        IsAlreadyQuitting: false,
        IsWindowVisible: false,
        HasUnsavedWork: false);

    private static readonly AutomaticQuitSnapshot Busy = Idle with { IsBusy = true };

    private static Task<AutomaticQuitDecision> Run(
        Queue<AutomaticQuitSnapshot?> samples,
        Action quit,
        Task? launching = null,
        Action? onSample = null)
        => AutomaticQuitMonitor.RunAsync(
            launching,
            () => Task.CompletedTask,
            decide => Task.FromResult(decide()),
            () =>
            {
                onSample?.Invoke();
                return samples.Dequeue();
            },
            quit);

    [Fact]
    public async Task QuitsAfterTwoConsecutiveIdleSamples()
    {
        var samples = new Queue<AutomaticQuitSnapshot?>([Idle, Idle]);
        int quits = 0;

        AutomaticQuitDecision decision = await Run(samples, () => quits++);

        Assert.Equal(AutomaticQuitDecision.Quit, decision);
        Assert.Equal(1, quits);
        Assert.Empty(samples);
    }

    [Fact]
    public async Task BusySampleResetsTheIdleCount()
    {
        var samples = new Queue<AutomaticQuitSnapshot?>([Idle, Busy, Idle, Busy, Idle, Idle]);
        int quits = 0;

        AutomaticQuitDecision decision = await Run(samples, () => quits++);

        Assert.Equal(AutomaticQuitDecision.Quit, decision);
        Assert.Equal(1, quits);
        Assert.Empty(samples);
    }

    [Fact]
    public async Task DoesNotSampleUntilTheLaunchingWorkCompletes()
    {
        var launching = new TaskCompletionSource();
        var samples = new Queue<AutomaticQuitSnapshot?>([Idle, Idle]);
        int sampled = 0;

        Task<AutomaticQuitDecision> run = Run(samples, () => { }, launching.Task, () => sampled++);

        Assert.False(run.IsCompleted);
        Assert.Equal(0, sampled);

        launching.SetResult();

        Assert.Equal(AutomaticQuitDecision.Quit, await run);
        Assert.Equal(2, sampled);
    }

    [Theory]
    [InlineData(false, false, false, AutomaticQuitDecision.Cancelled)]
    [InlineData(true, true, false, AutomaticQuitDecision.WindowVisible)]
    [InlineData(true, false, true, AutomaticQuitDecision.UnsavedWork)]
    public async Task RefusesToQuitWhenASafeguardApplies(
        bool enabled,
        bool windowVisible,
        bool unsavedWork,
        AutomaticQuitDecision expected)
    {
        AutomaticQuitSnapshot final = Idle with
        {
            IsEnabled = enabled,
            IsWindowVisible = windowVisible,
            HasUnsavedWork = unsavedWork,
        };
        var samples = new Queue<AutomaticQuitSnapshot?>([Idle, final]);
        int quits = 0;

        AutomaticQuitDecision decision = await Run(samples, () => quits++);

        Assert.Equal(expected, decision);
        Assert.Equal(0, quits);
    }

    [Fact]
    public async Task DoesNotQuitTwiceWhenAQuitIsAlreadyInProgress()
    {
        var samples = new Queue<AutomaticQuitSnapshot?>([Idle, Idle with { IsAlreadyQuitting = true }]);
        int quits = 0;

        AutomaticQuitDecision decision = await Run(samples, () => quits++);

        Assert.Equal(AutomaticQuitDecision.Cancelled, decision);
        Assert.Equal(0, quits);
    }

    [Fact]
    public async Task StopsWithoutQuittingWhenTheWindowIsGone()
    {
        var samples = new Queue<AutomaticQuitSnapshot?>([Idle, null]);
        int quits = 0;

        AutomaticQuitDecision decision = await Run(samples, () => quits++);

        Assert.Equal(AutomaticQuitDecision.Cancelled, decision);
        Assert.Equal(0, quits);
    }

    [Fact]
    public void SafeguardsAreNotCheckedUntilTheMachineIsIdleLongEnough()
    {
        int idleChecks = 0;
        AutomaticQuitSnapshot visible = Idle with { IsWindowVisible = true };

        Assert.Equal(AutomaticQuitDecision.KeepWaiting, AutomaticQuitMonitor.Evaluate(visible, ref idleChecks));
        Assert.Equal(AutomaticQuitDecision.WindowVisible, AutomaticQuitMonitor.Evaluate(visible, ref idleChecks));
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(false, false, true, false)]
    [InlineData(false, true, false, false)]
    [InlineData(false, true, true, true)]
    [InlineData(true, false, false, true)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, true, true)]
    public void EachKindOfPassFollowsItsOwnSetting(
        bool quitAfterUpdates,
        bool quitWhenNothingToInstall,
        bool nothingToInstall,
        bool expected)
    {
        Assert.Equal(
            expected,
            AutomaticQuitMonitor.IsQuitEnabled(quitAfterUpdates, quitWhenNothingToInstall, nothingToInstall));
    }

    [Fact]
    public void OnlyOneRequestWaitsAtATime()
    {
        var request = new AutomaticQuitRequest();

        Assert.True(request.TryBegin(nothingToInstall: false));
        Assert.False(request.TryBegin(nothingToInstall: false));

        request.End();

        Assert.True(request.TryBegin(nothingToInstall: true));
        Assert.True(request.NothingToInstall);
    }

    [Fact]
    public void APassThatInstallsUpdatesUpgradesAWaitingEmptyPass()
    {
        var request = new AutomaticQuitRequest();

        Assert.True(request.TryBegin(nothingToInstall: true));
        Assert.False(request.TryBegin(nothingToInstall: false));

        Assert.False(request.NothingToInstall);
    }

    [Fact]
    public void AnEmptyPassDoesNotDowngradeAWaitingUpdatePass()
    {
        var request = new AutomaticQuitRequest();

        Assert.True(request.TryBegin(nothingToInstall: false));
        Assert.False(request.TryBegin(nothingToInstall: true));

        Assert.False(request.NothingToInstall);
    }

    [Fact]
    public void AnUpdatePassThatWillNotQuitStillTurnsAWaitingEmptyPassIntoAnUpdatePass()
    {
        var request = new AutomaticQuitRequest();

        Assert.True(request.TryBegin(nothingToInstall: true));
        request.NoteUpdatePass();

        Assert.False(request.NothingToInstall);
        Assert.False(AutomaticQuitMonitor.IsQuitEnabled(
            quitAfterUpdates: false,
            quitWhenNothingToInstall: true,
            request.NothingToInstall));
    }

    [Fact]
    public void NotingAnUpdatePassWithoutAWaiterChangesNothing()
    {
        var request = new AutomaticQuitRequest();

        request.NoteUpdatePass();

        Assert.True(request.TryBegin(nothingToInstall: true));
        Assert.True(request.NothingToInstall);
    }

    [Fact]
    public void RequestsAbsorbedBeforeTheLastSampleAreNotReplayed()
    {
        var request = new AutomaticQuitRequest();

        Assert.True(request.TryBegin(nothingToInstall: false));
        Assert.False(request.TryBegin(nothingToInstall: true));
        request.MarkSampled();

        Assert.Null(request.End());
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void RequestsArrivingAfterTheLastSampleAreReplayed(bool first, bool second, bool expected)
    {
        var request = new AutomaticQuitRequest();

        Assert.True(request.TryBegin(nothingToInstall: true));
        request.MarkSampled();
        Assert.False(request.TryBegin(first));
        Assert.False(request.TryBegin(second));

        Assert.Equal(expected, request.End());
        Assert.True(request.TryBegin(nothingToInstall: true));
    }

    [Fact]
    public async Task ConcurrentRequestsNeverLeaveAnUpdatePassMarkedAsEmpty()
    {
        for (int i = 0; i < 2000; i++)
        {
            var request = new AutomaticQuitRequest();
            using var start = new Barrier(2);

            Task empty = Task.Run(() =>
            {
                start.SignalAndWait();
                request.TryBegin(nothingToInstall: true);
            });
            Task update = Task.Run(() =>
            {
                start.SignalAndWait();
                request.TryBegin(nothingToInstall: false);
            });
            await Task.WhenAll(empty, update);

            Assert.False(request.NothingToInstall);
        }
    }

    [Theory]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    public void AnEmptyPassQuitsOnlyAfterASuccessfulAutomaticCheck(
        bool checkSucceeded,
        bool installDue,
        bool firstUpdateAppsPass,
        bool expected)
    {
        Assert.Equal(
            expected,
            AutomaticQuitMonitor.ShouldQuitAfterEmptyPass(checkSucceeded, installDue, firstUpdateAppsPass));
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, false, false)]
    public void AnInstallPassThatLaunchedNothingNeedsASuccessfulCheck(bool launched, bool checkSucceeded, bool expected)
    {
        Assert.Equal(expected, AutomaticQuitMonitor.ShouldQuitAfterInstallPass(launched, checkSucceeded));
    }

    [Fact]
    public void TheUpdateAppsArgumentCountsOnlyOnce()
    {
        var handled = new OneTimeClaim();

        Assert.False(AutomaticQuitMonitor.IsFirstUpdateAppsPass(false, true, false, handled));
        Assert.False(AutomaticQuitMonitor.IsFirstUpdateAppsPass(true, false, false, handled));
        Assert.False(handled.IsClaimed);

        Assert.True(AutomaticQuitMonitor.IsFirstUpdateAppsPass(true, false, true, handled));
        Assert.False(AutomaticQuitMonitor.IsFirstUpdateAppsPass(true, true, false, handled));
        Assert.False(AutomaticQuitMonitor.IsFirstUpdateAppsPass(true, true, true, handled));
    }
}
