using UniGetUI.Core.Logging;

namespace UniGetUI.Interface;

internal sealed class IpcSnapshotRefresher
{
    private readonly object _lock = new();
    private readonly Func<bool> _isIdle;
    private readonly Func<Task> _refresh;
    private bool _requested;
    private Task _pending = Task.CompletedTask;

    public IpcSnapshotRefresher(Func<bool> isIdle, Func<Task> refresh)
    {
        _isIdle = isIdle;
        _refresh = refresh;
    }

    public void Request()
    {
        lock (_lock)
            _requested = true;
        StartIfIdle();
    }

    public void StartIfIdle()
    {
        lock (_lock)
        {
            if (!_requested || !_isIdle())
                return;

            _requested = false;
            Task previous = _pending;
            _pending = Task.Run(async () =>
            {
                await previous;
                await RunRefreshAsync();
            });
        }
    }

    public Task WaitForPendingAsync()
    {
        lock (_lock)
            return _pending;
    }

    private async Task RunRefreshAsync()
    {
        try
        {
            await _refresh();
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not refresh package snapshots after an IPC operation");
            Logger.Warn(ex);
        }
    }
}
