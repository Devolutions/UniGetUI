using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using UniGetUI.PackageEngine.Enums;

namespace UniGetUI.Avalonia.Infrastructure;

internal readonly record struct BatchOperationState(OperationStatus Status, bool RestartRequired = false);

public sealed class ShutdownWhenFinishedController : ObservableObject
{
    private readonly ShutdownWhenFinishedArm _arm = new();
    private readonly Func<IReadOnlyCollection<BatchOperationState>> _batch;
    private readonly Func<bool> _restartPending;
    private readonly Func<bool, Func<bool>, CancellationToken, Task<ShutdownCountdownOutcome>> _runCountdown;
    private readonly Action _reportFailedOperations;
    private readonly Action<Action> _post;
    private CancellationTokenSource? _countdownCancellation;

    internal ShutdownWhenFinishedController(
        bool isSupported,
        Func<IReadOnlyCollection<BatchOperationState>> batch,
        Func<bool> restartPending,
        Func<bool, Func<bool>, CancellationToken, Task<ShutdownCountdownOutcome>> runCountdown,
        Action reportFailedOperations,
        Action<Action>? post = null)
    {
        IsSupported = isSupported;
        _batch = batch;
        _restartPending = restartPending;
        _runCountdown = runCountdown;
        _reportFailedOperations = reportFailedOperations;
        _post = post ?? (action => Dispatcher.UIThread.Post(action));
    }

    public bool IsSupported { get; }

    public bool IsArmed
    {
        get => _arm.IsArmed;
        set => SetArmed(value);
    }

    public bool CanToggle => IsArmed || HasActiveOperations;

    internal bool HasActiveOperations =>
        _batch().Any(op => op.Status is OperationStatus.InQueue or OperationStatus.Running);

    internal Task CountdownTask { get; private set; } = Task.CompletedTask;

    internal void SetArmed(bool armed)
    {
        if (armed && IsSupported)
            _arm.Arm();
        else
            _arm.Disarm();

        RaiseStateChanged();
        if (IsArmed && !HasActiveOperations)
            _post(OnOperationsChanged);
    }

    internal void OnOperationJoined() => _countdownCancellation?.Cancel();

    internal void OnOperationsChanged()
    {
        if (_countdownCancellation is { } countdown)
        {
            if (HasActiveOperations)
                countdown.Cancel();
        }
        else
        {
            Evaluate();
        }

        RaiseStateChanged();
    }

    private void Evaluate()
    {
        var batch = _batch();
        bool restartPending = _restartPending()
            || batch.Any(op => op.Status is OperationStatus.Succeeded && op.RestartRequired);

        switch (_arm.Evaluate(batch.Select(op => op.Status).ToList(), restartPending))
        {
            case ShutdownWhenFinishedDecision.ShutDown:
                CountdownTask = RunCountdownAsync(restart: false);
                break;
            case ShutdownWhenFinishedDecision.Restart:
                CountdownTask = RunCountdownAsync(restart: true);
                break;
            case ShutdownWhenFinishedDecision.CanceledByFailure:
                _reportFailedOperations();
                break;
        }
    }

    private async Task RunCountdownAsync(bool restart)
    {
        using var cancellation = new CancellationTokenSource();
        _countdownCancellation = cancellation;
        try
        {
            var outcome = await _runCountdown(restart, () => HasActiveOperations, cancellation.Token);
            if (outcome is ShutdownCountdownOutcome.Deferred)
                _arm.Arm();
        }
        finally
        {
            _countdownCancellation = null;
        }

        OnOperationsChanged();
    }

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(IsArmed));
        OnPropertyChanged(nameof(CanToggle));
    }
}
