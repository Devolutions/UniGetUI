using UniGetUI.Avalonia.Infrastructure;

namespace UniGetUI.Tests;

public class IdleSelfUpdateGateTests
{
    private static readonly IdleSelfUpdateSnapshot Idle = new(
        IsEnabled: true,
        IsBusy: false,
        IsWindowVisible: false,
        HasUnsavedWork: false,
        IsQuitting: false);

    private static bool[] Run(params IdleSelfUpdateSnapshot[] samples)
    {
        int idleChecks = 0;
        return samples.Select(s => IdleSelfUpdateGate.ShouldInstall(s, ref idleChecks)).ToArray();
    }

    [Fact]
    public void InstallsAfterTwoConsecutiveIdleSamples()
    {
        Assert.Equal([false, true], Run(Idle, Idle));
    }

    [Fact]
    public void BusySampleResetsTheIdleCount()
    {
        IdleSelfUpdateSnapshot busy = Idle with { IsBusy = true };

        Assert.Equal([false, false, false, true], Run(Idle, busy, Idle, Idle));
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, false, false, true)]
    public void NeverInstallsWhenASafeguardApplies(
        bool enabled,
        bool windowVisible,
        bool unsavedWork,
        bool quitting)
    {
        IdleSelfUpdateSnapshot blocked = Idle with
        {
            IsEnabled = enabled,
            IsWindowVisible = windowVisible,
            HasUnsavedWork = unsavedWork,
            IsQuitting = quitting,
        };

        Assert.All(Run(blocked, blocked, blocked), Assert.False);
    }

    [Fact]
    public void ShowingTheWindowRestartsTheIdleCount()
    {
        IdleSelfUpdateSnapshot visible = Idle with { IsWindowVisible = true };

        Assert.Equal([false, false, false, true], Run(Idle, visible, Idle, Idle));
    }
}
