using UniGetUI.Avalonia.Infrastructure;

namespace UniGetUI.Tests;

public class InstallerHandoffTrackerTests
{
    [Fact]
    public async Task WaitForAll_ReturnsImmediatelyWhenNothingIsPending()
    {
        var tracker = new InstallerHandoffTracker();

        Assert.False(tracker.HasPending);
        Assert.True(await tracker.WaitForAllAsync(TimeSpan.FromMilliseconds(50)));
    }

    [Fact]
    public async Task EndingOneAttempt_DoesNotReleaseAnotherAttemptStillAwaitingConsent()
    {
        var tracker = new InstallerHandoffTracker();
        TaskCompletionSource manual = tracker.Begin();
        TaskCompletionSource automatic = tracker.Begin();

        tracker.Complete(automatic);

        Assert.True(tracker.HasPending);
        Assert.False(await tracker.WaitForAllAsync(TimeSpan.FromMilliseconds(100)));

        Task<bool> shutdownWait = tracker.WaitForAllAsync(TimeSpan.FromSeconds(5));
        Assert.False(shutdownWait.IsCompleted);

        tracker.Complete(manual);

        Assert.True(await shutdownWait);
        Assert.False(tracker.HasPending);
    }

    [Fact]
    public async Task CompletingTwice_IsHarmless()
    {
        var tracker = new InstallerHandoffTracker();
        TaskCompletionSource handoff = tracker.Begin();

        tracker.Complete(handoff);
        tracker.Complete(handoff);

        Assert.False(tracker.HasPending);
        Assert.True(await tracker.WaitForAllAsync(TimeSpan.FromMilliseconds(50)));
    }
}
