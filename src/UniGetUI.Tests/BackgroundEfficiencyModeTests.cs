using UniGetUI.Avalonia.Infrastructure;

namespace UniGetUI.Tests;

public class BackgroundEfficiencyModeTests
{
    [Fact]
    public void IsOnlyWantedInTheBackgroundOnceLoadedAndIdle()
    {
        Assert.True(BackgroundEfficiencyMode.ShouldBeEnabled(
            disabledBySetting: false, windowOnScreen: false, managersLoaded: true, anyActiveOperation: false));
    }

    [Theory]
    [InlineData(true, false, true, false)] // turned off in settings
    [InlineData(false, true, true, false)] // the user is looking at the window
    [InlineData(false, false, false, false)] // still starting up, e.g. a start straight to the tray
    [InlineData(false, false, true, true)] // an operation is queued or running
    public void IsNotWantedOtherwise(
        bool disabledBySetting, bool windowOnScreen, bool managersLoaded, bool anyActiveOperation)
    {
        Assert.False(BackgroundEfficiencyMode.ShouldBeEnabled(
            disabledBySetting, windowOnScreen, managersLoaded, anyActiveOperation));
    }
}
