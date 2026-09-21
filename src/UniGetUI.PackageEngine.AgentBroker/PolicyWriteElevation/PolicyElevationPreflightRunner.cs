#if WINDOWS
namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

internal static class PolicyElevationPreflightRunner
{
    private static readonly SemaphoreSlim WorkerGate = new(1, 1);

    public static Task<PolicyElevationPreflightResult> VerifyAsync(
        IPolicyElevationPreflight preflight,
        CancellationToken cancellationToken) =>
        VerifyAsync(preflight, PolicyElevationProtocol.PreflightTimeout, cancellationToken);

    public static async Task<PolicyElevationPreflightResult> VerifyAsync(
        IPolicyElevationPreflight preflight,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        using var deadline = new CancellationTokenSource(timeout);
        using CancellationTokenSource boundedCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            await WorkerGate.WaitAsync(boundedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return TimedOut();
        }

        Task<PolicyElevationPreflightResult> worker;
        try
        {
            worker = Task.Run(
                () =>
                {
                    boundedCancellation.Token.ThrowIfCancellationRequested();
                    return preflight.Verify(boundedCancellation.Token);
                },
                CancellationToken.None);
        }
        catch
        {
            WorkerGate.Release();
            throw;
        }

        try
        {
            PolicyElevationPreflightResult result =
                await worker.WaitAsync(boundedCancellation.Token).ConfigureAwait(false);
            WorkerGate.Release();
            return result;
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            ReleaseGateAfterWorker(worker);
            return TimedOut();
        }
        catch
        {
            ReleaseGateAfterWorker(worker);
            throw;
        }
    }

    private static PolicyElevationPreflightResult TimedOut()
    {
        const string reason = "Security verification of the packaged policy write helper timed out.";
        return PolicyElevationPreflightResult.Rejected(
            PolicyElevationHelperLocation.NotFound(reason),
            PolicyElevationPreflightFailureKind.TimedOut,
            reason);
    }

    private static void ReleaseGateAfterWorker(Task<PolicyElevationPreflightResult> worker)
    {
        Task release = worker.ContinueWith(
            static completed =>
            {
                try
                {
                    if (completed.Status == TaskStatus.RanToCompletion)
                        completed.Result.Dispose();
                    else
                        _ = completed.Exception;
                }
                finally
                {
                    WorkerGate.Release();
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        _ = release.ContinueWith(
            static faulted => _ = faulted.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
#endif
