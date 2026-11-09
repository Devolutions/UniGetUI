using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.PackageEngine.Enums;

namespace UniGetUI.Tests;

public class ShutdownWhenFinishedArmTests
{
    private static ShutdownWhenFinishedArm Armed()
    {
        var arm = new ShutdownWhenFinishedArm();
        arm.Arm();
        return arm;
    }

    [Fact]
    public void Unarmed_NeverShutsDown()
    {
        var arm = new ShutdownWhenFinishedArm();

        Assert.Equal(
            ShutdownWhenFinishedDecision.None,
            arm.Evaluate([OperationStatus.Succeeded], restartPending: false));
    }

    [Theory]
    [InlineData(OperationStatus.InQueue)]
    [InlineData(OperationStatus.Running)]
    public void ActiveOperation_KeepsWaitingAndStaysArmed(OperationStatus active)
    {
        var arm = Armed();

        Assert.Equal(
            ShutdownWhenFinishedDecision.None,
            arm.Evaluate([OperationStatus.Succeeded, active], restartPending: false));
        Assert.True(arm.IsArmed);
    }

    [Fact]
    public void EmptyBatch_KeepsWaiting()
    {
        var arm = Armed();

        Assert.Equal(ShutdownWhenFinishedDecision.None, arm.Evaluate([], restartPending: false));
        Assert.True(arm.IsArmed);
    }

    [Fact]
    public void FinishedBatch_ShutsDownOnceAndDisarms()
    {
        var arm = Armed();
        OperationStatus[] batch = [OperationStatus.Succeeded, OperationStatus.Canceled];

        Assert.Equal(ShutdownWhenFinishedDecision.ShutDown, arm.Evaluate(batch, restartPending: false));
        Assert.False(arm.IsArmed);
        Assert.Equal(ShutdownWhenFinishedDecision.None, arm.Evaluate(batch, restartPending: false));
    }

    [Fact]
    public void PendingReboot_RestartsInsteadOfShuttingDown()
    {
        var arm = Armed();

        Assert.Equal(
            ShutdownWhenFinishedDecision.Restart,
            arm.Evaluate([OperationStatus.Succeeded], restartPending: true));
    }

    [Fact]
    public void FailedOperation_CancelsShutdownAndDisarms()
    {
        var arm = Armed();

        Assert.Equal(
            ShutdownWhenFinishedDecision.CanceledByFailure,
            arm.Evaluate([OperationStatus.Succeeded, OperationStatus.Failed], restartPending: true));
        Assert.False(arm.IsArmed);
    }

    [Fact]
    public void EveryOperationCanceled_DisarmsWithoutShuttingDown()
    {
        var arm = Armed();

        Assert.Equal(
            ShutdownWhenFinishedDecision.None,
            arm.Evaluate([OperationStatus.Canceled, OperationStatus.Canceled], restartPending: false));
        Assert.False(arm.IsArmed);
    }

    [Fact]
    public void Disarm_StopsAPendingShutdown()
    {
        var arm = Armed();
        arm.Disarm();

        Assert.Equal(
            ShutdownWhenFinishedDecision.None,
            arm.Evaluate([OperationStatus.Succeeded], restartPending: false));
    }
}
