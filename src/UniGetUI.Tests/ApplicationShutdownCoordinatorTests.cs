using UniGetUI.Avalonia.Infrastructure;

namespace UniGetUI.Tests;

public class ApplicationShutdownCoordinatorTests
{
    [Fact]
    public async Task DeclinedRestart_DoesNotScheduleRelaunchBeforeLaterOrdinaryExit()
    {
        var coordinator = new ApplicationShutdownCoordinator();
        int scheduled = 0;
        int shutdowns = 0;

        bool restarted = await coordinator.RequestAsync(
            () => Task.FromResult(false),
            () =>
            {
                shutdowns++;
                return Task.CompletedTask;
            },
            () => scheduled++);
        bool quit = await coordinator.RequestAsync(
            () => Task.FromResult(true),
            () =>
            {
                shutdowns++;
                return Task.CompletedTask;
            });

        Assert.False(restarted);
        Assert.True(quit);
        Assert.Equal(0, scheduled);
        Assert.Equal(1, shutdowns);
    }

    [Fact]
    public async Task ApprovedRestart_SchedulesExactlyOnceBetweenAuthorizationAndShutdown()
    {
        var coordinator = new ApplicationShutdownCoordinator();
        var events = new List<string>();

        bool result = await coordinator.RequestAsync(
            () =>
            {
                events.Add("authorize");
                return Task.FromResult(true);
            },
            () =>
            {
                events.Add("shutdown");
                return Task.CompletedTask;
            },
            () => events.Add("schedule"));

        Assert.True(result);
        Assert.Equal(["authorize", "schedule", "shutdown"], events);
    }

    [Fact]
    public async Task CleanRestart_SchedulesExactlyOnce()
    {
        var coordinator = new ApplicationShutdownCoordinator();
        int scheduled = 0;
        int shutdowns = 0;

        bool result = await coordinator.RequestAsync(
            () => Task.FromResult(true),
            () =>
            {
                shutdowns++;
                return Task.CompletedTask;
            },
            () => scheduled++);

        Assert.True(result);
        Assert.Equal(1, scheduled);
        Assert.Equal(1, shutdowns);
    }

    [Fact]
    public async Task ConcurrentAndRepeatedRestarts_CannotScheduleDuplicates()
    {
        var coordinator = new ApplicationShutdownCoordinator();
        var authorization = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int scheduled = 0;
        int shutdowns = 0;

        Task<bool> first = coordinator.RequestAsync(
            () => authorization.Task,
            () =>
            {
                shutdowns++;
                return Task.CompletedTask;
            },
            () => scheduled++);
        bool concurrent = await coordinator.RequestAsync(
            () => Task.FromResult(true),
            () => Task.CompletedTask,
            () => scheduled++);
        authorization.SetResult(true);
        bool initial = await first;
        bool repeated = await coordinator.RequestAsync(
            () => Task.FromResult(true),
            () => Task.CompletedTask,
            () => scheduled++);

        Assert.True(initial);
        Assert.False(concurrent);
        Assert.False(repeated);
        Assert.Equal(1, scheduled);
        Assert.Equal(1, shutdowns);
    }
}
