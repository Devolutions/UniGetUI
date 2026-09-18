using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageOperations;

namespace UniGetUI.PackageEngine.Tests;

/// <summary>
/// Direct coverage for the actual card state machine behind
/// <c>OperationViewModel</c> (<c>_determinateProgressActive</c> /
/// <c>_lastLogLine</c> ordering). The controller is the exact logic the ViewModel
/// delegates to on the UI thread, so these pin the display behavior without an
/// Avalonia harness.
/// </summary>
public sealed class OperationCardControllerTests
{
    private const ulong OneMiB = 1024UL * 1024;

    private static OperationProgress DeterminateDownload(
        ulong downloaded = 42,
        ulong total = 100,
        double? speed = 1024
    ) =>
        speed is null
            ? OperationProgress.FromDownload(downloaded, total)
            : OperationProgress.FromDownload(downloaded, total) with
            {
                BytesPerSecond = speed,
            };

    [Fact]
    public void ConstructDuringDeterminate_ShowsFormatted_AndGatesRawLines()
    {
        var controller = new OperationCardController();
        var current = DeterminateDownload(21 * OneMiB, 100 * OneMiB, 1.2 * OneMiB);

        controller.SyncInitial("Starting operation...", OperationStatus.Running, current);
        var (isIndeterminate, value, liveLine) = controller.ApplyProgress(
            OperationStatus.Running,
            current
        );

        Assert.False(isIndeterminate);
        Assert.True(value > 0);
        Assert.Contains("21%", liveLine);
        Assert.True(controller.DeterminateProgressActive);

        // A raw per-frame progress line must not clobber the formatted line.
        Assert.False(
            controller.TryApplyLogLine(
                "[###] 21%",
                AbstractOperation.LineType.ProgressIndicator,
                out string afterRaw
            )
        );
        Assert.Contains("21%", afterRaw);
        Assert.Contains("/s", afterRaw);
    }

    [Fact]
    public void FailureOrdering_ClearsGate_BeforeFailureLineArrives()
    {
        var controller = new OperationCardController();
        controller.SyncInitial("Starting operation...", OperationStatus.Running, null);
        controller.ApplyProgress(
            OperationStatus.Running,
            DeterminateDownload(40, 100, 1024)
        );
        Assert.True(controller.DeterminateProgressActive);

        // Dispatcher FIFO: Status=Failed is handled before the failure
        // ProgressIndicator line, so the gate is cleared first.
        controller.ApplyStatus(OperationStatus.Failed);
        Assert.False(controller.DeterminateProgressActive);

        Assert.True(
            controller.TryApplyLogLine(
                "Failure message - Click here for more details",
                AbstractOperation.LineType.ProgressIndicator,
                out string liveLine
            )
        );
        Assert.Contains("Failure message", liveLine);
    }

    [Fact]
    public void RetryReset_RestoresLogLine_WithoutStaleSpeed()
    {
        var controller = new OperationCardController();
        controller.SyncInitial("Starting operation...", OperationStatus.Running, null);
        Assert.True(
            controller.TryApplyLogLine(
                "Starting operation...",
                AbstractOperation.LineType.Information,
                out _
            )
        );
        controller.ApplyProgress(
            OperationStatus.Running,
            DeterminateDownload(40, 100, 1024)
        );
        Assert.Contains("/s", controller.Card.LiveLine);

        // Plain Unknown reset (retry/restart) restores the last log line.
        var (_, _, liveLine) = controller.ApplyProgress(
            OperationStatus.Running,
            OperationProgress.Unknown
        );

        Assert.Equal("Starting operation...", liveLine);
        Assert.DoesNotContain("/s", liveLine);
        Assert.False(controller.DeterminateProgressActive);
    }

    [Fact]
    public void DownloadToStage_RemovesSpeed()
    {
        var controller = new OperationCardController();
        controller.SyncInitial("Starting operation...", OperationStatus.Running, null);
        var (_, _, determinateLine) = controller.ApplyProgress(
            OperationStatus.Running,
            DeterminateDownload(21 * OneMiB, 100 * OneMiB, 1.2 * OneMiB)
        );
        Assert.Contains("/s", determinateLine);

        var (isIndeterminate, _, stageLine) = controller.ApplyProgress(
            OperationStatus.Running,
            OperationProgress.ForStage(OperationProgressStage.Installing)
        );

        Assert.True(isIndeterminate);
        Assert.Contains("Installing", stageLine);
        Assert.DoesNotContain("/s", stageLine);
        Assert.False(controller.DeterminateProgressActive);
    }

    [Fact]
    public void NonDeterminateLogLines_Flow_WhenNoDeterminateActive()
    {
        var controller = new OperationCardController();
        controller.SyncInitial("Queued...", OperationStatus.InQueue, null);

        Assert.True(
            controller.TryApplyLogLine(
                "Operation on queue (position 1)...",
                AbstractOperation.LineType.ProgressIndicator,
                out string liveLine
            )
        );
        Assert.Contains("position 1", liveLine);
    }

    [Fact]
    public void TerminalStatus_ClearsGate()
    {
        var controller = new OperationCardController();
        controller.SyncInitial("Starting operation...", OperationStatus.Running, null);
        controller.ApplyProgress(
            OperationStatus.Running,
            DeterminateDownload(40, 100, 1024)
        );

        controller.ApplyStatus(OperationStatus.Succeeded);

        Assert.False(controller.DeterminateProgressActive);
        Assert.False(controller.Card.IsIndeterminate);
        Assert.Equal(100, controller.Card.Value);
    }
}
