using UniGetUI.PackageEngine.Enums;

namespace UniGetUI.PackageOperations;

/// <summary>
/// UI-thread-owned state machine behind <c>OperationViewModel</c> progress visuals.
/// Owns the three pieces of display state that decide what the card shows:
/// the pure mapping (<see cref="OperationCardProgressState"/>), the last log-driven
/// line for retry/reset restoration, and determinate ownership gating for raw
/// per-frame progress text. All methods run on the UI thread in production
/// (via <c>Dispatcher.UIThread.Post</c>, which is FIFO at the same priority, so the
/// failure path clears ownership before the failure line arrives) and synchronously
/// in unit tests.
/// </summary>
public sealed class OperationCardController
{
    private OperationCardProgressState _card = new(
        IsIndeterminate: false,
        Value: 0,
        LiveLine: ""
    );

    private string _lastLogLine = "";
    private bool _determinateProgressActive;

    public OperationCardProgressState Card => _card;
    public string LastLogLine => _lastLogLine;
    public bool DeterminateProgressActive => _determinateProgressActive;

    /// <summary>
    /// Initializes all three display states from the same current operation snapshot.
    /// Must derive the determinate gate from <paramref name="currentProgress"/> so a
    /// card constructed mid-download does not let raw progress lines clobber the
    /// formatted line until the next report arrives.
    /// </summary>
    public void SyncInitial(
        string initialLiveLine,
        OperationStatus status,
        OperationProgress? currentProgress
    )
    {
        _card = _card with { LiveLine = initialLiveLine };
        _lastLogLine = initialLiveLine;
        ApplyStatus(status);
        _card = _card.WithProgress(status, currentProgress);
        _determinateProgressActive =
            status is OperationStatus.Running && currentProgress?.IsDeterminate is true;
    }

    /// <summary>
    /// Applies a log line to the card. Returns false when a raw per-frame progress
    /// line is gated out while determinate progress owns the status line; otherwise
    /// updates the card and last log line and returns the display line.
    /// </summary>
    public bool TryApplyLogLine(
        string text,
        AbstractOperation.LineType type,
        out string liveLine
    )
    {
        if (
            type is AbstractOperation.LineType.ProgressIndicator
            && _determinateProgressActive
        )
        {
            liveLine = _card.LiveLine;
            return false;
        }

        _card = _card with { LiveLine = text };
        _lastLogLine = text;
        liveLine = text;
        return true;
    }

    /// <summary>
    /// Applies a structured progress report. Returns the display values the ViewModel
    /// copies onto its bindable properties. A plain <c>Unknown</c> reset restores the
    /// last log line so stale speed-bearing text never survives retry/restart.
    /// </summary>
    public (bool IsIndeterminate, double Value, string LiveLine) ApplyProgress(
        OperationStatus status,
        OperationProgress? progress
    )
    {
        _card = _card.WithProgress(status, progress);
        _determinateProgressActive =
            status is OperationStatus.Running && progress?.IsDeterminate is true;

        string liveLine =
            progress is null || progress.Stage is OperationProgressStage.Unknown
                ? _lastLogLine
                : _card.LiveLine;

        // Keep the card's LiveLine in sync with what is actually displayed when the
        // reset path restores the log line; the pure mapping intentionally keeps its
        // held line, but the card must not show stale determinate text.
        if (progress is null || progress.Stage is OperationProgressStage.Unknown)
        {
            _card = _card with { LiveLine = liveLine };
        }

        return (_card.IsIndeterminate, _card.Value, liveLine);
    }

    /// <summary>
    /// Applies a status transition to the progress visuals. Determinate ownership ends
    /// with the running phase; afterwards log lines own the status line again.
    /// </summary>
    public void ApplyStatus(OperationStatus status)
    {
        _card = _card.WithStatus(status);
        _determinateProgressActive =
            status is OperationStatus.Running && _determinateProgressActive;
    }
}
