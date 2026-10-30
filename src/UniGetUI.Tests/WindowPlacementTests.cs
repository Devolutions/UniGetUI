using Avalonia;
using UniGetUI.Avalonia.Infrastructure;

namespace UniGetUI.Tests;

public class WindowPlacementTests
{
    private const double MinWidth = 700;
    private const double MinHeight = 500;

    private static PlacementScreen Screen(int x, int y, int width, int height, double scaling, bool primary = false, int taskbar = 48)
        => new(new PixelRect(x, y, width, height), new PixelRect(x, y, width, height - taskbar), scaling, primary);

    private static PixelRect PixelBounds(WindowPlacementResult placement, double scaling)
        => new(placement.Position.X, placement.Position.Y,
            (int)Math.Round(placement.Width * scaling), (int)Math.Round(placement.Height * scaling));

    private static void AssertInside(PixelRect area, PixelRect rect)
    {
        Assert.True(rect.X >= area.X && rect.Y >= area.Y && rect.Right <= area.Right && rect.Bottom <= area.Bottom,
            $"{rect} is not inside {area}");
    }

    [Fact]
    public void Default_OnLargeScreen_KeepsDefaultSizeAndCentres()
    {
        var screen = Screen(0, 0, 2560, 1440, 1.0, primary: true);

        var placement = WindowPlacement.ForDefault(screen, 1450, 900, MinWidth, MinHeight);

        Assert.Equal(1450, placement.Width);
        Assert.Equal(900, placement.Height);
        Assert.Equal(new PixelPoint((2560 - 1450) / 2, (1392 - 900) / 2), placement.Position);
    }

    [Fact]
    public void Default_OnSmallScreen_ShrinksToFitAndCentres()
    {
        var screen = Screen(0, 0, 1366, 768, 1.0, primary: true);

        var placement = WindowPlacement.ForDefault(screen, 1450, 900, MinWidth, MinHeight);

        Assert.True(placement.Width < 1450);
        Assert.True(placement.Height < 900);
        var rect = PixelBounds(placement, 1.0);
        AssertInside(screen.WorkingArea, rect);
        Assert.InRange(rect.X - screen.WorkingArea.X - (screen.WorkingArea.Right - rect.Right), -1, 1);
        Assert.InRange(rect.Y - screen.WorkingArea.Y - (screen.WorkingArea.Bottom - rect.Bottom), -1, 1);
    }

    [Fact]
    public void Default_OnScaledScreen_UsesDips()
    {
        var screen = Screen(0, 0, 1920, 1080, 1.5, primary: true);

        var placement = WindowPlacement.ForDefault(screen, 1450, 900, MinWidth, MinHeight);

        Assert.True(placement.Width <= Math.Floor(1920 * WindowPlacement.DefaultScreenFraction / 1.5));
        Assert.True(placement.Height <= Math.Floor(1032 * WindowPlacement.DefaultScreenFraction / 1.5));
        AssertInside(screen.WorkingArea, PixelBounds(placement, 1.5));
    }

    [Fact]
    public void Default_NeverGoesBelowMinimumSize()
    {
        var screen = Screen(0, 0, 640, 480, 1.0, primary: true);

        var placement = WindowPlacement.ForDefault(screen, 1450, 900, MinWidth, MinHeight);

        Assert.Equal(MinWidth, placement.Width);
        Assert.Equal(MinHeight, placement.Height);
        Assert.Equal(screen.WorkingArea.Position, placement.Position);
    }

    [Fact]
    public void Saved_ThatFits_IsKeptUnchanged()
    {
        var screens = new[] { Screen(0, 0, 1920, 1080, 1.0, primary: true) };

        var placement = WindowPlacement.FitSaved(screens, new PixelPoint(100, 50), 1200, 800, MinWidth, MinHeight);

        Assert.Equal(new WindowPlacementResult(new PixelPoint(100, 50), 1200, 800), placement);
    }

    [Fact]
    public void Saved_LargerThanScreen_ShrinksToWorkingArea()
    {
        var screen = Screen(0, 0, 1280, 720, 1.0, primary: true);

        var placement = WindowPlacement.FitSaved(new[] { screen }, new PixelPoint(0, 0), 2400, 1300, MinWidth, MinHeight);

        Assert.Equal(1280, placement.Width);
        Assert.Equal(672, placement.Height);
        Assert.Equal(new PixelPoint(0, 0), placement.Position);
        Assert.True(WindowPlacement.IsFullyVisible(new[] { screen }, placement.Position, placement.Width, placement.Height));
    }

    [Fact]
    public void Saved_OffScreenAfterResolutionChange_KeepsSizeAndMovesOnScreen()
    {
        var screen = Screen(0, 0, 1600, 900, 1.0, primary: true);

        var placement = WindowPlacement.FitSaved(new[] { screen }, new PixelPoint(2200, 900), 1000, 700, MinWidth, MinHeight);

        Assert.Equal(1000, placement.Width);
        Assert.Equal(700, placement.Height);
        Assert.Equal(new PixelPoint(600, 152), placement.Position);
        AssertInside(screen.WorkingArea, PixelBounds(placement, 1.0));
    }

    [Fact]
    public void Saved_PartlyOffScreen_SlidesBackWithoutResizing()
    {
        var screen = Screen(0, 0, 1920, 1080, 1.0, primary: true);

        var placement = WindowPlacement.FitSaved(new[] { screen }, new PixelPoint(-300, 400), 1200, 800, MinWidth, MinHeight);

        Assert.Equal(1200, placement.Width);
        Assert.Equal(800, placement.Height);
        Assert.Equal(new PixelPoint(0, 232), placement.Position);
    }

    [Fact]
    public void Saved_OnDisconnectedMonitor_MovesToNearestRemainingScreen()
    {
        var left = Screen(0, 0, 1920, 1080, 1.0, primary: true);
        var right = Screen(1920, 0, 2560, 1440, 1.0);

        var placement = WindowPlacement.FitSaved(new[] { left, right }, new PixelPoint(5000, 200), 1450, 900, MinWidth, MinHeight);

        Assert.Equal(1450, placement.Width);
        Assert.Equal(900, placement.Height);
        AssertInside(right.WorkingArea, PixelBounds(placement, 1.0));
    }

    [Fact]
    public void Saved_OnScaledSecondaryMonitor_FitsUsingThatMonitorsScaling()
    {
        var primary = Screen(0, 0, 1920, 1080, 1.0, primary: true);
        var secondary = Screen(1920, 0, 2560, 1440, 2.0, taskbar: 96);

        var placement = WindowPlacement.FitSaved(new[] { primary, secondary }, new PixelPoint(2000, 100), 1450, 900, MinWidth, MinHeight);

        Assert.Equal(1280, placement.Width);
        Assert.Equal(672, placement.Height);
        var rect = PixelBounds(placement, 2.0);
        Assert.Equal(secondary.WorkingArea, rect);
    }

    [Fact]
    public void Saved_ThatFitsOnScaledSecondaryMonitor_IsKeptUnchanged()
    {
        var primary = Screen(0, 0, 1920, 1080, 1.0, primary: true);
        var secondary = Screen(1920, 0, 3840, 2160, 2.0, taskbar: 96);

        var placement = WindowPlacement.FitSaved(new[] { primary, secondary }, new PixelPoint(2100, 100), 1450, 900, MinWidth, MinHeight);

        Assert.Equal(new WindowPlacementResult(new PixelPoint(2100, 100), 1450, 900), placement);
    }

    [Fact]
    public void Saved_WithNoScreens_IsKeptUnchanged()
    {
        var placement = WindowPlacement.FitSaved(Array.Empty<PlacementScreen>(), new PixelPoint(-5000, -5000), 1450, 900, MinWidth, MinHeight);

        Assert.Equal(new WindowPlacementResult(new PixelPoint(-5000, -5000), 1450, 900), placement);
    }
}
