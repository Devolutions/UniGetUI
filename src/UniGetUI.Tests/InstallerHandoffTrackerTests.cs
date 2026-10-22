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

    [Fact]
    public async Task WaitForAll_KeepsWaitingPastItsTimeoutWhileAnInteractionIsOpen()
    {
        var tracker = new InstallerHandoffTracker();
        TaskCompletionSource handoff = tracker.Begin();
        IDisposable prompt = tracker.BeginInteraction();

        Task<bool> shutdownWait = tracker.WaitForAllAsync(TimeSpan.FromMilliseconds(50));
        await Task.Delay(300);
        Assert.False(shutdownWait.IsCompleted);

        prompt.Dispose();
        tracker.Complete(handoff);

        Assert.True(await shutdownWait);
    }

    [Fact]
    public async Task WaitForAll_TimesOutOnceTheInteractionHasEnded()
    {
        var tracker = new InstallerHandoffTracker();
        tracker.Begin();
        IDisposable prompt = tracker.BeginInteraction();
        prompt.Dispose();
        prompt.Dispose();

        Assert.False(await tracker.WaitForAllAsync(TimeSpan.FromMilliseconds(50)));
    }
}
