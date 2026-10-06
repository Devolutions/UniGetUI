using System.Threading.Channels;
using Avalonia.Threading;
using NUnit.Framework;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Operations.History;
using UniGetUI.PackageOperations;
using UniGetUI.Tui.Infrastructure;
using NAssert = NUnit.Framework.Assert;

namespace UniGetUI.Tui.Tests;

[TestFixture, NonParallelizable]
internal sealed class TuiOperationRegistryTests : TuiE2ETestBase
{
    private readonly List<ControlledOperation> _operations = [];
    private readonly List<Task> _ownedTasks = [];
    private readonly List<TaskCompletionSource> _recordingGates = [];
    private readonly List<TuiNotification> _summaries = [];
    private Dictionary<Settings.K, bool> _settings = [];
    private RegressionTimeProvider _clock = null!;
    private string? _historyPath;
    private TimeSpan _removalDelay;
    private int _batches;

    private static Task Fence() => Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask();
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [SetUp]
    public async Task IsolateRegistry()
    {
        await OnUi(TuiOperationRegistry.Reset);
        await Fence();
        _operations.Clear();
        _ownedTasks.Clear();
        _recordingGates.Clear();
        _summaries.Clear();
        _batches = 0;
        _settings = new[]
        {
            Settings.K.MaintainSuccessfulInstalls, Settings.K.ShowOperationSummaryNotifications,
            Settings.K.DoCacheAdminRightsForBatches, Settings.K.DisableNotifications,
            Settings.K.DisableSystemTray, Settings.K.DisableSuccessNotifications,
            Settings.K.DisableErrorNotifications, Settings.K.DisableProgressNotifications,
        }.ToDictionary(key => key, key => Settings.Get(key));
        Settings.Set(Settings.K.MaintainSuccessfulInstalls, true);
        Settings.Set(Settings.K.ShowOperationSummaryNotifications, true);
        Settings.Set(Settings.K.DoCacheAdminRightsForBatches, false);
        Settings.Set(Settings.K.DisableNotifications, false);
        Settings.Set(Settings.K.DisableSystemTray, false);
        Settings.Set(Settings.K.DisableSuccessNotifications, false);
        Settings.Set(Settings.K.DisableErrorNotifications, false);
        Settings.Set(Settings.K.DisableProgressNotifications, true);
        _removalDelay = TuiOperationRegistry.SuccessfulRemovalDelay;
        _clock = new();
        await OnUi(() => TuiOperationRegistry.Clock = _clock);
        _historyPath = OperationHistoryStore.TestFilePathOverride;
        OperationHistoryStore.TestFilePathOverride = Path.Join(FakeDataSetUp.Sandbox, Guid.NewGuid() + ".history.json");
        OperationHistoryStore.InvalidateCache();
        TuiNotifications.NotificationRaised += Observe;
        TuiOperationRegistry.BatchCompleted += Batch;
    }

    private void Observe(TuiNotification notification)
    {
        if (notification.Title == "Operations finished") _summaries.Add(notification);
    }

    private void Batch() => _batches++;

    [TearDown]
    public async Task RestoreRegistry()
    {
        foreach (var op in _operations) op.ReleaseAll();
        foreach (var gate in _recordingGates) gate.TrySetResult();
        await Task.WhenAll(_ownedTasks);
        await OnUi(TuiOperationRegistry.Reset);
        await DrainTimers();
        await OnUi(() =>
        {
            TuiNotifications.NotificationRaised -= Observe;
            TuiOperationRegistry.BatchCompleted -= Batch;
            TuiOperationRegistry.Clock = TimeProvider.System;
            TuiOperationRegistry.SuccessfulRemovalDelay = _removalDelay;
        });
        foreach (var op in _operations) op.Dispose();
        OperationHistoryStore.TestFilePathOverride = _historyPath;
        OperationHistoryStore.InvalidateCache();
        foreach (var (key, value) in _settings) Settings.Set(key, value);
    }

    private ControlledOperation NewOperation(string name = "current", bool holdCompletion = false)
    {
        var op = new ControlledOperation(name, holdCompletion);
        _operations.Add(op);
        return op;
    }

    private static async Task Track(ControlledOperation op) => await OnUi(() => TuiOperationRegistry.Track(op));

    private static async Task SetStatus(ControlledOperation op, OperationStatus status)
    {
        await OnUi(() => op.Status = status);
        await Fence();
    }

    private async Task Quiet()
    {
        var timer = await _clock.NextTimerAsync();
        NAssert.That(timer.DueTime, Is.EqualTo(TimeSpan.FromMilliseconds(500)));
        await OnUi(() => timer.Fire());
        await Fence();
    }

    private async Task DrainTimers()
    {
        // FinishBatchRun is UI-owned; firing on that same dispatcher queues its captured continuations
        // ahead of the background-priority fence, including losing revision checks.
        await OnUi(() =>
        {
            foreach (var timer in _clock.Timers) timer.Fire(advance: false);
        });
        await Fence();
    }

    private void AssertSummary(int succeeded, int failed, int count = 1)
    {
        NAssert.That(_batches, Is.EqualTo(count), "payload-free BatchCompleted publications");
        NAssert.That(_summaries, Has.Count.EqualTo(count));
        NAssert.That(_summaries[^1].Message, Is.EqualTo($"{succeeded} succeeded, {failed} failed"));
        NAssert.That(_summaries[^1].Severity, Is.EqualTo(failed > 0
            ? TuiNotificationSeverity.Warning : TuiNotificationSeverity.Success));
    }

    [TestCase(OperationStatus.Failed, "failed")]
    [TestCase(OperationStatus.Canceled, "canceled")]
    [TestCase(OperationStatus.Succeeded, "succeeded")]
    public async Task History_CapturesTerminalStatusBeforeDeferredContinuation(OperationStatus terminal, string expected)
    {
        var op = NewOperation("event-history", holdCompletion: true);
        await Track(op);
        var finished = Signal();
        var recorded = Signal();
        op.OperationFinished += (_, _) => finished.TrySetResult();
        void Changed(object? sender, EventArgs args) => recorded.TrySetResult();
        OperationHistoryStore.Changed += Changed;
        try
        {
            var run = Task.Run(op.MainThread);
            _ownedTasks.Add(run);
            var attempt = await op.NextRun();
            attempt.Result.SetResult(terminal switch
            {
                OperationStatus.Succeeded => OperationVeredict.Success,
                OperationStatus.Canceled => OperationVeredict.Canceled,
                _ => OperationVeredict.Failure,
            });
            await finished.Task;
            await attempt.CompletionEntered.Task;
            NAssert.That(run.IsCompleted, Is.False);
            NAssert.That(OperationHistoryStore.GetAll(), Is.Empty, "No early output/status snapshot");
            op.Status = OperationStatus.InQueue; // Status mutation, not a claim of an actual retry.
            op.Append("terminal-after-finished-5447");
            attempt.ReleaseCompletion.SetResult();
            await run;
            await recorded.Task;
            var record = OperationHistoryStore.GetAll().Single();
            NAssert.That(record.Status, Is.EqualTo(expected));
            NAssert.That(record.Output.Select(line => line.Text), Does.Contain("terminal-after-finished-5447"));
            NAssert.That(record.Output.Select(line => line.Text), Does.Contain(terminal switch
            {
                OperationStatus.Succeeded => "event-history success",
                OperationStatus.Canceled => "Operation canceled by user",
                _ => "event-history failure",
            }));
        }
        finally { OperationHistoryStore.Changed -= Changed; }
    }

    [Test]
    public async Task History_RetrySameInstance_DoesNotOverwritePreviousRunStatus()
    {
        // The approved helper deliberately accepts an independently controlled completion task.
        // This proves deferred recording across a genuine same-instance retry, not a scheduler hook.
        var op = NewOperation("retry-history");
        var first = Task.Run(op.MainThread);
        _ownedTasks.Add(first);
        (await op.NextRun()).Result.SetResult(OperationVeredict.Failure);
        await first;
        var recordGate = Signal();
        _recordingGates.Add(recordGate);
        var firstHistory = TuiOperationRegistry.RecordHistoryAfterCompletionAsync(op, recordGate.Task, OperationStatus.Failed);
        _ownedTasks.Add(firstHistory);
        op.Retry(AbstractOperation.RetryMode.Retry);
        var secondAttempt = await op.NextRun();
        var second = op.MainThread();
        _ownedTasks.Add(second);
        NAssert.That(op.Status, Is.EqualTo(OperationStatus.Running));
        NAssert.That(OperationHistoryStore.GetAll(), Is.Empty);
        recordGate.SetResult();
        await firstHistory;
        NAssert.That(OperationHistoryStore.GetAll().Single().Status, Is.EqualTo("failed"));
        secondAttempt.Result.SetResult(OperationVeredict.Success);
        await second;
        await TuiOperationRegistry.RecordHistoryAfterCompletionAsync(op, second, OperationStatus.Succeeded);
        var records = OperationHistoryStore.GetAll();
        NAssert.That(records.Select(record => record.Status), Is.EqualTo(new[] { "succeeded", "failed" }));
        NAssert.That(records.Select(record => record.Id).Distinct().Count(), Is.EqualTo(2));
    }

    [TestCase(OperationStatus.Succeeded, OperationStatus.Failed, 0, 1, false)]
    [TestCase(OperationStatus.Failed, OperationStatus.Succeeded, 1, 0, false)]
    [TestCase(OperationStatus.Canceled, OperationStatus.Succeeded, 1, 0, false)]
    [TestCase(OperationStatus.Succeeded, OperationStatus.Failed, 0, 1, true)]
    [TestCase(OperationStatus.Failed, OperationStatus.Succeeded, 1, 0, true)]
    [TestCase(OperationStatus.Canceled, OperationStatus.Succeeded, 1, 0, true)]
    public async Task Summary_ExcludesRetainedOrRemovedPriorOutcome(
        OperationStatus priorStatus, OperationStatus currentStatus, int succeeded, int failed, bool removePrior)
    {
        var prior = NewOperation("prior-run");
        await Track(prior);
        await SetStatus(prior, priorStatus);
        await Quiet();
        if (removePrior) await OnUi(() => TuiOperationRegistry.Remove(prior));
        _summaries.Clear();
        _batches = 0;
        var current = NewOperation("current-A");
        await Track(current);
        await SetStatus(current, OperationStatus.Running);
        await DrainTimers();
        NAssert.That(_batches, Is.Zero, "Retained prior cancellation/failure must not complete active work");
        await SetStatus(current, currentStatus);
        await Quiet();
        AssertSummary(succeeded, failed);
        NAssert.That(await OnUi(TuiOperationRegistry.Snapshot),
            Is.EquivalentTo(removePrior ? new[] { current } : new[] { prior, current }));
    }

    [Test]
    public async Task Summary_RetrySameObject_InNewBatchCountsNewRun()
    {
        var op = NewOperation("batch-retry");
        await Track(op);
        var first = Task.Run(op.MainThread);
        _ownedTasks.Add(first);
        (await op.NextRun()).Result.SetResult(OperationVeredict.Failure);
        await first;
        await Fence();
        await Quiet();
        AssertSummary(0, 1);
        // Await the event-path history insertion before allowing run 2.
        await AwaitHistoryCount(1);
        _summaries.Clear();
        _batches = 0;
        op.Retry(AbstractOperation.RetryMode.Retry);
        var attempt = await op.NextRun();
        var second = op.MainThread();
        _ownedTasks.Add(second);
        await Fence();
        NAssert.That(await OnUi(() => TuiOperationRegistry.ActiveCount), Is.EqualTo(1));
        attempt.Result.SetResult(OperationVeredict.Success);
        await second;
        await Fence();
        await Quiet();
        AssertSummary(1, 0);
        await AwaitHistoryCount(2);
    }

    private static async Task AwaitHistoryCount(int count)
    {
        var recorded = Signal();
        void Changed(object? sender, EventArgs args)
        {
            if (OperationHistoryStore.GetAll().Count >= count) recorded.TrySetResult();
        }
        OperationHistoryStore.Changed += Changed;
        try
        {
            Changed(null, EventArgs.Empty);
            await recorded.Task;
        }
        finally { OperationHistoryStore.Changed -= Changed; }
    }

    [Test]
    public async Task Summary_CountsSuccessAfterAutomaticRemoval()
    {
        Settings.Set(Settings.K.MaintainSuccessfulInstalls, false);
        await OnUi(() => TuiOperationRegistry.SuccessfulRemovalDelay = TimeSpan.FromSeconds(4));
        var op = NewOperation("removed-success");
        await Track(op);
        var removed = Signal();
        void Changed()
        {
            if (!TuiOperationRegistry.Snapshot().Contains(op)) removed.TrySetResult();
        }
        TuiOperationRegistry.Changed += Changed;
        try
        {
            var run = Task.Run(op.MainThread);
            _ownedTasks.Add(run);
            (await op.NextRun()).Result.SetResult(OperationVeredict.Success);
            await run;
            await Fence();
            var firstTimer = await _clock.NextTimerAsync().WaitAsync(DefaultTimeout);
            var secondTimer = await _clock.NextTimerAsync().WaitAsync(DefaultTimeout);
            var removalTimer = new[] { firstTimer, secondTimer }.Single(timer => timer.DueTime == TimeSpan.FromSeconds(4));
            var quietTimer = new[] { firstTimer, secondTimer }.Single(timer => timer.DueTime == TimeSpan.FromMilliseconds(500));
            NAssert.That(await OnUi(TuiOperationRegistry.Snapshot), Does.Contain(op), "Success retained until removal timer fires");
            await OnUi(() => removalTimer.Fire());
            await removed.Task.WaitAsync(DefaultTimeout);
            await Fence();
            NAssert.That(await OnUi(TuiOperationRegistry.Snapshot), Is.Empty);
            NAssert.That(_batches, Is.Zero, "Removal does not publish the summary ahead of the quiet check");
            await OnUi(() => quietTimer.Fire(advance: false));
            await Fence();
            AssertSummary(1, 0);
            await AwaitHistoryCount(1);
        }
        finally { TuiOperationRegistry.Changed -= Changed; }
    }

    [Test]
    public async Task ConcurrentFinishes_EmitOneSummaryAndOneBatchCompleted()
    {
        var a = NewOperation("current-A");
        var b = NewOperation("current-B");
        await Track(a);
        await Track(b);
        await SetStatus(a, OperationStatus.Running);
        await SetStatus(b, OperationStatus.Running);
        // Both finishes queue competing revision checks before any quiet timer is released.
        await OnUi(() => { a.Status = OperationStatus.Succeeded; b.Status = OperationStatus.Failed; });
        await Fence();
        NAssert.That(_clock.Timers.Select(timer => timer.DueTime),
            Is.EqualTo(new[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500) }));
        await Quiet();
        NAssert.That(_batches, Is.Zero, "Earlier revision must lose");
        await Quiet();
        AssertSummary(1, 1);
        await DrainTimers();
        AssertSummary(1, 1);
    }

    [TestCase(OperationStatus.Running)]
    [TestCase(OperationStatus.InQueue)]
    public async Task ActiveWorkDuringQuietPeriod_PreventsPrematureCompletion(OperationStatus active)
    {
        var a = NewOperation("current-A");
        await Track(a);
        await SetStatus(a, OperationStatus.Succeeded);
        var firstQuiet = await _clock.NextTimerAsync();
        NAssert.That(firstQuiet.DueTime, Is.EqualTo(TimeSpan.FromMilliseconds(500)));
        _clock.Advance(TimeSpan.FromMilliseconds(499));
        NAssert.That(_batches, Is.Zero);
        var b = NewOperation("current-B");
        await Track(b);
        await SetStatus(b, active);
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        await OnUi(() => firstQuiet.Fire(advance: false));
        await Fence();
        NAssert.That(_summaries, Is.Empty);
        NAssert.That(_batches, Is.Zero);
        await SetStatus(b, OperationStatus.Failed);
        await Quiet();
        AssertSummary(1, 1);
    }

    [Test]
    public async Task Reset_InvalidatesOldQuietChecksWithoutClearingNewBatch()
    {
        var old = NewOperation("old-check");
        await Track(old);
        await SetStatus(old, OperationStatus.Failed);
        await OnUi(TuiOperationRegistry.Reset);
        var current = NewOperation("after-reset");
        await Track(current);
        await SetStatus(current, OperationStatus.Succeeded);
        await Quiet();
        NAssert.That(_batches, Is.Zero);
        await Quiet();
        AssertSummary(1, 0);
        await DrainTimers();
        AssertSummary(1, 0);
    }

    private sealed class ControlledOperation : AbstractOperation
    {
        internal sealed class Attempt
        {
            public TaskCompletionSource<OperationVeredict> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource CompletionEntered { get; } = Signal();
            public TaskCompletionSource ReleaseCompletion { get; } = Signal();
        }

        private readonly Channel<Attempt> _started = Channel.CreateUnbounded<Attempt>();
        private readonly List<Attempt> _attempts = [];
        private readonly bool _holdCompletion;
        private Attempt? _current;

        public ControlledOperation(string name, bool holdCompletion) : base(queue_enabled: false)
        {
            _holdCompletion = holdCompletion;
            Metadata.Title = name;
            Metadata.Status = name + " running";
            Metadata.OperationInformation = name + " information";
            Metadata.SuccessTitle = name + " succeeded";
            Metadata.SuccessMessage = name + " success";
            Metadata.FailureTitle = name + " failed";
            Metadata.FailureMessage = name + " failure";
        }

        public Task<Attempt> NextRun() => _started.Reader.ReadAsync().AsTask();
        public void Append(string text) => Line(text, LineType.Information);
        protected override void ApplyRetryAction(string retryMode) { }
        protected override Task<OperationVeredict> PerformOperation()
        {
            _current = new Attempt();
            _attempts.Add(_current);
            _started.Writer.TryWrite(_current);
            return _current.Result.Task;
        }
        protected override void OnRunCompleted()
        {
            if (_current is null) return;
            _current.CompletionEntered.TrySetResult();
            if (_holdCompletion) _current.ReleaseCompletion.Task.GetAwaiter().GetResult();
        }
        public void ReleaseAll()
        {
            foreach (var attempt in _attempts)
            {
                attempt.Result.TrySetResult(OperationVeredict.Canceled);
                attempt.ReleaseCompletion.TrySetResult();
            }
        }
        public override Task<Uri> GetOperationIcon() => Task.FromResult(new Uri("https://example.invalid/icon"));
    }

    [Test]
    public async Task Summary_CurrentCanceledOperation_ReportsZeroSucceededAndZeroFailed()
    {
        var op = NewOperation("current-canceled");
        await Track(op);
        await SetStatus(op, OperationStatus.Canceled);
        var quiet = await _clock.NextTimerAsync().WaitAsync(DefaultTimeout);
        NAssert.That(quiet.DueTime, Is.EqualTo(TimeSpan.FromMilliseconds(500)));
        NAssert.That(_summaries, Is.Empty);
        NAssert.That(_batches, Is.Zero);
        await OnUi(() => quiet.Fire());
        await Fence();
        AssertSummary(0, 0);
        NAssert.That(await OnUi(TuiOperationRegistry.Snapshot), Is.EqualTo(new[] { op }));
        NAssert.That(op.Status, Is.EqualTo(OperationStatus.Canceled));
        await DrainTimers();
        AssertSummary(0, 0);
    }

    [Test]
    public async Task Summary_RetrySameObject_InsideOriginalQuietWindowInvalidatesOldCheck()
    {
        var op = NewOperation("quiet-window-retry");
        await Track(op);
        var first = Task.Run(op.MainThread);
        _ownedTasks.Add(first);
        var firstAttempt = await op.NextRun().WaitAsync(DefaultTimeout);
        firstAttempt.Result.SetResult(OperationVeredict.Failure);
        await first.WaitAsync(DefaultTimeout);
        await Fence();
        var firstQuiet = await _clock.NextTimerAsync().WaitAsync(DefaultTimeout);
        NAssert.That(firstQuiet.DueTime, Is.EqualTo(TimeSpan.FromMilliseconds(500)));
        await AwaitHistoryCount(1);

        _clock.Advance(TimeSpan.FromMilliseconds(499));
        NAssert.That(_summaries, Is.Empty);
        NAssert.That(_batches, Is.Zero);
        op.Retry(AbstractOperation.RetryMode.Retry);
        var second = op.MainThread();
        _ownedTasks.Add(second);
        var retryAttempt = await op.NextRun().WaitAsync(DefaultTimeout);
        NAssert.That(retryAttempt, Is.Not.SameAs(firstAttempt), "Acknowledge a genuine second run of the same object");
        await Fence();
        NAssert.That(op.Status, Is.EqualTo(OperationStatus.Running));
        NAssert.That(await OnUi(() => TuiOperationRegistry.ActiveCount), Is.EqualTo(1));
        NAssert.That(await OnUi(TuiOperationRegistry.Snapshot), Is.EqualTo(new[] { op }));
        retryAttempt.Result.SetResult(OperationVeredict.Failure);
        await second.WaitAsync(DefaultTimeout);
        await Fence();
        var latestQuiet = await _clock.NextTimerAsync().WaitAsync(DefaultTimeout);
        NAssert.That(latestQuiet, Is.Not.SameAs(firstQuiet));
        NAssert.That(_clock.Timers.Select(timer => timer.DueTime),
            Is.EqualTo(new[] { TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(500) }));
        NAssert.That(op.Status, Is.EqualTo(OperationStatus.Failed));
        NAssert.That(_clock.GetElapsedTime(0), Is.EqualTo(TimeSpan.FromMilliseconds(499)));

        _clock.Advance(TimeSpan.FromMilliseconds(1));
        await OnUi(() => firstQuiet.Fire(advance: false));
        await Fence();
        NAssert.That(_summaries, Is.Empty, "The original check at 500 ms must lose even after the retry has failed");
        NAssert.That(_batches, Is.Zero);
        _clock.Advance(TimeSpan.FromMilliseconds(498));
        NAssert.That(_summaries, Is.Empty, "Immediately before the retry's own 500 ms quiet deadline");
        NAssert.That(_batches, Is.Zero);
        NAssert.That(latestQuiet.HasFired, Is.False);
        _clock.Advance(TimeSpan.FromMilliseconds(1));
        await OnUi(() => latestQuiet.Fire(advance: false));
        await Fence();
        AssertSummary(0, 1);
        await DrainTimers();
        AssertSummary(0, 1);
        await AwaitHistoryCount(2);
    }
}
