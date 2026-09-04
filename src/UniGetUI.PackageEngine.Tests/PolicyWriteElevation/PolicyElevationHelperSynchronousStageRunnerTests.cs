#if WINDOWS
using UniGetUI.AgentPolicy.ElevatedHelper;

namespace UniGetUI.PackageEngine.Tests.PolicyWriteElevation;

public class PolicyElevationHelperSynchronousStageRunnerTests
{
    [Theory]
    [InlineData("protected-layout verifier")]
    [InlineData("pipe authenticator")]
    [InlineData("initiating-user resolver")]
    public async Task BlockingSecurityStage_DeadlineReturnsWithoutStartingNextStage(string stage)
    {
        using var release = new ManualResetEventSlim();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int nextStageInvocations = 0;

        PolicyElevationHelperSynchronousStageResult<string> result =
            await PolicyElevationHelperSynchronousStageRunner.RunAsync(
                () =>
                {
                    release.Wait(CancellationToken.None);
                    return stage;
                },
                timeout.Token,
                cleanupAfterAbandonedWork: () => cleanup.TrySetResult());

        if (result.Completed)
            Interlocked.Increment(ref nextStageInvocations);

        Assert.False(result.Completed);
        Assert.Equal(0, Volatile.Read(ref nextStageInvocations));
        release.Set();
        await cleanup.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task TimedOutStage_DisposesLateResult()
    {
        using var release = new ManualResetEventSlim();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var disposed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        PolicyElevationHelperSynchronousStageResult<IDisposable> result =
            await PolicyElevationHelperSynchronousStageRunner.RunAsync(
                () =>
                {
                    release.Wait(CancellationToken.None);
                    return (IDisposable)new CallbackDisposable(() => disposed.TrySetResult());
                },
                timeout.Token,
                static abandoned => abandoned.Dispose());

        Assert.False(result.Completed);
        release.Set();
        await disposed.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task NormalStage_CompletesWithoutAbandonmentCleanup()
    {
        bool cleanedUp = false;

        PolicyElevationHelperSynchronousStageResult<int> result =
            await PolicyElevationHelperSynchronousStageRunner.RunAsync(
                () => 42,
                CancellationToken.None,
                cleanupAfterAbandonedWork: () => cleanedUp = true);

        Assert.True(result.Completed);
        Assert.Equal(42, result.Value);
        Assert.False(cleanedUp);
    }

    private sealed class CallbackDisposable(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
#endif
