using UniGetUI.Core.Tools;

namespace UniGetUI.PackageEngine.Tests;

public sealed class PythonVersionTests
{
    // Every expectation below was generated from Python's packaging library, which is the
    // reference implementation of PEP 440, rather than written by hand.
    [Theory]
    [InlineData("1.0.0rc1", "1.0.0", -1)]
    [InlineData("1.0.0b1", "1.0.0", -1)]
    [InlineData("1.0.0a1", "1.0.0", -1)]
    [InlineData("1.0.0rc1", "1.0.0rc2", -1)]
    [InlineData("1.0.0a2", "1.0.0b1", -1)]
    [InlineData("1.0.0b2", "1.0.0rc1", -1)]
    [InlineData("1.0.0alpha1", "1.0.0a1", 0)]
    [InlineData("1.0.0beta1", "1.0.0b1", 0)]
    [InlineData("1.0.0c1", "1.0.0rc1", 0)]
    [InlineData("1.0.0pre1", "1.0.0rc1", 0)]
    [InlineData("1.0.0preview1", "1.0.0rc1", 0)]
    [InlineData("1.0.0-rc1", "1.0.0rc1", 0)]
    [InlineData("1.0.0_rc1", "1.0.0rc1", 0)]
    [InlineData("1.0.0.rc1", "1.0.0rc1", 0)]
    [InlineData("1.0.0rc", "1.0.0rc0", 0)]
    [InlineData("1.0.0.post1", "1.0.0", 1)]
    [InlineData("1.0.0-post1", "1.0.0.post1", 0)]
    [InlineData("1.0.0.rev1", "1.0.0.post1", 0)]
    [InlineData("1.0.0.r1", "1.0.0.post1", 0)]
    [InlineData("1.0.0-1", "1.0.0", 1)]
    [InlineData("1.0.0-1", "1.0.0.post1", 0)]
    [InlineData("1.0.0-2", "1.0.0-1", 1)]
    [InlineData("1.0.0.dev1", "1.0.0", -1)]
    [InlineData("1.0.0.dev1", "1.0.0a1", -1)]
    [InlineData("1.0.0a1.dev1", "1.0.0a1", -1)]
    [InlineData("1.0.0.post1.dev1", "1.0.0.post1", -1)]
    [InlineData("1.0.0.post1.dev1", "1.0.0", 1)]
    [InlineData("1.0.0.dev2", "1.0.0.dev1", 1)]
    [InlineData("1.0", "1.0.0", 0)]
    [InlineData("1.0.0.0", "1.0", 0)]
    [InlineData("1.2.3.4.5", "1.2.3.4", 1)]
    [InlineData("1.10", "1.9", 1)]
    [InlineData("1!1.0", "2.0", 1)]
    [InlineData("1!1.0", "1.0", 1)]
    [InlineData("2!1.0", "1!9.9", 1)]
    [InlineData("1.0+local", "1.0", 1)]
    [InlineData("1.0+local.2", "1.0+local.1", 1)]
    [InlineData("v1.0.0", "1.0.0", 0)]
    [InlineData("1.0.0RC1", "1.0.0rc1", 0)]
    [InlineData("  1.0.0  ", "1.0.0", 0)]
    public void OrderingMatchesThePep440ReferenceImplementation(string a, string b, int expected)
    {
        Assert.True(PythonVersion.TryParse(a, out var parsedA), $"could not parse {a}");
        Assert.True(PythonVersion.TryParse(b, out var parsedB), $"could not parse {b}");

        Assert.Equal(expected, Math.Sign(parsedA.CompareTo(parsedB)));
        Assert.Equal(-expected, Math.Sign(parsedB.CompareTo(parsedA)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-version")]
    [InlineData("1.0.0-abc")]
    [InlineData("1.0.0..0")]
    [InlineData("1..0")]
    [InlineData("1.0.0+")]
    [InlineData("hello")]
    [InlineData("8b640eef")]
    public void UnparseableVersionsAreRejected(string version)
    {
        Assert.False(PythonVersion.TryParse(version, out var parsed));
        Assert.False(parsed.IsValid);
    }

    [Theory]
    [InlineData("1.0.0rc1", true)]
    [InlineData("1.0.0a1", true)]
    [InlineData("1.0.0.dev1", true)]
    [InlineData("1.0.0a1.dev1", true)]
    [InlineData("1.0.0", false)]
    [InlineData("1.0.0.post1", false)]
    [InlineData("1.0.0+local", false)]
    public void PreReleaseIsDetected(string version, bool expected)
    {
        Assert.True(PythonVersion.TryParse(version, out var parsed));
        Assert.Equal(expected, parsed.IsPreRelease);
    }

    [Fact]
    public void ALocalSegmentNamedDevIsNotADevRelease()
    {
        Assert.True(PythonVersion.TryParse("1.0.0+devbuild", out var local));
        Assert.True(PythonVersion.TryParse("1.0.0", out var release));

        Assert.False(local.IsPreRelease);
        Assert.True(local > release);
    }

    [Fact]
    public void InvalidVersionsOrderBelowValidOnes()
    {
        Assert.True(PythonVersion.TryParse("0.0.1", out var valid));
        PythonVersion invalid = default;

        Assert.True(valid > invalid);
        Assert.Equal(string.Empty, invalid.ToString());
    }

    [Fact]
    public void OriginalStringIsPreserved()
    {
        Assert.True(PythonVersion.TryParse("1.0.0RC1", out var parsed));
        Assert.Equal("1.0.0RC1", parsed.Original);
    }
}
