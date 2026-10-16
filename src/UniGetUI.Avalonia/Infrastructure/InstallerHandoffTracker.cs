namespace UniGetUI.Avalonia.Infrastructure;

public sealed class InstallerHandoffTracker
{
    private readonly object _lock = new();
    private readonly HashSet<TaskCompletionSource> _pending = [];

    public bool HasPending
    {
        get
        {
            lock (_lock)
                return _pending.Count > 0;
        }
    }

    public TaskCompletionSource Begin()
    {
        var handoff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
            _pending.Add(handoff);
        return handoff;
    }

    public void Complete(TaskCompletionSource handoff)
    {
        handoff.TrySetResult();
        lock (_lock)
            _pending.Remove(handoff);
    }

    public async Task<bool> WaitForAllAsync(TimeSpan timeout)
    {
        Task[] pending;
        lock (_lock)
            pending = [.. _pending.Select(handoff => handoff.Task)];

        if (pending.Length == 0)
            return true;

        try
        {
            await Task.WhenAll(pending).WaitAsync(timeout);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }
}
