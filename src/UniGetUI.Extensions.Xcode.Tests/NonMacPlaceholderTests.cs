#if !UNIGETUI_XCODE_EXTENSION
namespace UniGetUI.Extensions.Xcode.Tests;

public sealed class NonMacPlaceholderTests
{
    [Fact]
    public void XcodeExtensionIsExcludedFromNonMacBuilds()
        => Assert.False(false);
}
#endif
