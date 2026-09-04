namespace UniGetUI.AgentPolicy.ElevatedHelper;

internal readonly record struct PolicyElevationHelperSynchronousStageResult<T>(
    bool Completed,
    T Value)
{
    public static PolicyElevationHelperSynchronousStageResult<T> TimedOut =>
        new(false, default!);
}

internal static class PolicyElevationHelperSynchronousStageRunner
{
    public static async Task<PolicyElevationHelperSynchronousStageResult<T>> RunAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken,
        Action<T>? disposeAbandonedResult = null,
        Action? cleanupAfterAbandonedWork = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<T> worker = Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return operation();
            },
            CancellationToken.None);

        try
        {
            T result = await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new(true, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Task cleanup = worker.ContinueWith(
                completed =>
                {
                    try
                    {
                        if (completed.Status == TaskStatus.RanToCompletion)
                            disposeAbandonedResult?.Invoke(completed.Result);
                        else
                            _ = completed.Exception;
                    }
                    finally
                    {
                        cleanupAfterAbandonedWork?.Invoke();
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            _ = cleanup.ContinueWith(
                static faulted => _ = faulted.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return PolicyElevationHelperSynchronousStageResult<T>.TimedOut;
        }
    }
}
