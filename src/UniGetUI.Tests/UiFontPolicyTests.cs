using Avalonia.Media;
using UniGetUI.Avalonia.Infrastructure;

namespace UniGetUI.Tests;

public sealed class UiFontPolicyTests
{
    [Fact]
    public void ResolveFontFallbacks_CoversTheScriptsSegoeUiLacks()
    {
        IReadOnlyList<FontFallback>? fallbacks = UiFontPolicy.ResolveFontFallbacks();

        Assert.NotNull(fallbacks);

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
        IReadOnlyList<FontFallback> fallbacks = UiFontPolicy.ResolveFontFallbacks()!;

        string[] families = [.. fallbacks
            .Where(f => f.UnicodeRange.IsInRange(codepoint))
            .Select(f => f.FontFamily.Name)];

        Assert.NotEmpty(families);
        Assert.Equal(families.Length, families.Distinct().Count());
    }

    [Fact]
    public void ResolveFontFallbacks_OffersEveryCjkFamilyForHan()
    {
        IReadOnlyList<FontFallback> fallbacks = UiFontPolicy.ResolveFontFallbacks()!;

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
        IReadOnlyList<FontFallback> fallbacks = UiFontPolicy.ResolveFontFallbacks()!;

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
        IReadOnlyList<FontFallback> fallbacks = UiFontPolicy.ResolveFontFallbacks()!;

        string[] cjkFamilies = [.. fallbacks
            .Where(f => f.UnicodeRange.IsInRange(0x4E2D))
            .Select(f => f.FontFamily.Name)];

        int index = Array.IndexOf(cjkFamilies, uiFamily);

        Assert.InRange(index, 0, cjkFamilies.Length - 2);
        Assert.Equal(alternative, cjkFamilies[index + 1]);
    }
}
