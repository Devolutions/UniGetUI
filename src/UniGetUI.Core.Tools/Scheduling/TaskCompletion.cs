namespace UniGetUI.Core.Tools.Scheduling;

public static class TaskCompletion
{
    public static async Task<bool> CompletesWithin(Task work, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(work);

        using var timeoutCancellation = new CancellationTokenSource();
        Task timer = Task.Delay(timeout, timeoutCancellation.Token);

        if (await Task.WhenAny(work, timer) != work)
            return false;

        await timeoutCancellation.CancelAsync();
        await work;
        return true;
    }
}
