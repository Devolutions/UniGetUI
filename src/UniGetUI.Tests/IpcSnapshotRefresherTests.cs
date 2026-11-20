using UniGetUI.Interface;

namespace UniGetUI.Tests;

public sealed class IpcSnapshotRefresherTests
{
    [Fact]
    public async Task ARequestWaitsUntilNoOperationIsQueuedOrRunning()
    {
        bool idle = false;
        int refreshes = 0;
        var refresher = new IpcSnapshotRefresher(
            () => idle,
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

        refresher.StartIfIdle();
        await refresher.WaitForPendingAsync();
        Assert.Equal(0, refreshes);

        idle = true;
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
            () => true,
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
            () => true,
            () => throw new InvalidOperationException("boom")
        );

        refresher.Request();
        await refresher.WaitForPendingAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }
}
