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

    public static bool RequiresBundledFont(string familyName)
        => familyName.Contains(BundledFamily, StringComparison.Ordinal);

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

    private static string ResolveScriptFamily()
    {
        string language = CoreSettings.GetValue(CoreSettings.K.PreferredLanguage);
        if (language is "default" or "")
        {
            language = CultureInfo.CurrentUICulture.Name;
        }

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
