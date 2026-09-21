#if WINDOWS
namespace UniGetUI.PackageEngine.AgentBroker.PolicyWriteElevation;

internal static class PolicyElevationPreflightRunner
{
    private static readonly SemaphoreSlim WorkerGate = new(1, 1);

    public static async Task<PolicyElevationPreflightResult> VerifyAsync(
        IPolicyElevationPreflight preflight,
        CancellationToken cancellationToken)
    {
        await WorkerGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        Task<PolicyElevationPreflightResult> worker;
        try
        {
            worker = Task.Run(
                () =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return preflight.Verify(cancellationToken);
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
                await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            WorkerGate.Release();
            return result;
        }
        catch
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
            throw;
        }
    }
}
#endif
