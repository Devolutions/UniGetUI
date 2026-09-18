using System.Diagnostics;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageOperations;
using LineType = UniGetUI.PackageOperations.AbstractOperation.LineType;

namespace UniGetUI.PackageEngine.Tests;

/// <summary>
/// Covers the manager-neutral <see cref="OperationProgress"/> model, the generic
/// monotonic throughput tracker, stale-speed expiry, and subscriber isolation.
/// Download throttling lives in <see cref="DownloadOperation"/> and is covered by
/// <c>DownloadOperationProgressTests</c>; card visuals by
/// <c>OperationCardProgressStateTests</c> and <c>OperationCardControllerTests</c>.
/// </summary>
public sealed class OperationProgressTests
{
    private sealed class ProgressProbeOperation : AbstractOperation
    {
        public ProgressProbeOperation()
            : base(queue_enabled: false)
        {
            Metadata.Status = "probe status";
            Metadata.Title = "probe title";
            Metadata.OperationInformation = "probe info";
            Metadata.SuccessTitle = "probe success";
            Metadata.SuccessMessage = "probe success";
            Metadata.FailureTitle = "probe failure";
            Metadata.FailureMessage = "probe failure";
        }

        public void ReportForTests(OperationProgress progress) => ReportProgress(progress);

        public void EmitForTests(string line, LineType type) => Line(line, type);

        public void SetClockForTests(Func<long> provider) =>
            SetTimestampProviderForTests(provider);

        protected override void ApplyRetryAction(string retryMode) { }

        protected override Task<OperationVeredict> PerformOperation() =>
            Task.FromResult(OperationVeredict.Success);

        public override Task<Uri> GetOperationIcon() =>
            Task.FromResult(new Uri("avares://UniGetUI/Assets/package_color.png"));
    }

    /// <summary>
    /// Deterministic monotonic clock. Production uses Stopwatch.GetTimestamp();
    /// tests advance explicitly by Stopwatch frequency ticks.
    /// </summary>
    private sealed class ManualTimestampClock
    {
        private long _ticks;

        public long Now() => _ticks;

        public void Advance(TimeSpan delta) =>
            _ticks += (long)(delta.TotalSeconds * Stopwatch.Frequency);
    }

    private const ulong OneMiB = 1024UL * 1024;
    private const ulong TenMiB = 10UL * 1024 * 1024;

    private static (ProgressProbeOperation Op, ManualTimestampClock Clock) CreateClockedProbe()
    {
        var op = new ProgressProbeOperation();
        var clock = new ManualTimestampClock();
        op.SetClockForTests(clock.Now);
        return (op, clock);
    }

    private static void ReportDownload(
        ProgressProbeOperation op,
        ulong downloaded,
        ulong total = TenMiB
    ) => op.ReportForTests(OperationProgress.FromDownload(downloaded, total));

    // ── Model: determinate vs unknown ──────────────────────────────────────

    [Fact]
    public void Unknown_IsIndeterminate()
    {
        Assert.False(OperationProgress.Unknown.IsDeterminate);
        Assert.Null(OperationProgress.Unknown.Percentage);
        Assert.False(OperationProgress.Unknown.HasThroughput);
    }

    [Fact]
    public void FromDownload_Mid_IsDeterminateWithDerivedPercentage()
    {
        var progress = OperationProgress.FromDownload(326, 624);

        Assert.True(progress.IsDeterminate);
        Assert.Equal(326UL, progress.BytesDownloaded);
        Assert.Equal(624UL, progress.BytesTotal);
        Assert.Equal(52, Math.Round(progress.Percentage!.Value));
    }

    [Fact]
    public void FromDownload_ZeroTotal_IsIndeterminate_NotFakeZero()
    {
        var progress = OperationProgress.FromDownload(1234, 0);

        Assert.False(progress.IsDeterminate);
        Assert.Null(progress.Percentage);
    }

    [Fact]
    public void FromDownload_Overshoot_ClampsPercentageKeepsRealBytes()
    {
        var progress = OperationProgress.FromDownload(150, 100);

        Assert.True(progress.IsDeterminate);
        Assert.Equal(100, progress.Percentage);
        Assert.Equal(150UL, progress.BytesDownloaded);
    }

    // ── Throughput: monotonic clock, real bytes over real time ─────────────

    [Fact]
    public void FirstDownloadSample_HasNoSpeed()
    {
        var (op, _) = CreateClockedProbe();
        using (op)
        {
            ReportDownload(op, OneMiB);

            Assert.True(op.CurrentProgress.IsDeterminate);
            Assert.Null(op.CurrentProgress.BytesPerSecond);
        }
    }

    [Fact]
    public void SecondValidSample_CalculatesDeltaBytesOverDeltaTime()
    {
        var (op, clock) = CreateClockedProbe();
        using (op)
        {
            ReportDownload(op, 0);
            clock.Advance(TimeSpan.FromSeconds(1));
            ReportDownload(op, OneMiB);

            Assert.Equal((double)OneMiB, op.CurrentProgress.BytesPerSecond);
        }
    }

    [Fact]
    public void ZeroTimeDelta_PreservesPreviousSpeedWithoutNaN()
    {
        var (op, clock) = CreateClockedProbe();
        using (op)
        {
            ReportDownload(op, 0);
            ReportDownload(op, OneMiB);
            Assert.Null(op.CurrentProgress.BytesPerSecond);

            clock.Advance(TimeSpan.FromSeconds(1));
            ReportDownload(op, 2 * OneMiB);
            double? speed = op.CurrentProgress.BytesPerSecond;
            Assert.NotNull(speed);

            ReportDownload(op, 3 * OneMiB);
            Assert.Equal(speed, op.CurrentProgress.BytesPerSecond);
        }
    }

    [Fact]
    public void BackwardByteCounter_ResetsSpeedAndStartsNewBaseline()
    {
        var (op, clock) = CreateClockedProbe();
        using (op)
        {
            ReportDownload(op, 0);
            clock.Advance(TimeSpan.FromSeconds(1));
            ReportDownload(op, 2 * OneMiB);
            Assert.NotNull(op.CurrentProgress.BytesPerSecond);

            clock.Advance(TimeSpan.FromSeconds(1));
            ReportDownload(op, 512);
            Assert.Null(op.CurrentProgress.BytesPerSecond);

            clock.Advance(TimeSpan.FromSeconds(1));
            ReportDownload(op, 512 + OneMiB);
            Assert.Equal((double)OneMiB, op.CurrentProgress.BytesPerSecond);
        }
    }

    [Fact]
    public void Smoothing_IsDeterministicExponentialMovingAverage()
    {
        static double? RunSequence()
        {
            var (op, clock) = CreateClockedProbe();
            using (op)
            {
                ReportDownload(op, 0);
                clock.Advance(TimeSpan.FromSeconds(1));
                ReportDownload(op, OneMiB);
                clock.Advance(TimeSpan.FromSeconds(1));
                ReportDownload(op, 3 * OneMiB);
                return op.CurrentProgress.BytesPerSecond;
            }
        }

        double? first = RunSequence();
        double? second = RunSequence();

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.InRange(first!.Value, 1.3 * OneMiB - 1, 1.3 * OneMiB + 1);
    }

    [Fact]
    public void StageTransition_ResetsSpeed()
    {
        var (op, clock) = CreateClockedProbe();
        using (op)
        {
            ReportDownload(op, 0);
            clock.Advance(TimeSpan.FromSeconds(1));
            ReportDownload(op, OneMiB);
            Assert.NotNull(op.CurrentProgress.BytesPerSecond);

            op.ReportForTests(OperationProgress.ForStage(OperationProgressStage.Installing));
            Assert.Null(op.CurrentProgress.BytesPerSecond);

            clock.Advance(TimeSpan.FromSeconds(1));
            ReportDownload(op, 2 * OneMiB);
            Assert.Null(op.CurrentProgress.BytesPerSecond);
        }
    }

    // ── Stale speed expiry ─────────────────────────────────────────────────

    [Fact]
    public void FreshSpeed_IsVisible_AndArmsExpiryTimer()
    {
        var (op, clock) = CreateClockedProbe();
        using (op)
        {
            ReportDownload(op, 0);
            clock.Advance(TimeSpan.FromSeconds(1));
            ReportDownload(op, OneMiB);

            Assert.NotNull(op.CurrentProgress.BytesPerSecond);
            Assert.True(op.IsStaleSpeedTimerArmedForTests());
        }
    }

    [Fact]
    public void StaleSpeed_Disappears_AfterTimeout()
    {
        var (op, clock) = CreateClockedProbe();
        using (op)
        {
            var seen = new List<OperationProgress>();
            op.ProgressChanged += (_, p) => seen.Add(p);

            ReportDownload(op, 0);
            clock.Advance(TimeSpan.FromSeconds(1));
            ReportDownload(op, OneMiB);
            Assert.NotNull(op.CurrentProgress.BytesPerSecond);

            clock.Advance(TimeSpan.FromSeconds(3));
            Assert.True(op.ExpireStaleSpeedForTests());
            Assert.Null(op.CurrentProgress.BytesPerSecond);
            Assert.Contains(seen, static p => p.BytesPerSecond is null);
        }
    }

    [Fact]
    public void FreshSample_AfterStale_RestoresSpeed()
    {
        var (op, clock) = CreateClockedProbe();
        using (op)
        {
            ReportDownload(op, 0);
            clock.Advance(TimeSpan.FromSeconds(1));
            ReportDownload(op, OneMiB);

            clock.Advance(TimeSpan.FromSeconds(3));
            Assert.True(op.ExpireStaleSpeedForTests());
            Assert.Null(op.CurrentProgress.BytesPerSecond);

            clock.Advance(TimeSpan.FromSeconds(1));
            ReportDownload(op, 2 * OneMiB);
            Assert.NotNull(op.CurrentProgress.BytesPerSecond);
        }
    }

    [Fact]
    public void RepeatedCounter_WhenStale_DoesNotFabricateSpeed()
    {
        var (op, clock) = CreateClockedProbe();
        using (op)
        {
            ReportDownload(op, 0);
            clock.Advance(TimeSpan.FromSeconds(1));
            ReportDownload(op, OneMiB);
            double? speed = op.CurrentProgress.BytesPerSecond;
            Assert.NotNull(speed);

            // Fresh repeated counter preserves the previous speed.
            clock.Advance(TimeSpan.FromMilliseconds(500));
            ReportDownload(op, OneMiB);
            Assert.Equal(speed, op.CurrentProgress.BytesPerSecond);

            // Stalled past the timeout drops the speed instead of preserving it.
            clock.Advance(TimeSpan.FromSeconds(3));
            ReportDownload(op, OneMiB);
            Assert.Null(op.CurrentProgress.BytesPerSecond);
        }
    }

    [Fact]
    public void StageReset_StopsExpiryMechanism()
    {
        var (op, clock) = CreateClockedProbe();
        using (op)
        {
            ReportDownload(op, 0);
            clock.Advance(TimeSpan.FromSeconds(1));
            ReportDownload(op, OneMiB);
            Assert.True(op.IsStaleSpeedTimerArmedForTests());

            op.ReportForTests(OperationProgress.ForStage(OperationProgressStage.Installing));
            Assert.False(op.IsStaleSpeedTimerArmedForTests());
            Assert.False(op.ExpireStaleSpeedForTests());
        }
    }

    // ── Subscriber isolation: progress never fails the operation ───────────

    [Fact]
    public void ThrowingSubscriber_DoesNotEscape_AndLaterSubscriberStillReceives()
    {
        using var op = new ProgressProbeOperation();
        bool secondReceived = false;
        op.ProgressChanged += (_, _) => throw new InvalidOperationException("display bug");
        op.ProgressChanged += (_, _) => secondReceived = true;

        var progress = OperationProgress.FromDownload(50, 100);
        var ex = Record.Exception(() => op.ReportForTests(progress));

        Assert.Null(ex);
        Assert.True(secondReceived);
        Assert.Equal(50, op.CurrentProgress.Percentage);
    }

    [Fact]
    public void ThrowingSubscriber_DoesNotTurnSuccessfulOperationIntoFailure()
    {
        using var op = new ProgressProbeOperation();
        op.ProgressChanged += (_, _) => throw new InvalidOperationException("display bug");

        var ex = Record.Exception(() =>
            op.ReportForTests(OperationProgress.FromDownload(10, 100))
        );

        Assert.Null(ex);
        Assert.True(op.CurrentProgress.IsDeterminate);
    }

    // ── Separation: progress never touches log/history ─────────────────────

    [Fact]
    public void ReportProgress_DoesNotWriteToOperationOutput()
    {
        using var op = new ProgressProbeOperation();
        var clock = new ManualTimestampClock();
        op.SetClockForTests(clock.Now);

        op.ReportForTests(OperationProgress.ForStage(OperationProgressStage.Downloading));
        ReportDownload(op, OneMiB);
        clock.Advance(TimeSpan.FromSeconds(1));
        ReportDownload(op, 2 * OneMiB);
        op.ReportForTests(OperationProgress.Unknown);

        Assert.Empty(op.GetOutput());
    }

    [Fact]
    public void ProgressIndicatorLogLines_DoNotCreateStructuredProgress()
    {
        using var op = new ProgressProbeOperation();
        int progressEvents = 0;
        op.ProgressChanged += (_, _) => progressEvents++;

        op.EmitForTests("[###.....] 30% (3.0 MB/10.0 MB)", LineType.ProgressIndicator);
        op.EmitForTests("Fetching download url...", LineType.Information);

        Assert.Equal(0, progressEvents);
        Assert.Equal(OperationProgress.Unknown, op.CurrentProgress);
        Assert.Single(op.GetOutput());
    }

    // ── Formatter (small representative set) ───────────────────────────────

    [Fact]
    public void Formatter_DeterminateDownload_IncludesPercentAndByteCounters()
    {
        string text = OperationProgressFormatter.Format(
            OperationProgress.FromDownload(21 * OneMiB, 100 * OneMiB)
        );

        Assert.Contains("21%", text);
        Assert.Contains("/", text);
        Assert.DoesNotContain("/s", text);
    }

    [Fact]
    public void Formatter_DownloadWithSpeed_AppendsThroughput()
    {
        var progress =
            OperationProgress.FromDownload(21 * OneMiB, 100 * OneMiB)
            with
            {
                BytesPerSecond = 1.2 * OneMiB,
            };

        string text = OperationProgressFormatter.Format(progress);

        Assert.Contains("21%", text);
        Assert.Contains("/s", text);
    }

    [Fact]
    public void Formatter_StaleSpeed_IsOmitted()
    {
        var progress = OperationProgress.FromDownload(50, 100) with { BytesPerSecond = (double?)null };

        Assert.DoesNotContain("/s", OperationProgressFormatter.Format(progress));
    }
}
