namespace UniGetUI.Core.Tools.Scheduling;

public static class TaskCompletion
{
    public static async Task<bool> CompletesWithin(Task work, TimeSpan timeout)
    {
        ArgumentNullException.ThrowIfNull(work);

        if (await Task.WhenAny(work, Task.Delay(timeout)) != work)
            return false;

        await work;
        return true;
    }
}
