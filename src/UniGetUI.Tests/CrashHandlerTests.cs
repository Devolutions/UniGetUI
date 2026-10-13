using UniGetUI.Avalonia;

namespace UniGetUI.Tests;

public sealed class CrashHandlerTests
{
    [Theory]
    [InlineData("Unable to load DLL 'libHarfBuzzSharp' or one of its dependencies: The specified module could not be found.", true)]
    [InlineData("Unable to load DLL 'libSkiaSharp.dll' or one of its dependencies: The specified module could not be found.", true)]
    [InlineData("Unable to load DLL 'UIAutomationCore.dll' or one of its dependencies: Accesso negato.", false)]
    [InlineData("Unable to load DLL 'user32' or one of its dependencies: Access is denied.", false)]
    [InlineData("Unable to load a native library.", true)]
    public void IsMissingBundledLibrary_SkipsLibrariesShippedWithWindows(string message, bool expected)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(expected, CrashHandler.IsMissingBundledLibrary(new DllNotFoundException(message)));
    }

    [Fact]
    public void IsMissingBundledLibrary_IgnoresOtherExceptions()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.False(CrashHandler.IsMissingBundledLibrary(new FileNotFoundException("'libHarfBuzzSharp'")));
    }
}
