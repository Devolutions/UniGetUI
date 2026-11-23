using UniGetUI.Avalonia.Views;

namespace UniGetUI.Avalonia.Infrastructure;

internal sealed class NavigationCoalescer
{
    private (PageType Page, bool ToHistory, Task<bool> Result)? _inFlight;

    public async Task<bool> RunAsync(
        PageType page,
        bool toHistory,
        Func<Task<bool>> navigate,
        CancellationToken cancellationToken = default)
    {
        if (_inFlight is { } inFlight && inFlight.Page == page && inFlight.ToHistory == toHistory)
            return await inFlight.Result.WaitAsync(cancellationToken);

        Task<bool> navigation = navigate();
        _inFlight = (page, toHistory, navigation);
        try
        {
            return await navigation;
        }
        finally
        {
            if (_inFlight is { } current && ReferenceEquals(current.Result, navigation))
                _inFlight = null;
        }
    }
}
