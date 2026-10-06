using System.Threading.Channels;

namespace UniGetUI.Tui.Tests;

/// <summary>One-shot timers acknowledged by the test before it advances time. No wall-clock waits.</summary>
internal sealed class RegressionTimeProvider : TimeProvider
{
    private long _ticks;
    private readonly Channel<ManualTimer> _created = Channel.CreateUnbounded<ManualTimer>();
    private readonly List<ManualTimer> _timers = [];

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref _ticks);
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(GetTimestamp());
    public IReadOnlyList<ManualTimer> Timers
    {
        get
        {
            lock (_timers) return _timers.ToArray();
        }
    }
    public Task<ManualTimer> NextTimerAsync() => _created.Reader.ReadAsync().AsTask();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state, dueTime, period);
        lock (_timers) _timers.Add(timer);
        _created.Writer.TryWrite(timer);
        return timer;
    }

    public void Advance(TimeSpan amount) => Interlocked.Add(ref _ticks, amount.Ticks);

    internal sealed class ManualTimer(
        RegressionTimeProvider clock, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        private int _disposed;
        public TimeSpan DueTime { get; private set; } = dueTime;
        public TimeSpan Period { get; private set; } = period;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;
        public bool HasFired { get; private set; }

        public void Fire(bool advance = true)
        {
            if (IsDisposed || HasFired) return;
            HasFired = true;
            if (advance) clock.Advance(DueTime);
            callback(state);
        }

        public bool Change(TimeSpan due, TimeSpan repeat)
        {
            if (IsDisposed) return false;
            DueTime = due;
            Period = repeat;
            return true;
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
