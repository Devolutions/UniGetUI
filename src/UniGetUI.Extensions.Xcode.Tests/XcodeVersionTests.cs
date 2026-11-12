namespace UniGetUI.Extensions.Xcode.Tests;

public sealed class XcodeVersionTests
{
    [Theory]
    [InlineData("26.4", 26, 4, 0, false)]
    [InlineData("26.4.1", 26, 4, 1, false)]
    [InlineData("11", 11, 0, 0, false)]
    [InlineData("Xcode 26.4", 26, 4, 0, false)]
    [InlineData("  26.4  ", 26, 4, 0, false)]
    [InlineData("26.1 Beta", 26, 1, 0, true)]
    [InlineData("26.1 Beta 2", 26, 1, 0, true)]
    [InlineData("11 Beta 7", 11, 0, 0, true)]
    [InlineData("12.1.1 Release Candidate", 12, 1, 1, true)]
    [InlineData("26.3 Release Candidate 2", 26, 3, 0, true)]
    [InlineData("10.0 GM Seed", 10, 0, 0, true)]
    [InlineData("11.2 GM seed", 11, 2, 0, true)]
    [InlineData("26.1 GM", 26, 1, 0, true)]
    public void TryParse_ReadsTheVersionsXcodesWrites(string text, int major, int minor, int patch, bool prerelease)
    {
        Assert.True(XcodeVersion.TryParse(text, out XcodeVersion version));
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
        Assert.Equal(prerelease, version.IsPrerelease);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Xcode")]
    [InlineData("26.4 3")]
    [InlineData("--latest")]
    [InlineData("-1")]
    [InlineData("26.4; rm -rf /")]
    [InlineData("/Applications/Xcode.app")]
    [InlineData("26.4\n27.0")]
    [InlineData("Showing Xcodes for this Mac by default: Apple Silicon (arm64).")]
    public void TryParse_RejectsAnythingElse(string text)
        => Assert.False(XcodeVersion.TryParse(text, out _));

    [Fact]
    public void TryParse_RejectsNull()
        => Assert.False(XcodeVersion.TryParse(null, out _));

    [Theory]
    // A beta comes before the release candidate, which comes before the release it leads to.
    [InlineData("26.1 Beta 2", "26.1 Beta 3")]
    [InlineData("26.1 Beta 3", "26.1 Release Candidate")]
    [InlineData("26.3 Release Candidate", "26.3 Release Candidate 2")]
    [InlineData("26.1 Release Candidate", "26.1")]
    [InlineData("10.0 GM Seed", "10.0")]
    [InlineData("26.1", "26.1.1")]
    [InlineData("26.1.1", "26.2 Beta")]
    // Numbers are numbers, not text.
    [InlineData("9.4", "10.0")]
    [InlineData("26.5.1", "26.6")]
    [InlineData("26.9", "26.10")]
    public void Compare_OrdersTheWayAppleShipsThem(string older, string newer)
    {
        Assert.True(XcodeVersion.Compare(older, newer) < 0);
        Assert.True(XcodeVersion.Compare(newer, older) > 0);
    }

    [Fact]
    public void Compare_TreatsAMissingMinorAsZero()
    {
        Assert.Equal(0, XcodeVersion.Compare("26", "26.0"));
        Assert.Equal(0, XcodeVersion.Compare("26.0.0", "26"));
        Assert.Equal(0, XcodeVersion.Compare("Xcode 26.4", "26.4"));
    }

    [Theory]
    [InlineData("26.4", "26.4 Beta 2", true)]
    [InlineData("26", "26.0.0 Release Candidate", true)]
    [InlineData("26.4", "26.4.1", false)]
    [InlineData("26.4", "27.4", false)]
    [InlineData(null, "26.4", false)]
    [InlineData("not a version", "26.4", false)]
    public void SameProductVersionIgnoresOnlyReleaseLabels(string? left, string? right, bool expected)
    {
        Assert.Equal(expected, XcodeVersion.SameProductVersion(left, right));
        Assert.Equal(expected, XcodeVersion.SameProductVersion(right, left));
    }

    [Fact]
    public void Compare_PutsWhatIsNotAVersionBelowWhatIs()
    {
        Assert.True(XcodeVersion.Compare("nonsense", "1.0") < 0);
        Assert.True(XcodeVersion.Compare("1.0", "nonsense") > 0);
        Assert.True(XcodeVersion.Compare(null, "1.0") < 0);
        Assert.Equal(0, XcodeVersion.Compare("nonsense", "NONSENSE"));
    }

    [Fact]
    public void Releases_KnowWhetherTheyArePrereleases()
    {
        Assert.False(new XcodeRelease("26.4", "17E192", XcodeArchitecture.AppleSilicon, false, false).IsPrerelease);
        Assert.True(new XcodeRelease("26.5 Beta 2", "17F5022i", XcodeArchitecture.AppleSilicon, false, false).IsPrerelease);
        Assert.True(new InstalledXcode("26.5 Release Candidate", null, XcodeArchitecture.Unspecified, false, "/Applications/Xcode.app").IsPrerelease);
        Assert.False(new InstalledXcode("26.4", null, XcodeArchitecture.Unspecified, false, "/Applications/Xcode.app").IsPrerelease);
    }

    [Theory]
    [InlineData("17E192", true)]
    [InlineData("17F5012f", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("not-a-build", false)]
    [InlineData("17E192\n", false)]
    [InlineData("17E192\r\n", false)]
    [InlineData("17E192/path", false)]
    public void BuildIdentityRequiresAnEntireValidBuild(string? build, bool expected)
        => Assert.Equal(expected, XcodeSimulatorImageFile.IsValidBuild(build));
}
