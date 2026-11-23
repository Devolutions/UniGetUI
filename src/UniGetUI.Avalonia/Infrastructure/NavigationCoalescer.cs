using UniGetUI.Avalonia.Views;

namespace UniGetUI.Avalonia.Infrastructure;

internal sealed class NavigationCoalescer
{
    private readonly Dictionary<(PageType Page, bool ToHistory), Navigation> _inFlight = [];
    private readonly Lock _lock = new();

    public async Task<bool> RunAsync(
        PageType page,
        bool toHistory,
        Func<CancellationToken, Task<bool>> navigate,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Navigation navigation;
        bool start = false;
        lock (_lock)
        {
            if (!_inFlight.TryGetValue((page, toHistory), out navigation!))
            {
                navigation = new Navigation();
                _inFlight[(page, toHistory)] = navigation;
                start = true;
            }
            navigation.Waiters++;
        }

        if (start)
            _ = RunSharedAsync((page, toHistory), navigation, navigate);

        try
        {
            return await navigation.Result.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            bool abandoned;
            lock (_lock)
            {
                abandoned = --navigation.Waiters == 0 && !navigation.Result.Task.IsCompleted;
                if (abandoned
                    && _inFlight.TryGetValue((page, toHistory), out Navigation? current)
                    && ReferenceEquals(current, navigation))
                    _inFlight.Remove((page, toHistory));
            }
            if (abandoned)
                navigation.Cancellation.Cancel();
        }
    }

    private async Task RunSharedAsync(
        (PageType, bool) key,
        Navigation navigation,
        Func<CancellationToken, Task<bool>> navigate)
    {
        try
        {
            navigation.Result.TrySetResult(await navigate(navigation.Cancellation.Token));
        }
        catch (OperationCanceledException exception)
        {
            navigation.Result.TrySetCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            navigation.Result.TrySetException(exception);
        }
        finally
        {
            lock (_lock)
            {
                if (_inFlight.TryGetValue(key, out Navigation? current) && ReferenceEquals(current, navigation))
                    _inFlight.Remove(key);
            }
        }
    }

    private sealed class Navigation
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<bool> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Waiters { get; set; }
    }
}
