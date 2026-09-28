using UniGetUI.Tui.Infrastructure;

namespace UniGetUI.Tui.Tests;

public class TuiInputGuardTests
{
    [Fact]
    public void Sanitize_ReplacesLineBreaksAndTabs_WithSpaces()
    {
        string result = TuiInputGuard.Sanitize("winget\r\nsource\tlist", 100, out bool changed, out bool truncated);

        Assert.Equal("winget  source list", result);
        Assert.True(changed);
        Assert.False(truncated);
    }

    [Fact]
    public void Sanitize_DropsControlCharacters()
    {
        string result = TuiInputGuard.Sanitize("abc\u001B[31m", 100, out bool changed, out bool truncated);

        Assert.Equal("abc[31m", result);
        Assert.True(changed);
        Assert.False(truncated);
    }

    [Fact]
    public void Sanitize_CapsInputLength()
    {
        string result = TuiInputGuard.Sanitize("abcdef", 3, out bool changed, out bool truncated);

        Assert.Equal("abc", result);
        Assert.False(changed);
        Assert.True(truncated);
    }
}
