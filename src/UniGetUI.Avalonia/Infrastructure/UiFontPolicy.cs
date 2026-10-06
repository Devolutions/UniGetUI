using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using CoreSettings = global::UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class UiFontPolicy
{
    public const string FontFamilyEnvironmentVariable = "UNIGETUI_FONT_FAMILY";

    private const string FallbackFamily = "Segoe UI";

    private const string BundledFamily = "avares://Avalonia.Fonts.Inter/Assets#Inter";

    private const string MacOSFamily = ".AppleSystemUIFont, .SF NS, SF Pro Text, Helvetica Neue";

    // Segoe UI has no glyphs for these scripts, so the interface language picks the primary family
    // the way WinUI does instead of leaving the entire UI to per-glyph fallback.
    private static readonly (string LanguagePrefix, string Family)[] ScriptFamilies =
    [
        ("zh_hant", "Microsoft JhengHei UI"),
        ("zh_tw", "Microsoft JhengHei UI"),
        ("zh_hk", "Microsoft JhengHei UI"),
        ("zh_mo", "Microsoft JhengHei UI"),
        ("zh", "Microsoft YaHei UI"),
        ("ja", "Yu Gothic UI"),
        ("ko", "Malgun Gothic"),
        ("th", "Leelawadee UI"),
        ("bn", "Nirmala UI"),
        ("gu", "Nirmala UI"),
        ("hi", "Nirmala UI"),
        ("kn", "Nirmala UI"),
        ("mr", "Nirmala UI"),
        ("sa", "Nirmala UI"),
        ("si", "Nirmala UI"),
        ("ta", "Nirmala UI"),
    ];

    private static readonly string[][] CjkFamilyGroups =
    [
        ["Microsoft YaHei UI", "Microsoft YaHei", "SimSun"],
        ["Microsoft JhengHei UI", "Microsoft JhengHei"],
        ["Yu Gothic UI", "Yu Gothic"],
        ["Malgun Gothic"],
    ];

    private static readonly UnicodeRangeSegment[] CjkSegments =
    [
        new(0x1100, 0x11FF),
        new(0x2E80, 0x303F),
        new(0x3040, 0x30FF),
        new(0x3100, 0x312F),
        new(0x3130, 0x318F),
        new(0x3190, 0x4DBF),
        new(0x4E00, 0x9FFF),
        new(0xA960, 0xA97F),
        new(0xAC00, 0xD7FF),
        new(0xF900, 0xFAFF),
        new(0xFE30, 0xFE4F),
        new(0xFF00, 0xFFEF),
        new(0x20000, 0x2FA1F),
    ];

    private static readonly (UnicodeRangeSegment[] Segments, string[] Families)[] ScriptFallbacks =
    [
        ([new(0x0900, 0x0DFF)], ["Nirmala UI"]),
        ([new(0x0E00, 0x0EFF)], ["Leelawadee UI"]),
    ];

    public static bool RequiresBundledFont(string familyName)
        => familyName.Contains(BundledFamily, StringComparison.Ordinal);

    /// <summary>
    /// Resolves the per-codepoint font fallbacks to register with Avalonia, or <c>null</c> to keep
    /// Avalonia's own fallback lookup.
    /// </summary>
    public static IReadOnlyList<FontFallback>? ResolveFontFallbacks()
    {
        if (Design.IsDesignMode || !OperatingSystem.IsWindows())
        {
            return null;
        }

        return BuildFontFallbacks(ResolveInterfaceLanguage(), CultureInfo.CurrentUICulture.Name);
    }

    /// <summary>
    /// Builds the fallback table for an explicit pair of languages, so the regional ordering can be
    /// exercised without the process' own settings and culture.
    /// </summary>
    public static IReadOnlyList<FontFallback> BuildFontFallbacks(string interfaceLanguage, string systemLanguage)
    {
        string scriptFamily = ResolveCjkFamily(interfaceLanguage, systemLanguage);
        IEnumerable<string> cjkFamilies = CjkFamilyGroups
            .OrderByDescending(group => group[0] == scriptFamily)
            .SelectMany(group => group);

        var fallbacks = new List<FontFallback>();

        foreach ((UnicodeRangeSegment[] segments, string[] families) in ScriptFallbacks)
        {
            AddFallbacks(fallbacks, segments, families);
        }

        AddFallbacks(fallbacks, CjkSegments, cjkFamilies);

        return fallbacks;

        static void AddFallbacks(List<FontFallback> target, UnicodeRangeSegment[] segments, IEnumerable<string> families)
        {
            var range = new UnicodeRange(segments);

            foreach (string family in families)
            {
                target.Add(new FontFallback { FontFamily = new FontFamily(family), UnicodeRange = range });
            }
        }
    }

    /// <summary>
    /// Resolves the family chain to pin as Avalonia's default, or <c>null</c> to keep the platform
    /// default. Avalonia derives that default from the Win32 system message font, which is
    /// locale-dependent and is rewritten by tools such as noMeiryoUI, so a font that only claims to
    /// cover a script renders tofu boxes that per-glyph fallback never repairs (#5264).
    /// </summary>
    public static string? ResolveDefaultFamilyName()
    {
        // Design mode is excluded because reading a setting there would migrate the user's real
        // configuration directory from the previewer process.
        if (Design.IsDesignMode)
        {
            return null;
        }

        if (!OperatingSystem.IsMacOS() && CoreSettings.Get(CoreSettings.K.UseSystemUIFont))
        {
            return null;
        }

        string? overrideFamily = Environment.GetEnvironmentVariable(FontFamilyEnvironmentVariable)?.Trim();

        // A "$Default" entry makes default-family resolution recurse into itself and overflow the
        // stack, which no handler can report, so such an override is discarded.
        if (overrideFamily is null || overrideFamily.Length == 0 ||
            overrideFamily.Contains(FontFamily.DefaultFontFamilyName, StringComparison.Ordinal))
        {
            overrideFamily = null;
        }

        // The fallback is kept as the tail of every chain so an unavailable family degrades to it
        // instead of leaving the app with no resolvable font at all.
        if (OperatingSystem.IsMacOS())
        {
            return overrideFamily is null ? MacOSFamily : $"{overrideFamily}, {MacOSFamily}";
        }

        if (!OperatingSystem.IsWindows())
        {
            return overrideFamily is null ? BundledFamily : $"{overrideFamily}, {BundledFamily}";
        }

        string scriptFamily = ResolveScriptFamily();
        string chain = scriptFamily == FallbackFamily ? FallbackFamily : $"{scriptFamily}, {FallbackFamily}";

        return overrideFamily is null ? chain : $"{overrideFamily}, {chain}";
    }

    /// <summary>
    /// Resolves the family whose group leads the CJK fallback order. Han glyph forms differ between
    /// the regions that share the block, so the interface language chooses, and the operating
    /// system's own language chooses when the interface runs in a language that shares no block.
    /// </summary>
    private static string ResolveCjkFamily(string interfaceLanguage, string systemLanguage)
    {
        string family = ResolveScriptFamily(interfaceLanguage);
        if (CjkFamilyGroups.Any(group => group[0] == family))
        {
            return family;
        }

        return ResolveScriptFamily(systemLanguage);
    }

    private static string ResolveScriptFamily()
        => ResolveScriptFamily(ResolveInterfaceLanguage());

    private static string ResolveInterfaceLanguage()
    {
        string language = CoreSettings.GetValue(CoreSettings.K.PreferredLanguage);

        return language is "default" or "" ? CultureInfo.CurrentUICulture.Name : language;
    }

    private static string ResolveScriptFamily(string language)
    {
        language = language.Replace('-', '_').ToLowerInvariant();

        foreach ((string prefix, string family) in ScriptFamilies)
        {
            // Prefixes are ordered most specific first, and are matched on a separator boundary so
            // "sain" cannot select the Sanskrit family.
            if (language == prefix || language.StartsWith($"{prefix}_", StringComparison.Ordinal))
            {
                return family;
            }
        }

        return FallbackFamily;
    }
}
