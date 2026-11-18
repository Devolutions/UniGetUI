using UniGetUI.Avalonia.Infrastructure;

namespace UniGetUI.Tests;

public class NativePickerTrackerTests
{
    [Fact]
    public async Task ReportsAPickerOpenUntilItCompletes()
    {
        var picker = new TaskCompletionSource<string?>();

        Task<string?> tracked = NativePickerTracker.Track(picker.Task);
        Assert.True(NativePickerTracker.IsOpen);

        picker.SetResult("chosen");

        Assert.Equal("chosen", await tracked);
        Assert.False(NativePickerTracker.IsOpen);
    }

    [Fact]
    public async Task StopsReportingAPickerThatFails()
    {
        var picker = new TaskCompletionSource<string?>();

        Task<string?> tracked = NativePickerTracker.Track(picker.Task);
        picker.SetException(new InvalidOperationException());

        await Assert.ThrowsAsync<InvalidOperationException>(() => tracked);
        Assert.False(NativePickerTracker.IsOpen);
    }
}
