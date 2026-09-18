using UniGetUI.Core.Tools;

namespace UniGetUI.PackageEngine.Tests;

public sealed class PythonVersionSpecifierTests
{
    [Theory]
    [InlineData(">=3.9", "3.8", false)]
    [InlineData(">=3.9", "3.9.7", true)]
    [InlineData(">=3.9", "3.10.2", true)]
    [InlineData(">=3.9", "3.13.15", true)]
    [InlineData(">=3.9", "4.0", true)]
    [InlineData(">=3.8,<4", "3.8", true)]
    [InlineData(">=3.8,<4", "3.13.15", true)]
    [InlineData(">=3.8,<4", "4.0", false)]
    [InlineData(">=3.8, <3.13", "3.11.2", true)]
    [InlineData(">=3.8, <3.13", "3.13.0", false)]
    [InlineData("!=3.9.*", "3.8", true)]
    [InlineData("!=3.9.*", "3.9.7", false)]
    [InlineData("!=3.9.*", "3.10.2", true)]
    [InlineData(">=3.7,!=3.9.*", "3.9.7", false)]
    [InlineData(">=3.7,!=3.9.*", "3.10.2", true)]
    [InlineData("~=3.10", "3.9.7", false)]
    [InlineData("~=3.10", "3.10.2", true)]
    [InlineData("~=3.10", "3.13.15", true)]
    [InlineData("~=3.10", "4.0", false)]
    [InlineData("~=3.10.2", "3.10.2", true)]
    [InlineData("~=3.10.2", "3.11.0", false)]
    [InlineData("==3.*", "3.13.15", true)]
    [InlineData("==3.*", "4.0", false)]
    [InlineData("==3.11.*", "3.10.2", false)]
    [InlineData("==3.11.*", "3.11.0", true)]
    [InlineData("==3.11.*", "3.11.2", true)]
    [InlineData(">3.9", "3.8", false)]
    [InlineData(">3.9", "3.9.7", true)]
    [InlineData("<=3.11", "3.11.0", true)]
    [InlineData("<=3.11", "3.11.2", false)]
    [InlineData("===3.11.2", "3.11.0", false)]
    [InlineData("===3.11.2", "3.11.2", true)]
    [InlineData(">=3.13.2", "3.13.0", false)]
    [InlineData(">=3.13.2", "3.13.15", true)]
    [InlineData(">= 3.9", "3.9.7", true)]
    [InlineData(">=3.9,", "3.9.7", true)]
    [InlineData(">=3.13.0rc1", "3.12.0", false)]
    [InlineData(">=3.13.0rc1", "3.13.0", true)]
    [InlineData("~=3.11.0rc1", "3.10.2", false)]
    [InlineData("~=3.11.0rc1", "3.11.0", true)]
    [InlineData("~=3.11.0rc1", "3.11.2", true)]
    [InlineData("~=3.11.0rc1", "3.12.0", false)]
    [InlineData("~=3.11.0.post1", "3.11.0", false)]
    [InlineData("~=3.11.0.post1", "3.11.2", true)]
    [InlineData("~=3.11.0.post1", "3.12.0", false)]
    public void SpecifierSetsMatchThePackagingLibrary(
        string specifier,
        string version,
        bool expected
    )
    {
        Assert.True(PythonVersionSpecifier.TryParse(specifier, out var parsed));
        Assert.True(PythonVersion.TryParse(version, out var parsedVersion));

        Assert.Equal(expected, parsed.IsSatisfiedBy(parsedVersion));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("3.9")]
    [InlineData(">=")]
    [InlineData(">=not-a-version")]
    [InlineData(">=3.9.*")]
    [InlineData("==3.9.*.*")]
    [InlineData("~=3")]
    public void UnusableSpecifiersAreRejected(string specifier)
    {
        Assert.False(PythonVersionSpecifier.TryParse(specifier, out _));
    }

    [Fact]
    public void ADefaultSpecifierNeverMatches()
    {
        Assert.True(PythonVersion.TryParse("3.12.0", out var version));

        Assert.False(default(PythonVersionSpecifier).IsSatisfiedBy(version));
    }
}
