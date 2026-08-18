using System.Diagnostics;
using UniGetUI.Core.Tools.Scheduling;

namespace UniGetUI.Core.Tools.Tests;

public class TaskCompletionTests
{
    [Fact]
    public async Task WorkThatFinishesInTimeReportsCompletion()
    {
        Assert.True(await TaskCompletion.CompletesWithin(Task.Delay(10), TimeSpan.FromSeconds(30)));
    }

    [Fact]
    public async Task WorkThatHangsIsAbandonedInsteadOfBlockingForever()
    {
        var hung = new TaskCompletionSource();
        var stopwatch = Stopwatch.StartNew();

        bool completed = await TaskCompletion.CompletesWithin(hung.Task, TimeSpan.FromMilliseconds(200));
        stopwatch.Stop();

        Assert.False(completed);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"waited {stopwatch.Elapsed}");
        Assert.False(hung.Task.IsCompleted);

        hung.SetResult();
    }

    [Fact]
    public async Task AFailingTaskSurfacesItsExceptionRatherThanATimeout()
    {
        var failing = Task.Run(() => throw new InvalidOperationException("scheduled work failed"));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TaskCompletion.CompletesWithin(failing, TimeSpan.FromSeconds(30)));

        Assert.Equal("scheduled work failed", thrown.Message);
    }

    [Fact]
    public async Task ACancelledTaskSurfacesItsCancellation()
    {
        using var source = new CancellationTokenSource();
        await source.CancelAsync();
        var cancelled = Task.FromCanceled(source.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => TaskCompletion.CompletesWithin(cancelled, TimeSpan.FromSeconds(30)));
    }
}
