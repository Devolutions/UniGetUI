namespace UniGetUI.Avalonia.Infrastructure;

public sealed class InstallerHandoffTracker
{
    private readonly object _lock = new();
    private readonly HashSet<TaskCompletionSource> _pending = [];
    private int _interactions;

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

    // Marks a step the user must answer, such as an administrator prompt, until disposed.
    // WaitForAllAsync does not give up on its timeout while one is open.
    public IDisposable BeginInteraction()
    {
        Interlocked.Increment(ref _interactions);
        return new Interaction(this);
    }

    public async Task<bool> WaitForAllAsync(TimeSpan timeout)
    {
        Task[] pending;
        lock (_lock)
            pending = [.. _pending.Select(handoff => handoff.Task)];

        if (pending.Length == 0)
            return true;

        while (true)
        {
            try
            {
                await Task.WhenAll(pending).WaitAsync(timeout);
                return true;
            }
            catch (TimeoutException)
            {
                if (Volatile.Read(ref _interactions) == 0)
                    return false;
            }
        }
    }

    private sealed class Interaction(InstallerHandoffTracker tracker) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref tracker._interactions);
        }
    }
}
