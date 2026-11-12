using UniGetUI.PackageEngine.Enums;

namespace UniGetUI.Avalonia.Infrastructure;

internal enum ShutdownWhenFinishedDecision
{
    None,
    ShutDown,
    Restart,
    CanceledByFailure,
}

internal sealed class ShutdownWhenFinishedArm
{
    public bool IsArmed { get; private set; }

    public void Arm() => IsArmed = true;

    public void Disarm() => IsArmed = false;

    public ShutdownWhenFinishedDecision Evaluate(IReadOnlyCollection<OperationStatus> batch, bool restartPending)
    {
        if (!IsArmed || batch.Count == 0)
            return ShutdownWhenFinishedDecision.None;

        if (batch.Any(status => status is OperationStatus.InQueue or OperationStatus.Running))
            return ShutdownWhenFinishedDecision.None;

        IsArmed = false;

        if (batch.Any(status => status is OperationStatus.Failed))
            return ShutdownWhenFinishedDecision.CanceledByFailure;

        if (batch.All(status => status is OperationStatus.Canceled))
            return ShutdownWhenFinishedDecision.None;

        return restartPending
            ? ShutdownWhenFinishedDecision.Restart
            : ShutdownWhenFinishedDecision.ShutDown;
    }
}
