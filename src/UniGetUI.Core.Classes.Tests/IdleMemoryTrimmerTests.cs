namespace UniGetUI.Core.Classes.Tests;

public sealed class IdleMemoryTrimmerTests : IDisposable
{
    private readonly Func<bool> _originalIsAppInBackground = IdleMemoryTrimmer.IsAppInBackground;
    private readonly TimeSpan _originalQuietPeriod = IdleMemoryTrimmer.QuietPeriod;

    public void Dispose()
    {
        IdleMemoryTrimmer.IsAppInBackground = _originalIsAppInBackground;
        IdleMemoryTrimmer.QuietPeriod = _originalQuietPeriod;
    }

    private static async Task<bool> WaitForTrimCountAsync(int expected, int timeoutMs)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < timeoutMs)
        {
            if (IdleMemoryTrimmer.TrimCount >= expected)
                return true;
            await Task.Delay(20);
        }
        return IdleMemoryTrimmer.TrimCount >= expected;
    }

    [Fact]
    public async Task TrimsOnceTheAppHasBeenQuietInTheBackground()
    {
        IdleMemoryTrimmer.IsAppInBackground = static () => true;
        IdleMemoryTrimmer.QuietPeriod = TimeSpan.FromMilliseconds(50);
        int before = IdleMemoryTrimmer.TrimCount;

        IdleMemoryTrimmer.Request();

        Assert.True(await WaitForTrimCountAsync(before + 1, 5000));
    }

    [Fact]
    public async Task DoesNotTrimWhileTheAppIsInTheForeground()
    {
        IdleMemoryTrimmer.IsAppInBackground = static () => false;
        IdleMemoryTrimmer.QuietPeriod = TimeSpan.FromMilliseconds(50);
        int before = IdleMemoryTrimmer.TrimCount;

        IdleMemoryTrimmer.Request();
        await Task.Delay(500);

        Assert.Equal(before, IdleMemoryTrimmer.TrimCount);
    }

    [Fact]
    public async Task ABurstOfRequestsTrimsOnlyOnce()
    {
        IdleMemoryTrimmer.IsAppInBackground = static () => true;
        IdleMemoryTrimmer.QuietPeriod = TimeSpan.FromMilliseconds(300);
        int before = IdleMemoryTrimmer.TrimCount;

        for (int i = 0; i < 25; i++)
            IdleMemoryTrimmer.Request();

        Assert.True(await WaitForTrimCountAsync(before + 1, 5000));
        await Task.Delay(700);
        Assert.Equal(before + 1, IdleMemoryTrimmer.TrimCount);
    }

    [Fact]
    public void TrimNowReleasesUnreachableMemory()
    {
        var garbage = new List<byte[]>();
        for (int i = 0; i < 64; i++)
            garbage.Add(new byte[1024 * 1024]);
        long withGarbage = GC.GetTotalMemory(forceFullCollection: false);
        garbage.Clear();

        IdleMemoryTrimmer.TrimNow();

        Assert.True(GC.GetTotalMemory(forceFullCollection: false) < withGarbage - (32L * 1024 * 1024));
    }
}
