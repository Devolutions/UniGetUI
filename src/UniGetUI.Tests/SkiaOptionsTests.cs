using Avalonia;
using UniGetUI.Avalonia.Infrastructure;

namespace UniGetUI.Tests;

public sealed class SkiaOptionsTests
{
    private const long BytesPerPixel = 4;

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1920, 1080)]
    [InlineData(3456, 2168)]
    public void SmallerScreensGetTheMeasuredBudget(int width, int height)
    {
        Assert.Equal(
            AvaloniaAppHost.MinimumGpuResourceCacheBytes,
            AvaloniaAppHost.GpuResourceCacheBytes((long)width * height));
    }

    [Theory]
    [InlineData(3840, 2160)]
    [InlineData(5120, 2880)]
    [InlineData(6016, 3384)]
    [InlineData(7680, 4320)]
    public void LargeScreensGetAWindowLayerPlusHeadroom(int width, int height)
    {
        long layer = (long)width * height * BytesPerPixel;
        long budget = AvaloniaAppHost.GpuResourceCacheBytes((long)width * height);

        Assert.True(budget >= layer + AvaloniaAppHost.GpuResourceCacheHeadroomBytes);
        Assert.True(budget >= AvaloniaAppHost.MinimumGpuResourceCacheBytes);
    }

    [Fact]
    public void OptionsCarryTheBudgetAndExceedAvaloniasDefault()
    {
        long pixels = 7680L * 4320;
        SkiaOptions options = AvaloniaAppHost.BuildSkiaOptions(pixels);

        Assert.Equal(AvaloniaAppHost.GpuResourceCacheBytes(pixels), options.MaxGpuResourceSizeBytes);
        Assert.True(AvaloniaAppHost.BuildSkiaOptions(0).MaxGpuResourceSizeBytes > new SkiaOptions().MaxGpuResourceSizeBytes);
    }

    [Fact]
    public void TheLargestScreenIsReadOnWindows()
    {
        long pixels = DisplayMetrics.LargestScreenPixels();

        if (OperatingSystem.IsWindows())
            Assert.True(pixels > 0);
        else
            Assert.Equal(0, pixels);
    }
}
