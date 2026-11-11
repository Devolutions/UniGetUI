using Avalonia;
using UniGetUI.Avalonia.Infrastructure;

namespace UniGetUI.Tests;

public sealed class SkiaOptionsTests
{
    private const long BytesPerPixel = 4;

    [Theory]
    [InlineData(3840, 2160)]
    [InlineData(3456, 2168)]
    [InlineData(5120, 2880)]
    public void GpuCacheHoldsAFullWindowLayerWithRoomToSpare(int width, int height)
    {
        SkiaOptions options = AvaloniaAppHost.BuildSkiaOptions();
        long layer = width * height * BytesPerPixel;

        Assert.NotNull(options.MaxGpuResourceSizeBytes);
        Assert.True(options.MaxGpuResourceSizeBytes >= 2 * layer);
    }

    [Fact]
    public void GpuCacheIsLargerThanAvaloniasDefault()
    {
        Assert.True(AvaloniaAppHost.BuildSkiaOptions().MaxGpuResourceSizeBytes > new SkiaOptions().MaxGpuResourceSizeBytes);
    }
}
