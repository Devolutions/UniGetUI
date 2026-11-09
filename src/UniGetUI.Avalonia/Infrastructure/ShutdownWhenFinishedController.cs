using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using UniGetUI.PackageEngine.Enums;

namespace UniGetUI.Avalonia.Infrastructure;

public sealed class ShutdownWhenFinishedController : ObservableObject
{
    private readonly ShutdownWhenFinishedArm _arm = new();
    private readonly Func<IReadOnlyCollection<OperationStatus>> _batchStatuses;
    private readonly Func<bool> _restartPending;
    private readonly Func<bool, Func<bool>, Task<ShutdownCountdownOutcome>> _runCountdown;
    private readonly Action _reportFailedOperations;
    private readonly Action<Action> _post;
    private bool _countdownRunning;

    internal ShutdownWhenFinishedController(
        bool isSupported,
        Func<IReadOnlyCollection<OperationStatus>> batchStatuses,
        Func<bool> restartPending,
        Func<bool, Func<bool>, Task<ShutdownCountdownOutcome>> runCountdown,
        Action reportFailedOperations,
        Action<Action>? post = null)
    {
        IsSupported = isSupported;
        _batchStatuses = batchStatuses;
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
        _batchStatuses().Any(status => status is OperationStatus.InQueue or OperationStatus.Running);

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

    internal void OnOperationsChanged()
    {
        Evaluate();
        RaiseStateChanged();
    }

    private void Evaluate()
    {
        if (_countdownRunning) return;

        switch (_arm.Evaluate(_batchStatuses(), _restartPending()))
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
        _countdownRunning = true;
        try
        {
            var outcome = await _runCountdown(restart, () => HasActiveOperations);
            if (outcome is ShutdownCountdownOutcome.Deferred)
                _arm.Arm();
        }
        finally
        {
            _countdownRunning = false;
        }

        OnOperationsChanged();
    }

    private void RaiseStateChanged()
    {
        OnPropertyChanged(nameof(IsArmed));
        OnPropertyChanged(nameof(CanToggle));
    }
}
