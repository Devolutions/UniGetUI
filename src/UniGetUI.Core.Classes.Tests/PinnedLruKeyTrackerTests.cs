namespace UniGetUI.Core.Classes.Tests;

public class PinnedLruKeyTrackerTests
{
    [Fact]
    public void EvictsLeastRecentlyUsedKeysPastCapacity()
    {
        var tracker = new PinnedLruKeyTracker<int>(3);

        Assert.Empty(tracker.Touch(1));
        Assert.Empty(tracker.Touch(2));
        Assert.Empty(tracker.Touch(3));
        Assert.Equal([1], tracker.Touch(4));
        Assert.Equal(3, tracker.EvictableCount);
    }

    [Fact]
    public void TouchAndRefreshMakeAKeyMostRecentlyUsed()
    {
        var tracker = new PinnedLruKeyTracker<int>(3);
        tracker.Touch(1);
        tracker.Touch(2);
        tracker.Touch(3);

        tracker.Touch(1);
        Assert.Equal([2], tracker.Touch(4));

        tracker.Refresh(3);
        Assert.Equal([1], tracker.Touch(5));
    }

    [Fact]
    public void RefreshDoesNotStartTrackingAKey()
    {
        var tracker = new PinnedLruKeyTracker<int>(2);
        tracker.Refresh(1);
        Assert.Equal(0, tracker.EvictableCount);
    }

    [Fact]
    public void PinnedKeysAreNeverEvictedNorCounted()
    {
        var tracker = new PinnedLruKeyTracker<int>(2);
        tracker.Pin(100);

        for (int i = 0; i < 10; i++)
            Assert.DoesNotContain(100, tracker.Touch(i));

        Assert.Empty(tracker.Touch(100));
        Assert.Equal(1, tracker.PinnedCount);
        Assert.Equal(2, tracker.EvictableCount);
    }

    [Fact]
    public void PinningAnEvictableKeyPromotesIt()
    {
        var tracker = new PinnedLruKeyTracker<int>(2);
        tracker.Touch(1);
        tracker.Touch(2);

        tracker.Pin(1);
        Assert.Equal(1, tracker.EvictableCount);
        Assert.Empty(tracker.Touch(3));
        Assert.Equal([2], tracker.Touch(4));
    }

    [Fact]
    public void ClearForgetsEverything()
    {
        var tracker = new PinnedLruKeyTracker<int>(2);
        tracker.Pin(1);
        tracker.Touch(2);

        tracker.Clear();

        Assert.Equal(0, tracker.PinnedCount);
        Assert.Equal(0, tracker.EvictableCount);
        tracker.Touch(1);
        tracker.Touch(3);
        Assert.Equal([1], tracker.Touch(4));
    }

    [Fact]
    public async Task ConcurrentUseStaysWithinCapacityAndEvictsEachKeyOnce()
    {
        var tracker = new PinnedLruKeyTracker<int>(100);
        var evicted = new System.Collections.Concurrent.ConcurrentBag<int>();

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 1_000; i++)
            {
                foreach (int key in tracker.Touch((t * 1_000) + i))
                    evicted.Add(key);
            }
        })));

        Assert.Equal(100, tracker.EvictableCount);
        Assert.Equal(7_900, evicted.Count);
        Assert.Equal(evicted.Count, evicted.Distinct().Count());
    }

    [Fact]
    public void RejectsANonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PinnedLruKeyTracker<int>(0));
    }
}
