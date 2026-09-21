namespace UniGetUI.AgentPolicy.ElevatedHelper;

internal sealed class PolicyElevationHelperStageTimeouts : IDisposable
{
    private readonly TimeSpan _exchangeTimeout;
    private CancellationTokenSource? _stage;
    private bool _exchangeStarted;

    public PolicyElevationHelperStageTimeouts(TimeSpan connectTimeout, TimeSpan exchangeTimeout)
    {
        _exchangeTimeout = exchangeTimeout;
        _stage = new CancellationTokenSource(connectTimeout);
    }

    public CancellationToken Token =>
        _stage?.Token ?? throw new ObjectDisposedException(nameof(PolicyElevationHelperStageTimeouts));

    public void BeginExchange()
    {
        ObjectDisposedException.ThrowIf(_stage is null, this);
        if (_exchangeStarted)
            throw new InvalidOperationException("The helper exchange timeout has already started.");

        _exchangeStarted = true;
        CancellationTokenSource connectStage = _stage;
        _stage = new CancellationTokenSource(_exchangeTimeout);
        connectStage.Dispose();
    }

    public void Dispose() =>
        Interlocked.Exchange(ref _stage, null)?.Dispose();
}
