using UniGetUI.Interface;

namespace UniGetUI.Tests;

public sealed class IpcSnapshotRefresherTests
{
    [Fact]
    public async Task ARequestWaitsUntilTheOperationQueueIsEmpty()
    {
        int queueLength = 2;
        int refreshes = 0;
        var refresher = new IpcSnapshotRefresher(
            () => queueLength,
            () =>
            {
                Interlocked.Increment(ref refreshes);
                return Task.CompletedTask;
            }
        );

        refresher.Request();
        refresher.Request();
        await refresher.WaitForPendingAsync();
        Assert.Equal(0, refreshes);

        queueLength = 1;
        refresher.StartIfIdle();
        await refresher.WaitForPendingAsync();
        Assert.Equal(0, refreshes);

        queueLength = 0;
        refresher.StartIfIdle();
        await refresher.WaitForPendingAsync();
        Assert.Equal(1, refreshes);

        refresher.StartIfIdle();
        await refresher.WaitForPendingAsync();
        Assert.Equal(1, refreshes);
    }

    [Fact]
    public async Task ARequestDoesNotWaitForTheRefreshButReadersDo()
    {
        var release = new TaskCompletionSource();
        bool refreshed = false;
        var refresher = new IpcSnapshotRefresher(
            () => 0,
            async () =>
            {
                await release.Task;
                refreshed = true;
            }
        );

        refresher.Request();
        Task pending = refresher.WaitForPendingAsync();
        Assert.False(pending.IsCompleted);

        release.SetResult();
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(refreshed);
    }

    [Fact]
    public async Task AFailedRefreshDoesNotFailReaders()
    {
        var refresher = new IpcSnapshotRefresher(
            () => 0,
            () => throw new InvalidOperationException("boom")
        );

        refresher.Request();
        await refresher.WaitForPendingAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
}
