namespace UniGetUI.Extensions.Xcode.Tests;

internal sealed class RecordingSynchronizationContext : SynchronizationContext
{
    private int _posts;

    public int Posts => Volatile.Read(ref _posts);

    public override void Post(SendOrPostCallback callback, object? state)
    {
        Interlocked.Increment(ref _posts);
        ThreadPool.QueueUserWorkItem(_ => callback(state));
    }
}
