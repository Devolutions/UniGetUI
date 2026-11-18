using UniGetUI.PackageOperations;
using LineType = UniGetUI.PackageOperations.AbstractOperation.LineType;

namespace UniGetUI.PackageEngine.Tests;

public class LiveOutputOrderTests
{
    [Fact]
    public void AProgressFrameStaysBeforeALineStoredAfterIt()
    {
        var merged = LiveOutputOrder.Merge(
            1,
            [("The file was saved", LineType.Information), ("Done!", LineType.Information)],
            [(1, "[=====     ] 50%")]
        );

        Assert.Equal(
            [
                ("[=====     ] 50%", LineType.ProgressIndicator),
                ("The file was saved", LineType.Information),
                ("Done!", LineType.Information),
            ],
            merged
        );
    }

    [Fact]
    public void ProgressFramesAreInterleavedAtTheirPositions()
    {
        var merged = LiveOutputOrder.Merge(
            0,
            [("a", LineType.Information), ("b", LineType.Information)],
            [(0, "p0"), (1, "p1"), (2, "p2")]
        );

        Assert.Equal(
            ["p0", "a", "p1", "b", "p2"],
            merged.Select(l => l.Text)
        );
    }

    [Fact]
    public void AProgressFrameOlderThanRenderedLinesIsDropped()
    {
        var merged = LiveOutputOrder.Merge(3, [("d", LineType.Information)], [(2, "stale")]);

        Assert.Equal([("d", LineType.Information)], merged);
    }

    [Fact]
    public void AProgressFrameAfterEveryStoredLineComesLast()
    {
        var merged = LiveOutputOrder.Merge(0, [("a", LineType.Information)], [(1, "current")]);

        Assert.Equal(["a", "current"], merged.Select(l => l.Text));
    }

    [Fact]
    public void WithNoProgressTheStoredTailIsReturnedAsIs()
    {
        var merged = LiveOutputOrder.Merge(5, [("x", LineType.StandardError)], []);

        Assert.Equal([("x", LineType.StandardError)], merged);
    }
}
