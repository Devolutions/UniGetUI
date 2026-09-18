using System.Diagnostics;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Enums;

namespace UniGetUI.PackageOperations;

// Progress reporting for AbstractOperation: structured, manager-neutral OperationProgress
// snapshots flow side-car via ProgressChanged and never touch the log/history path.
//
// DESIGN NOTES (maintainer feedback on #5390):
// - Structured progress callbacks and Line() logging are fully separated. ReportProgress
//   never logs; existing CLI output, return codes, retry decisions, and history entries
//   are unaffected by progress reporting.
// - ONE monotonic clock source: _timestampProvider (default Stopwatch.GetTimestamp,
//   injectable for tests) is the only time source used by progress logic. Elapsed
//   intervals come from Stopwatch.GetElapsedTime so wall-clock adjustments never skew
//   throughput. There is no time-based UI throttling, so there is no second clock.
// - Speed is measured generically as deltaBytes / deltaTime from real cumulative byte
//   samples. First sample, rewound counters, zero elapsed time, stage changes, and
//   unknown progress never synthesize a speed. Light deterministic EMA smoothing
//   (alpha 0.3) damps per-chunk network jitter.
// - Stale speed expires: when no fresh byte movement is observed for StaleSpeedTimeout
//   (default 2 s), the speed suffix is dropped rather than displayed indefinitely.
//   A single one-shot timer exists only while a fresh speed is active; it is stopped
//   on stage change/reset/completion/disposal and never affects execution.
// - No ETA is computed.
public abstract partial class AbstractOperation
{
    /// <summary>
    /// Raised on the reporting thread whenever structured progress is reported or expired.
    /// UI subscribers must marshal to the UI thread (as with LogLineAdded/StatusChanged).
    /// Never raised from the log path; progress lines in the log do not raise this.
    /// </summary>
    public event EventHandler<OperationProgress>? ProgressChanged;

    /// <summary>Latest structured progress snapshot. Unknown until first reported.</summary>
    public OperationProgress CurrentProgress { get; private set; } = OperationProgress.Unknown;

    private readonly object ProgressLock = new();
    private Func<long> TimestampProvider = static () => Stopwatch.GetTimestamp();

    // Throughput tracker state. All fields are guarded by ProgressLock.
    private bool HasThroughputBaseline;
    private ulong LastThroughputBytes;
    private long LastThroughputTimestamp;
    private long LastFreshTimestamp;
    private double? SmoothedBytesPerSecond;

    private const double ThroughputSmoothingAlpha = 0.3;

    /// <summary>
    /// Age after which a previously measured speed is considered stale and omitted
    /// from display. Fresh byte movement restores it.
    /// </summary>
    internal TimeSpan StaleSpeedTimeout { get; set; } = TimeSpan.FromSeconds(2);

    private Timer? StaleSpeedTimer;
    private bool StaleTimerArmed;

    /// <summary>
    /// Test hook: replaces the single monotonic clock used by progress logic. Resets
    /// tracker state so samples from different clocks are never mixed.
    /// </summary>
    internal void SetTimestampProviderForTests(Func<long> provider)
    {
        lock (ProgressLock)
        {
            TimestampProvider = provider;
            ResetThroughputStateUnlocked();
            DisarmStaleTimerUnlocked();
        }
    }

    /// <summary>
    /// Reports structured progress. Enriches download reports with measured throughput,
    /// stores the snapshot as <see cref="CurrentProgress"/>, and raises
    /// <see cref="ProgressChanged"/>. Never logs and never fails the operation:
    /// each subscriber is isolated so a display-layer exception cannot fail package
    /// execution nor block later subscribers.
    /// Safe to call concurrently from output callbacks.
    /// Callbacks run outside <c>ProgressLock</c> (holding a lock across subscriber
    /// code invites deadlock), so callback ordering is only guaranteed for a single
    /// reporting thread.
    /// </summary>
    protected void ReportProgress(OperationProgress progress)
    {
        OperationProgress enriched;
        lock (ProgressLock)
        {
            enriched = EnrichWithThroughputUnlocked(progress);
            CurrentProgress = enriched;
        }
        NotifyProgressSubscribers(enriched);
    }

    private void NotifyProgressSubscribers(OperationProgress enriched)
    {
        var handlers = ProgressChanged?.GetInvocationList();
        if (handlers is null)
        {
            return;
        }

        foreach (EventHandler<OperationProgress> handler in handlers)
        {
            try
            {
                handler(this, enriched);
            }
            catch (Exception ex)
            {
                Logger.Warn(
                    $"A progress subscriber threw; progress reporting is observational and will not fail the operation: {ex}"
                );
            }
        }
    }

    private OperationProgress EnrichWithThroughputUnlocked(OperationProgress progress)
    {
        long now = TimestampProvider();

        // Only the Downloading stage with full byte counters can carry a measured speed.
        // Anything else (stage change away from Downloading, unknown progress) clears
        // the tracker and strips any attached speed.
        if (
            progress.Stage is not OperationProgressStage.Downloading
            || progress.BytesDownloaded is null
            || progress.BytesTotal is null
            || progress.BytesTotal == 0
        )
        {
            ResetThroughputStateUnlocked();
            DisarmStaleTimerUnlocked();
            return progress.BytesPerSecond is null
                ? progress
                : progress with
                {
                    BytesPerSecond = null,
                };
        }

        ulong bytes = progress.BytesDownloaded.Value;

        if (!HasThroughputBaseline)
        {
            // First sample establishes the baseline; there is no speed yet.
            HasThroughputBaseline = true;
            LastThroughputBytes = bytes;
            LastThroughputTimestamp = now;
            LastFreshTimestamp = now;
            SmoothedBytesPerSecond = null;
            DisarmStaleTimerUnlocked();
            return progress with { BytesPerSecond = null };
        }

        if (bytes < LastThroughputBytes)
        {
            // Counter rewound (retry/restart): the old baseline is meaningless.
            // The rewound sample becomes the new baseline; no stale speed survives.
            LastThroughputBytes = bytes;
            LastThroughputTimestamp = now;
            LastFreshTimestamp = now;
            SmoothedBytesPerSecond = null;
            DisarmStaleTimerUnlocked();
            return progress with { BytesPerSecond = null };
        }

        if (bytes == LastThroughputBytes)
        {
            // Stalled counter carries no new information: move the delta baseline
            // forward so the stalled interval does not dilute the next real delta,
            // but do not refresh freshness. Past the stale timeout the previous
            // speed is dropped rather than displayed indefinitely.
            LastThroughputTimestamp = now;
            if (SmoothedBytesPerSecond is null)
            {
                return progress with { BytesPerSecond = null };
            }

            if (IsStaleUnlocked(now))
            {
                DisarmStaleTimerUnlocked();
                return progress with { BytesPerSecond = null };
            }

            return progress with { BytesPerSecond = SmoothedBytesPerSecond };
        }

        TimeSpan elapsed = Stopwatch.GetElapsedTime(LastThroughputTimestamp, now);
        if (elapsed <= TimeSpan.Zero)
        {
            // No time elapsed: preserve the previous speed without dividing by zero.
            return progress with { BytesPerSecond = SmoothedBytesPerSecond };
        }

        double instant = (bytes - LastThroughputBytes) / elapsed.TotalSeconds;
        double smoothed =
            SmoothedBytesPerSecond is { } previous
                ? ThroughputSmoothingAlpha * instant + (1 - ThroughputSmoothingAlpha) * previous
                : instant;

        if (OperationProgress.NormalizeBytesPerSecond(smoothed) is null)
        {
            // Defensive: with a positive delta over positive time this cannot happen,
            // but never let a non-usable speed leak downstream.
            return progress with { BytesPerSecond = SmoothedBytesPerSecond };
        }

        SmoothedBytesPerSecond = smoothed;
        LastThroughputBytes = bytes;
        LastThroughputTimestamp = now;
        LastFreshTimestamp = now;
        ArmStaleTimerUnlocked();
        return progress with { BytesPerSecond = smoothed };
    }

    private bool IsStaleUnlocked(long now) =>
        Stopwatch.GetElapsedTime(LastFreshTimestamp, now) > StaleSpeedTimeout;

    private void ArmStaleTimerUnlocked()
    {
        try
        {
            if (StaleSpeedTimer is null)
            {
                StaleSpeedTimer = new Timer(
                    OnStaleSpeedTimer,
                    null,
                    StaleSpeedTimeout,
                    Timeout.InfiniteTimeSpan
                );
            }
            else
            {
                StaleSpeedTimer.Change(StaleSpeedTimeout, Timeout.InfiniteTimeSpan);
            }

            StaleTimerArmed = true;
        }
        catch (Exception ex)
        {
            // Timer creation must never affect package execution.
            Logger.Warn($"Could not arm the stale-speed timer; speed expiry is disabled for this report: {ex}");
            StaleTimerArmed = false;
        }
    }

    private void DisarmStaleTimerUnlocked()
    {
        StaleTimerArmed = false;
        try
        {
            StaleSpeedTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not disarm the stale-speed timer: {ex}");
        }
    }

    private void OnStaleSpeedTimer(object? state)
    {
        OperationProgress? expired = null;
        try
        {
            long now = TimestampProvider();
            lock (ProgressLock)
            {
                if (StaleSpeedTimer is null || !StaleTimerArmed)
                {
                    return;
                }

                // Completion stops the freshness mechanism: terminal operations keep
                // their final visuals and must not receive post-completion updates.
                if (Status is not OperationStatus.Running)
                {
                    DisarmStaleTimerUnlocked();
                    return;
                }

                if (!HasThroughputBaseline || SmoothedBytesPerSecond is null)
                {
                    DisarmStaleTimerUnlocked();
                    return;
                }

                if (!IsStaleUnlocked(now))
                {
                    // Fired early (wall-clock vs monotonic skew or re-arm race):
                    // re-arm for the remaining freshness window, no dispatcher spam.
                    try
                    {
                        TimeSpan elapsed = Stopwatch.GetElapsedTime(LastFreshTimestamp, now);
                        TimeSpan remaining = StaleSpeedTimeout - elapsed;
                        if (remaining < TimeSpan.Zero)
                        {
                            remaining = TimeSpan.Zero;
                        }

                        StaleSpeedTimer.Change(remaining, Timeout.InfiniteTimeSpan);
                    }
                    catch (ObjectDisposedException)
                    {
                        StaleTimerArmed = false;
                    }

                    return;
                }

                if (CurrentProgress.BytesPerSecond is null)
                {
                    DisarmStaleTimerUnlocked();
                    return;
                }

                expired = CurrentProgress with { BytesPerSecond = null };
                CurrentProgress = expired;
                DisarmStaleTimerUnlocked();
            }
        }
        catch (Exception ex)
        {
            Logger.Warn($"Stale-speed expiry failed; keeping the last measured speed: {ex}");
            return;
        }

        if (expired is not null)
        {
            NotifyProgressSubscribers(expired);
        }
    }

    /// <summary>
    /// Deterministic test hook for stale-speed expiry: runs the same staleness check
    /// the timer performs, using the injected monotonic clock. Returns true when a
    /// speed-less update was pushed.
    /// </summary>
    internal bool ExpireStaleSpeedForTests()
    {
        OperationProgress? expired = null;
        long now = TimestampProvider();
        lock (ProgressLock)
        {
            if (!HasThroughputBaseline || SmoothedBytesPerSecond is null)
            {
                return false;
            }

            if (!IsStaleUnlocked(now))
            {
                return false;
            }

            if (CurrentProgress.BytesPerSecond is null)
            {
                DisarmStaleTimerUnlocked();
                return false;
            }

            expired = CurrentProgress with { BytesPerSecond = null };
            CurrentProgress = expired;
            DisarmStaleTimerUnlocked();
        }

        NotifyProgressSubscribers(expired);
        return true;
    }

    internal bool IsStaleSpeedTimerArmedForTests()
    {
        lock (ProgressLock)
        {
            return StaleTimerArmed;
        }
    }

    private void ResetThroughputStateUnlocked()
    {
        HasThroughputBaseline = false;
        LastThroughputBytes = 0;
        LastThroughputTimestamp = 0;
        LastFreshTimestamp = 0;
        SmoothedBytesPerSecond = null;
    }

    internal void DisposeStaleSpeedTimer()
    {
        Timer? timer;
        lock (ProgressLock)
        {
            timer = StaleSpeedTimer;
            StaleSpeedTimer = null;
            StaleTimerArmed = false;
        }

        try
        {
            timer?.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not dispose the stale-speed timer: {ex}");
        }
    }
}
