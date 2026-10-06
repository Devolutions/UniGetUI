using Avalonia.Media;
using UniGetUI.Avalonia.Infrastructure;

namespace UniGetUI.Tests;

public sealed class UiFontPolicyTests
{
    [Fact]
    public void ResolveFontFallbacks_CoversTheScriptsSegoeUiLacks()
    {
        IReadOnlyList<FontFallback> fallbacks = UiFontPolicy.BuildFontFallbacks("zh_CN", "zh-CN");

        int[] covered = [0x3042, 0x4E2D, 0x5DF2, 0xAC00, 0xFF21, 0x20000, 0x0915, 0x0E01];
        foreach (int codepoint in covered)
        {
            Assert.Contains(fallbacks, f => f.UnicodeRange.IsInRange(codepoint));
        }

        int[] notCovered = [0x0041, 0x00E9, 0x0401, 0x1F600];
        foreach (int codepoint in notCovered)
        {
            Assert.DoesNotContain(fallbacks, f => f.UnicodeRange.IsInRange(codepoint));
        }
    }

    [Theory]
    [InlineData(0x4E2D)]
    [InlineData(0x3042)]
    [InlineData(0xAC00)]
    [InlineData(0x1100)]
    [InlineData(0x3105)]
    [InlineData(0xFF21)]
    [InlineData(0x0915)]
    [InlineData(0x0E01)]
    public void ResolveFontFallbacks_NeverListsAFamilyTwiceForOneCodepoint(int codepoint)
    {
        IReadOnlyList<FontFallback> fallbacks = UiFontPolicy.BuildFontFallbacks("zh_CN", "zh-CN");

        string[] families = [.. fallbacks
            .Where(f => f.UnicodeRange.IsInRange(codepoint))
            .Select(f => f.FontFamily.Name)];

        Assert.NotEmpty(families);
        Assert.Equal(families.Length, families.Distinct().Count());
    }

    [Fact]
    public void ResolveFontFallbacks_OffersEveryCjkFamilyForHan()
    {
        IReadOnlyList<FontFallback> fallbacks = UiFontPolicy.BuildFontFallbacks("zh_CN", "zh-CN");

        string[] cjkFamilies = [.. fallbacks
            .Where(f => f.UnicodeRange.IsInRange(0x4E2D))
            .Select(f => f.FontFamily.Name)];

        Assert.Contains("Microsoft YaHei UI", cjkFamilies);
        Assert.Contains("Microsoft JhengHei UI", cjkFamilies);
        Assert.Contains("Yu Gothic UI", cjkFamilies);
        Assert.Contains("Malgun Gothic", cjkFamilies);
    }

    [Theory]
    [InlineData(0x3042, 0x4E2D)]
    [InlineData(0xAC00, 0x4E2D)]
    [InlineData(0x3105, 0x4E2D)]
    public void ResolveFontFallbacks_ResolvesCjkBlocksThroughOneOrderedChain(int codepoint, int han)
    {
        IReadOnlyList<FontFallback> fallbacks = UiFontPolicy.BuildFontFallbacks("zh_CN", "zh-CN");

        string[] forCodepoint = [.. fallbacks
            .Where(f => f.UnicodeRange.IsInRange(codepoint))
            .Select(f => f.FontFamily.Name)];

        string[] forHan = [.. fallbacks
            .Where(f => f.UnicodeRange.IsInRange(han))
            .Select(f => f.FontFamily.Name)];

        Assert.Equal(forHan, forCodepoint);
    }

    [Theory]
    [InlineData("Microsoft YaHei UI", "Microsoft YaHei")]
    [InlineData("Microsoft JhengHei UI", "Microsoft JhengHei")]
    [InlineData("Yu Gothic UI", "Yu Gothic")]
    public void ResolveFontFallbacks_BacksEveryUiFamilyWithItsNonUiVariant(string uiFamily, string alternative)
    {
        IReadOnlyList<FontFallback> fallbacks = UiFontPolicy.BuildFontFallbacks("zh_CN", "zh-CN");

        string[] cjkFamilies = [.. fallbacks
            .Where(f => f.UnicodeRange.IsInRange(0x4E2D))
            .Select(f => f.FontFamily.Name)];

        int index = Array.IndexOf(cjkFamilies, uiFamily);

        Assert.InRange(index, 0, cjkFamilies.Length - 2);
        Assert.Equal(alternative, cjkFamilies[index + 1]);
    }

    [Theory]
    [InlineData("zh_CN", "en-US", "Microsoft YaHei UI")]
    [InlineData("zh_TW", "en-US", "Microsoft JhengHei UI")]
    [InlineData("ja", "en-US", "Yu Gothic UI")]
    [InlineData("ko", "en-US", "Malgun Gothic")]
    [InlineData("en", "ja-JP", "Yu Gothic UI")]
    [InlineData("en", "ko-KR", "Malgun Gothic")]
    [InlineData("en", "zh-TW", "Microsoft JhengHei UI")]
    [InlineData("en", "zh-CN", "Microsoft YaHei UI")]
    [InlineData("en", "en-US", "Microsoft YaHei UI")]
    public void BuildFontFallbacks_LeadsHanWithTheRegionalFamily(string interfaceLanguage, string systemLanguage, string expected)
    {
        IReadOnlyList<FontFallback> fallbacks = UiFontPolicy.BuildFontFallbacks(interfaceLanguage, systemLanguage);

        string[] cjkFamilies = [.. fallbacks
            .Where(f => f.UnicodeRange.IsInRange(0x4E2D))
            .Select(f => f.FontFamily.Name)];

        Assert.Equal(expected, cjkFamilies[0]);
    }

    [Theory]
    [InlineData("ja", "en-US")]
    [InlineData("en", "ko-KR")]
    public void BuildFontFallbacks_LeadsEveryCjkBlockWithTheSameFamily(string interfaceLanguage, string systemLanguage)
    {
        IReadOnlyList<FontFallback> fallbacks = UiFontPolicy.BuildFontFallbacks(interfaceLanguage, systemLanguage);

        string[] leaders = [.. new[] { 0x4E2D, 0x3042, 0xAC00, 0x3105, 0xFF21 }
            .Select(codepoint => fallbacks.First(f => f.UnicodeRange.IsInRange(codepoint)).FontFamily.Name)];

        Assert.Single(leaders.Distinct());
    }

    [Theory]
    [InlineData(0x20000, "SimSun-ExtB")]
    [InlineData(0x2A700, "SimSun-ExtB")]
    [InlineData(0x20000, "MingLiU-ExtB")]
    public void BuildFontFallbacks_BacksSupplementaryIdeographsWithAnExtBFamily(int codepoint, string family)
    {
        IReadOnlyList<FontFallback> fallbacks = UiFontPolicy.BuildFontFallbacks("zh_CN", "zh-CN");

        string[] families = [.. fallbacks
            .Where(f => f.UnicodeRange.IsInRange(codepoint))
            .Select(f => f.FontFamily.Name)];

        Assert.Contains(family, families);
    }

    [Fact]
    public void BuildFontManagerOptions_CarriesTheFallbackTable()
    {
        FontManagerOptions options = AvaloniaAppHost.BuildFontManagerOptions();

        Assert.NotNull(options.FontFallbacks);
        Assert.NotEmpty(options.FontFallbacks);
        Assert.Contains(options.FontFallbacks, f => f.UnicodeRange.IsInRange(0x4E2D));
    }
}
