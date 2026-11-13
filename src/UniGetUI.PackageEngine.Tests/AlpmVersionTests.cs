using UniGetUI.PackageEngine.Managers.PacmanManager;

namespace UniGetUI.PackageEngine.Tests;

public sealed class AlpmVersionTests
{
    [Theory]
    [InlineData("1.5.0", "1.5.0", 0)]
    [InlineData("1.5.1", "1.5.0", 1)]
    [InlineData("1.5.1", "1.5", 1)]
    [InlineData("1.5.0-1", "1.5.0-1", 0)]
    [InlineData("1.5.0-1", "1.5.0-2", -1)]
    [InlineData("1.5.0-1", "1.5.1-1", -1)]
    [InlineData("1.5.0-2", "1.5.1-1", -1)]
    [InlineData("1.5-1", "1.5.1-1", -1)]
    [InlineData("1.5-2", "1.5.1-1", -1)]
    [InlineData("1.5-2", "1.5.1-2", -1)]
    [InlineData("1.5", "1.5-1", 0)]
    [InlineData("1.5-1", "1.5", 0)]
    [InlineData("1.1-1", "1.1", 0)]
    [InlineData("1.0-1", "1.1", -1)]
    [InlineData("1.1-1", "1.0", 1)]
    [InlineData("1.5b-1", "1.5-1", -1)]
    [InlineData("1.5b", "1.5", -1)]
    [InlineData("1.5b-1", "1.5", -1)]
    [InlineData("1.5b", "1.5.1", -1)]
    [InlineData("1.0a", "1.0alpha", -1)]
    [InlineData("1.0alpha", "1.0b", -1)]
    [InlineData("1.0b", "1.0beta", -1)]
    [InlineData("1.0beta", "1.0rc", -1)]
    [InlineData("1.0rc", "1.0", -1)]
    [InlineData("1.5.a", "1.5", 1)]
    [InlineData("1.5.b", "1.5.a", 1)]
    [InlineData("1.5.1", "1.5.b", 1)]
    [InlineData("1.5.b-1", "1.5.b", 0)]
    [InlineData("1.5-1", "1.5.b", -1)]
    [InlineData("2.0", "2_0", 0)]
    [InlineData("2.0_a", "2_0.a", 0)]
    [InlineData("2.0a", "2.0.a", -1)]
    [InlineData("2___a", "2_a", 1)]
    [InlineData("0:1.0", "0:1.0", 0)]
    [InlineData("0:1.0", "0:1.1", -1)]
    [InlineData("1:1.0", "0:1.0", 1)]
    [InlineData("1:1.0", "0:1.1", 1)]
    [InlineData("1:1.0", "2:1.1", -1)]
    [InlineData("1:1.0", "0:1.0-1", 1)]
    [InlineData("1:1.0-1", "0:1.1-1", 1)]
    [InlineData("0:1.0", "1.0", 0)]
    [InlineData("0:1.0", "1.1", -1)]
    [InlineData("0:1.1", "1.0", 1)]
    [InlineData("1:1.0", "1.0", 1)]
    [InlineData("1:1.0", "1.1", 1)]
    [InlineData("1:1.1", "1.1", 1)]
    [InlineData("1.0.10-1", "1.0.9-1", 1)]
    [InlineData("1.0.010", "1.0.10", 0)]
    public void MatchesLibalpmVercmp(string a, string b, int expected)
    {
        Assert.Equal(expected, AlpmVersion.Compare(a, b));
        Assert.Equal(-expected, AlpmVersion.Compare(b, a));
    }

    [Fact]
    public void PacmanUsesTheAlpmComparator()
    {
        Assert.Equal(-1, new Managers.PacmanManager.Pacman().CompareVersions("1.0-2", "1.0.1-1"));
    }

    [Theory]
    [InlineData(false, "-Q")]
    [InlineData(true, "-Qn")]
    public void PacmanLeavesForeignPackagesToTheAurManagerWhenItIsReady(bool listedElsewhere, string expected)
    {
        Assert.Equal(expected, Managers.PacmanManager.Pacman.InstalledListingArguments(listedElsewhere));
    }
}
