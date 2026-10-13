using UniGetUI.Avalonia;

namespace UniGetUI.Tests;

public sealed class CrashHandlerTests
{
    [Theory]
    [InlineData("Unable to load DLL 'libHarfBuzzSharp' or one of its dependencies: The specified module could not be found.", true)]
    [InlineData("Unable to load DLL 'libSkiaSharp.dll' or one of its dependencies: The specified module could not be found.", true)]
    [InlineData("Unable to load DLL 'av_libglesv2.dll' or one of its dependencies: The specified module could not be found.", true)]
    [InlineData("Unable to load DLL 'UIAutomationCore.dll' or one of its dependencies: Accesso negato.", false)]
    [InlineData("Unable to load DLL 'UIAutomationCore.dll' or one of its dependencies: The specified module could not be found.", false)]
    [InlineData("Unable to load DLL 'api-ms-win-core-winrt-string-l1-1-0.dll' or one of its dependencies: The specified module could not be found.", false)]
    [InlineData("Unable to load DLL 'user32' or one of its dependencies: Access is denied.", false)]
    [InlineData("Unable to load a native library.", false)]
    public void IsMissingBundledLibrary_MatchesOnlyLibrariesUniGetUIShips(string message, bool expected)
    {
        Assert.Equal(expected, CrashHandler.IsMissingBundledLibrary(new DllNotFoundException(message), []));
    }

    [Theory]
    [InlineData("getfilesiginforedist.dll", true)]
    [InlineData("GETFILESIGINFOREDIST", true)]
    [InlineData("UIAutomationCore.dll", false)]
    public void IsMissingBundledLibrary_UsesTheIntegrityTreeListing(string library, bool expected)
    {
        string[] shipped = ["Assets/Utilities/getfilesiginforedist.dll", "UniGetUI.exe"];
        var exception = new DllNotFoundException($"Unable to load DLL '{library}' or one of its dependencies.");

        Assert.Equal(expected, CrashHandler.IsMissingBundledLibrary(exception, shipped));
    }

    [Fact]
    public void IsMissingBundledLibrary_IgnoresOtherExceptions()
    {
        Assert.False(CrashHandler.IsMissingBundledLibrary(new FileNotFoundException("'libHarfBuzzSharp'"), []));
    }
}
