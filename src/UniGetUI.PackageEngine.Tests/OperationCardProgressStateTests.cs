using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageOperations;

namespace UniGetUI.PackageEngine.Tests;

/// <summary>
/// Small representative coverage for the pure card mapping. The full display
/// behavior (log restoration, determinate gating, dispatcher ordering) lives in
/// <see cref="OperationCardController"/> and is covered by
/// <c>OperationCardControllerTests</c>.
/// </summary>
public sealed class OperationCardProgressStateTests
{
    private static OperationCardProgressState FreshCard(string liveLine = "Please wait...") =>
        new(IsIndeterminate: false, Value: 0, LiveLine: liveLine);

    private static OperationCardProgressState RunningCard(string liveLine = "Please wait...") =>
        FreshCard(liveLine).WithStatus(OperationStatus.Running);

    [Fact]
    public void Running_Unknown_StaysIndeterminate()
    {
        var card = RunningCard().WithProgress(OperationStatus.Running, null);

        Assert.True(card.IsIndeterminate);
        Assert.Equal("Please wait...", card.LiveLine);
    }

    [Fact]
    public void Running_DeterminateDownload_IsDeterminate()
    {
        var card = RunningCard().WithProgress(
            OperationStatus.Running,
            OperationProgress.FromDownload(50, 100)
        );

        Assert.False(card.IsIndeterminate);
        Assert.Equal(50, card.Value);
        Assert.Contains("50%", card.LiveLine);
    }

    [Fact]
    public void Running_StageOnly_IsIndeterminate()
    {
        var card = RunningCard().WithProgress(
            OperationStatus.Running,
            OperationProgress.ForStage(OperationProgressStage.Installing)
        );

        Assert.True(card.IsIndeterminate);
        Assert.Contains("Installing", card.LiveLine);
    }

    [Theory]
    [InlineData(OperationStatus.Succeeded)]
    [InlineData(OperationStatus.Failed)]
    [InlineData(OperationStatus.Canceled)]
    public void TerminalStatus_OwnsFullBar(OperationStatus status)
    {
        var card = RunningCard().WithStatus(status);

        Assert.False(card.IsIndeterminate);
        Assert.Equal(100, card.Value);
    }
}
