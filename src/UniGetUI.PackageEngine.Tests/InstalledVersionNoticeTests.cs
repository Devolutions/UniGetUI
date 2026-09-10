using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.PackageEngine.Tests;

public sealed class InstalledVersionNoticeTests
{
    [Fact]
    public void ReturnsNothingWhenTheVersionWasRead()
    {
        Assert.Null(InstalledVersionNotice.BuildTooltip(false, "1.0.0", "WinGet"));
    }

    [Fact]
    public void NamesTheManagerAndTheRememberedVersion()
    {
        string? tooltip = InstalledVersionNotice.BuildTooltip(true, "6.0.0.3482", "WinGet");

        Assert.NotNull(tooltip);
        Assert.Contains("WinGet", tooltip);
        Assert.Contains("6.0.0.3482", tooltip);
        Assert.DoesNotContain("{0}", tooltip);
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("")]
    public void SaysNothingIsKnownWhenThereIsNoRememberedVersion(string versionString)
    {
        string? tooltip = InstalledVersionNotice.BuildTooltip(true, versionString, "WinGet");
        string? remembered = InstalledVersionNotice.BuildTooltip(true, "1.0.0", "WinGet");

        Assert.NotNull(tooltip);
        Assert.Contains("WinGet", tooltip);
        Assert.DoesNotContain("{0}", tooltip);
        Assert.NotEqual(remembered, tooltip);
    }
}
