using UniGetUI.Core.Tools;

namespace UniGetUI.PackageEngine.Tests;

public sealed class SemanticVersionTests
{
    [Theory]
    [InlineData("1.10.0", "1.9.0")]
    [InlineData("2.0.0", "2.0.0-beta")]
    [InlineData("2.0.0-beta.2", "2.0.0-beta.1")]
    [InlineData("2.0.0-beta", "2.0.0-alpha")]
    [InlineData("2.0.0-rc.10", "2.0.0-rc.2")]
    [InlineData("1.0.0.1", "1.0.0")]
    [InlineData("10.0.11", "2.1.0")]
    [InlineData("11.0.0-preview.7", "11.0.0-preview.6")]
    [InlineData("1.0.0-beta", "1.0.0-alpha.9")]
    public void HigherVersionsCompareGreater(string higher, string lower)
    {
        Assert.True(SemanticVersion.TryParse(higher, out var parsedHigher));
        Assert.True(SemanticVersion.TryParse(lower, out var parsedLower));

        Assert.True(parsedHigher > parsedLower);
        Assert.True(parsedLower < parsedHigher);
    }

    [Theory]
    [InlineData("1.0.0", "1.0.0.0")]
    [InlineData("1.0.0+build.5", "1.0.0")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("v1.0.0", "1.0.0")]
    public void EquivalentVersionsCompareEqual(string left, string right)
    {
        Assert.True(SemanticVersion.TryParse(left, out var parsedLeft));
        Assert.True(SemanticVersion.TryParse(right, out var parsedRight));

        Assert.Equal(0, parsedLeft.CompareTo(parsedRight));
        Assert.True(parsedLeft == parsedRight);
    }

    [Theory]
    [InlineData("1.0.0-beta", true)]
    [InlineData("1.0.0-preview.7.26381.103", true)]
    [InlineData("1.0.0", false)]
    [InlineData("1.0.0+build", false)]
    public void PreReleaseIsDetected(string version, bool expected)
    {
        Assert.True(SemanticVersion.TryParse(version, out var parsed));
        Assert.Equal(expected, parsed.IsPreRelease);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.x.3")]
    public void UnparseableVersionsAreRejected(string version)
    {
        Assert.False(SemanticVersion.TryParse(version, out var parsed));
        Assert.False(parsed.IsValid);
    }

    [Fact]
    public void InvalidVersionsOrderBelowValidOnes()
    {
        Assert.True(SemanticVersion.TryParse("0.0.1", out var valid));
        var invalid = SemanticVersion.Invalid("garbage");

        Assert.True(valid > invalid);
    }

    // SemVer 2.0 compares pre-release identifiers in ASCII order, so case matters and
    // "1.0.0-RC" precedes "1.0.0-rc". NuGet's own comparer is case-insensitive, and this type is
    // shared by both, so the mode has to be explicit.
    [Fact]
    public void PreReleaseLabelsAreCaseSensitiveByDefault()
    {
        Assert.True(SemanticVersion.TryParse("1.0.0-RC", out var upper));
        Assert.True(SemanticVersion.TryParse("1.0.0-rc", out var lower));

        Assert.True(upper < lower);
        Assert.NotEqual(0, upper.CompareTo(lower));
    }

    [Fact]
    public void PreReleaseLabelsCompareCaseInsensitivelyForNuGetFeeds()
    {
        Assert.True(
            SemanticVersion.TryParse("1.0.0-RC", SemVerLabels.CaseInsensitive, out var upper)
        );
        Assert.True(
            SemanticVersion.TryParse("1.0.0-rc", SemVerLabels.CaseInsensitive, out var lower)
        );

        Assert.Equal(0, upper.CompareTo(lower));
    }

    [Fact]
    public void OriginalStringIsPreserved()
    {
        Assert.True(SemanticVersion.TryParse("1.0.0.0-Beta+meta", out var parsed));
        Assert.Equal("1.0.0.0-Beta+meta", parsed.Original);
        Assert.Equal("1.0.0.0-Beta+meta", parsed.ToString());
    }
}
