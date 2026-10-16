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
}
