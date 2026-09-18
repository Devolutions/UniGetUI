using UniGetUI.PackageEngine.Enums;

namespace UniGetUI.PackageOperations;

/// <summary>
/// Pure, UI-framework-agnostic mapping from operation status plus generic
/// <see cref="OperationProgress"/> to operation-card progress visuals.
/// This is one input to the card visuals, not the full source of truth:
/// <c>OperationViewModel</c> additionally owns log-line restoration
/// (<c>_lastLogLine</c>), determinate ownership gating
/// (<c>_determinateProgressActive</c>), and dispatcher ordering, which are covered
/// by ViewModel state-machine tests. This type never touches the dispatcher or any
/// UI control; the ViewModel remains the only UI-thread owner and copies
/// <see cref="IsIndeterminate"/>, <see cref="Value"/>, and <see cref="LiveLine"/> onto
/// its bindable properties.
/// </summary>
public sealed record OperationCardProgressState(
    bool IsIndeterminate,
    double Value,
    string LiveLine
)
{
    /// <summary>
    /// Mirrors <c>OperationViewModel.ApplyStatus</c> for the progress visuals only
    /// (brushes and button text stay in the ViewModel). Terminal statuses own the final
    /// visuals with a full bar; queue resets to zero; running keeps the current value
    /// and shows the indeterminate animation until real progress arrives.
    /// </summary>
    public OperationCardProgressState WithStatus(OperationStatus status) =>
        status switch
        {
            OperationStatus.InQueue => this with { IsIndeterminate = false, Value = 0 },
            OperationStatus.Running => this with { IsIndeterminate = true },
            OperationStatus.Succeeded
            or OperationStatus.Failed
            or OperationStatus.Canceled => this with { IsIndeterminate = false, Value = 100 },
            _ => this,
        };

    /// <summary>
    /// Applies a structured progress report to a running card. Progress is only honored
    /// while <paramref name="status"/> is <see cref="OperationStatus.Running"/>; reports
    /// arriving after completion are ignored so terminal visuals win and stale reports
    /// (including stale speeds) never leak back. Unknown progress keeps the
    /// indeterminate animation; a known stage still updates the status text, while a
    /// plain <c>Unknown</c> reset preserves the existing log-driven line.
    /// </summary>
    public OperationCardProgressState WithProgress(
        OperationStatus status,
        OperationProgress? progress
    )
    {
        if (status is not OperationStatus.Running)
            return this;

        if (progress is null || !progress.IsDeterminate)
        {
            string liveLine =
                progress is not null && progress.Stage is not OperationProgressStage.Unknown
                    ? OperationProgressFormatter.Format(progress)
                    : LiveLine;
            return this with { IsIndeterminate = true, LiveLine = liveLine };
        }

        return this with
        {
            IsIndeterminate = false,
            Value = Math.Clamp(progress.Percentage!.Value, 0, 100),
            LiveLine = OperationProgressFormatter.Format(progress),
        };
    }
}
