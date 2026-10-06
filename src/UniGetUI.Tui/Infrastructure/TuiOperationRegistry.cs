using Avalonia.Threading;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Operations.History;
using UniGetUI.PackageOperations;

namespace UniGetUI.Tui.Infrastructure;

/// <summary>
/// Process-global registry of operations for the TUI shell, the counterpart of the desktop
/// <c>AvaloniaOperationRegistry</c>. It owns the list shown on the Operations page, subscribes to an
/// operation before it runs, records every finished operation in <see cref="OperationHistoryStore"/>,
/// raises the in-app notifications (honouring the notification settings), removes successful
/// operations after a short delay unless <see cref="Settings.K.MaintainSuccessfulInstalls"/> is set,
/// and marshals the engine's background-thread events onto the UI thread.
///
/// Threading contract: the backing list is only mutated on the Avalonia UI thread. Public mutators
/// self-marshal. Background-thread operation events are coalesced into one <see cref="Changed"/>.
/// </summary>
internal static class TuiOperationRegistry
{
    private static readonly List<AbstractOperation> _ops = [];
    private static readonly Dictionary<AbstractOperation, OperationStatus?> _batch = [];
    private static int _batchVersion;
    private static int _resetVersion;
    internal static TimeProvider Clock { get; set; } = TimeProvider.System;

    // Cancellation sets Status = Canceled from several code paths, so StatusChanged(Canceled) can fire more than once
    // per run. The operations whose current run already showed its "Operation canceled" notification (UI thread only).
    private static readonly HashSet<AbstractOperation> _cancelNotified = [];
    private static int _changePosted;

    /// <summary>How long a succeeded operation stays listed (Avalonia uses the same 4 s).</summary>
    public static TimeSpan SuccessfulRemovalDelay { get; set; } = TimeSpan.FromSeconds(4);

    /// <summary>Raised on the UI thread whenever the set of operations or any operation's state changes.</summary>
    public static event Action? Changed;

    /// <summary>Raised (UI thread) when the last running/queued operation of a batch finishes.</summary>
    public static event Action? BatchCompleted;

    /// <summary>A UI-thread snapshot of the tracked operations, oldest first.</summary>
    public static IReadOnlyList<AbstractOperation> Snapshot() => _ops.ToArray();

    public static int ErrorsOccurred => _ops.Count(o => o.Status is OperationStatus.Failed);

    public static int ActiveCount => _ops.Count(o => o.Status is OperationStatus.Running or OperationStatus.InQueue);

    /// <summary>
    /// Registers an operation and starts it. Subscribing before <see cref="AbstractOperation.MainThread"/>
    /// runs guarantees no early status change or completion is missed. An operation that another one
    /// depends on (a <c>req:</c> prerequisite) is registered with <see cref="Track"/> instead, so it
    /// shows up without being started twice.
    /// </summary>
    public static void Start(AbstractOperation op)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Start(op));
            return;
        }

        Track(op);
        // Run off the UI thread: operations do synchronous setup inside MainThread() and their awaits
        // must not resume on the UI dispatcher.
        _ = Task.Run(() => RunObservedAsync(op));
    }

    /// <summary>Registers an operation without starting it (prerequisites, retries).</summary>
    public static void Track(AbstractOperation op)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Track(op));
            return;
        }

        if (_ops.Contains(op)) return;
        _ops.Add(op);
        op.StatusChanged += OnOpStatusChanged;
        op.OperationStarting += OnOpStarting;
        op.OperationSucceeded += OnOpSucceeded;
        op.OperationFailed += OnOpFailed;
        op.OperationFinished += OnOpFinished;
        op.LogLineAdded += OnOpLogLine;
        if (op.Status is OperationStatus.InQueue or OperationStatus.Running) TrackBatchRun(op);
        RaiseChanged();
    }

    public static void Remove(AbstractOperation op)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Remove(op));
            return;
        }

        op.StatusChanged -= OnOpStatusChanged;
        op.OperationStarting -= OnOpStarting;
        op.OperationSucceeded -= OnOpSucceeded;
        op.OperationFailed -= OnOpFailed;
        op.OperationFinished -= OnOpFinished;
        op.LogLineAdded -= OnOpLogLine;
        if (op.Status is OperationStatus.Succeeded or OperationStatus.Failed or OperationStatus.Canceled)
            FinishBatchRun(op, op.Status);
        _ops.Remove(op);
        _cancelNotified.Remove(op);
        while (AbstractOperation.OperationQueue.Remove(op)) { }
        RaiseChanged();
    }

    /// <summary>Removes every finished (succeeded/failed/canceled) operation. Running/queued ops are kept.</summary>
    public static void ClearFinished() => RemoveWhere(o => o.Status is OperationStatus.Succeeded
        or OperationStatus.Failed or OperationStatus.Canceled);

    public static void ClearSuccessful() => RemoveWhere(o => o.Status is OperationStatus.Succeeded);

    public static void RetryFailed()
    {
        foreach (var op in Snapshot().Where(o => o.Status is OperationStatus.Failed))
            op.Retry(AbstractOperation.RetryMode.Retry);
    }

    /// <summary>Requests cancellation of every running or queued operation.</summary>
    public static void CancelAll()
    {
        foreach (var op in Snapshot().Where(o => o.Status is OperationStatus.Running or OperationStatus.InQueue))
            op.Cancel();
    }

    /// <summary>Test hook: cancels and forgets everything.</summary>
    public static void Reset()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Reset);
            return;
        }

        foreach (var op in Snapshot())
        {
            if (op.Status is OperationStatus.Running or OperationStatus.InQueue) op.Cancel();
            Remove(op);
        }
        _batch.Clear();
        _batchVersion++;
        _resetVersion++;
    }

    private static void RemoveWhere(Func<AbstractOperation, bool> predicate)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => RemoveWhere(predicate));
            return;
        }

        foreach (var op in _ops.Where(predicate).ToList()) Remove(op);
    }

    private static void OnOpStatusChanged(object? sender, OperationStatus status)
    {
        if (sender is AbstractOperation op)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_ops.Contains(op) && !_batch.ContainsKey(op)) return;
                if (status is OperationStatus.InQueue or OperationStatus.Running)
                    TrackBatchRun(op);
                else if (status is OperationStatus.Succeeded or OperationStatus.Failed or OperationStatus.Canceled)
                    FinishBatchRun(op, status);

                if (status is OperationStatus.Canceled && _cancelNotified.Add(op)
                    && !Settings.AreErrorNotificationsDisabled() && !Settings.Get(Settings.K.DisableNotifications))
                    TuiNotifications.Warning(CoreTools.Translate("Operation canceled"), TitleOf(op));
            });
        }

        RaiseChanged();
    }

    private static void OnOpStarting(object? sender, EventArgs e)
    {
        if (sender is not AbstractOperation op) return;
        // A new run (a retry) may be canceled and notified again.
        Dispatcher.UIThread.Post(() => _cancelNotified.Remove(op));
        if (Settings.AreProgressNotificationsDisabled()) return;
        Dispatcher.UIThread.Post(() => TuiNotifications.Info(TitleOf(op),
            op.Metadata.Status.Length > 0 ? op.Metadata.Status : CoreTools.Translate("Please wait...")));
    }

    private static void OnOpSucceeded(object? sender, EventArgs e)
    {
        if (sender is not AbstractOperation op) return;
        if (!Settings.AreSuccessNotificationsDisabled())
        {
            Dispatcher.UIThread.Post(() => TuiNotifications.Success(
                op.Metadata.SuccessTitle.Length > 0 ? op.Metadata.SuccessTitle : CoreTools.Translate("Success!"),
                op.Metadata.SuccessMessage.Length > 0 ? op.Metadata.SuccessMessage : TitleOf(op)));
        }

        if (!Settings.Get(Settings.K.MaintainSuccessfulInstalls))
            _ = RemoveAfterDelayAsync(op, SuccessfulRemovalDelay);
    }

    private static void OnOpFailed(object? sender, EventArgs e)
    {
        if (sender is not AbstractOperation op) return;
        if (Settings.AreErrorNotificationsDisabled()) return;
        Dispatcher.UIThread.Post(() => TuiNotifications.Error(
            op.Metadata.FailureTitle.Length > 0 ? op.Metadata.FailureTitle : CoreTools.Translate("Failed"),
            op.Metadata.FailureMessage.Length > 0
                ? op.Metadata.FailureMessage
                : CoreTools.Translate("An error occurred while processing this package")));
    }

    private static void OnOpFinished(object? sender, EventArgs e)
    {
        if (sender is not AbstractOperation op) return;

        // The terminal line is appended after the finished events fire; record once the run task is done.
        OperationStatus completedStatus = op.Status;
        _ = RecordHistoryAfterCompletionAsync(op, op.MainThread(), completedStatus);
        RaiseChanged();
    }

    private static void OnOpLogLine(object? sender, (string, AbstractOperation.LineType) line) => RaiseChanged();

    internal static Task RecordHistoryAfterCompletionAsync(
        AbstractOperation op, Task completion, OperationStatus completedStatus)
        => completion.ContinueWith(_ => RecordHistory(op, completedStatus), TaskScheduler.Default);

    private static void RecordHistory(AbstractOperation op, OperationStatus completedStatus)
    {
        try
        {
            string status = completedStatus switch
            {
                OperationStatus.Succeeded => OperationHistoryRecord.StatusSucceeded,
                OperationStatus.Failed => OperationHistoryRecord.StatusFailed,
                OperationStatus.Canceled => OperationHistoryRecord.StatusCanceled,
                _ => completedStatus.ToString().ToLowerInvariant(),
            };
            OperationHistoryStore.Add(OperationHistoryRecord.FromOperation(op, status));
        }
        catch (Exception ex)
        {
            Logger.Warn("Failed to write operation history");
            Logger.Warn(ex);
        }
    }

    private static void TrackBatchRun(AbstractOperation op)
    {
        if (_batch.TryGetValue(op, out OperationStatus? status) && status is null) return;
        _batch[op] = null;
        _batchVersion++;
    }

    private static void FinishBatchRun(AbstractOperation op, OperationStatus status)
    {
        if (!_batch.TryGetValue(op, out OperationStatus? previous) || previous == status) return;
        _batch[op] = status;
        _ = PostBatchChecksAsync(++_batchVersion);
    }

    private sealed record BatchResult(int Succeeded, int Failed, long UacGeneration, int ResetVersion);

    private static async Task PostBatchChecksAsync(int batchVersion)
    {
        await Task.Delay(TimeSpan.FromMilliseconds(500), Clock);
        BatchResult? result = await Dispatcher.UIThread.InvokeAsync<BatchResult?>(() =>
        {
            if (batchVersion != _batchVersion || _batch.Count == 0 || ActiveCount > 0
                || _batch.Values.Any(status => status is null)) return null;
            var completed = new BatchResult(
                _batch.Values.Count(status => status is OperationStatus.Succeeded),
                _batch.Values.Count(status => status is OperationStatus.Failed),
                CoreTools.UACCacheGeneration, _resetVersion);
            _batch.Clear();
            _batchVersion++;
            return completed;
        });
        if (result is null) return;

        if (Settings.Get(Settings.K.DoCacheAdminRightsForBatches))
        {
            Logger.Info("Clearing UAC prompt since there are no remaining operations");
            await CoreTools.ResetUACForCurrentProcess(result.UacGeneration);
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (result.ResetVersion != _resetVersion) return;
            if (Settings.Get(Settings.K.ShowOperationSummaryNotifications))
            {
                TuiNotifications.Show(result.Failed > 0 ? TuiNotificationSeverity.Warning : TuiNotificationSeverity.Success,
                    CoreTools.Translate("Operations finished"),
                    CoreTools.Translate("{0} succeeded, {1} failed", result.Succeeded, result.Failed));
            }

            BatchCompleted?.Invoke();
        });
    }

    private static async Task RemoveAfterDelayAsync(AbstractOperation op, TimeSpan delay)
    {
        await Task.Delay(delay, Clock);
        Dispatcher.UIThread.Post(() =>
        {
            if (op.Status is OperationStatus.Succeeded && _ops.Contains(op)) Remove(op);
        });
    }

    private static string TitleOf(AbstractOperation op)
        => string.IsNullOrWhiteSpace(op.Metadata.Title) ? CoreTools.Translate("Operation") : op.Metadata.Title;

    private static async Task RunObservedAsync(AbstractOperation op)
    {
        try
        {
            await op.MainThread();
        }
        catch (Exception ex)
        {
            Logger.Error("A TUI operation crashed unexpectedly:");
            Logger.Error(ex);
        }
    }

    // Coalesce bursts of background events into a single UI-thread Changed invocation while
    // guaranteeing a trailing one (the gate is cleared inside the post).
    private static void RaiseChanged()
    {
        if (Interlocked.CompareExchange(ref _changePosted, 1, 0) != 0) return;
        Dispatcher.UIThread.Post(() =>
        {
            Interlocked.Exchange(ref _changePosted, 0);
            Changed?.Invoke();
        }, DispatcherPriority.Background);
    }
}
