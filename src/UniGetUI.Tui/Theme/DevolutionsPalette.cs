using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace UniGetUI.Tui.Theme;

/// <summary>
/// Devolutions brand palette for the TUI, layered on top of Consolonia's Modern (RGB) dark theme.
///
/// Consolonia themes expose their accent through a small set of named brushes (defined per
/// Light/Dark variant in Consolonia.Themes/Modern/ModernColors.axaml). Rather than fork the
/// Consolonia theme XAML to brand it, we override just those accent brushes at the Application
/// resource level — Application resources take precedence over a Styles' theme dictionaries when a
/// control resolves a {DynamicResource}, so every selection/focus/chooser highlight picks up the
/// Devolutions blue while the rest of the Modern theme is reused as-is.
/// </summary>
internal static class DevolutionsPalette
{
    // The brand blue serves two visually distinct roles that have opposite contrast requirements:
    // as TEXT it sits on the dark Modern background, and as a SURFACE it carries white text. A single
    // hue cannot clear WCAG AAA (7:1) in both directions, so the palette splits the roles. All ratios
    // below are computed against the Modern dark background (#2D2D30, L=0.0263) / white (L=1.0) using
    // the WCAG relative-luminance formula. (Note: the scout report's §1.5 luminance table was off —
    // its proposed #5B9BF8/#1A5DBF/#1558C0 actually compute to 4.9–6.6:1, below AAA — so the values
    // here are the corrected, genuinely ≥7:1 equivalents that keep the light-blue brand language.)

    /// <summary>Brand blue for TEXT (page titles, column headers, labels, detail keys). #90BEFF →
    /// 7.19:1 on #2D2D30 (AAA). A light brand-blue tint — the darkest blue that still clears 7:1 on
    /// this background is essentially this light, since blue contributes little luminance.</summary>
    public static readonly Color BrandTextColor = Color.Parse("#90BEFF");

    /// <summary>Brand blue as a SURFACE behind white chrome text (title bar, menu bar). White on this
    /// is 8.56:1 (AAA). Shared with the selection/chooser highlight so the brand reads consistently.</summary>
    public static readonly Color BrandSurfaceColor = Color.Parse("#15489E");

    /// <summary>Selection/chooser highlight background (selected list/sidebar items). White on this is
    /// 8.56:1 (AAA).</summary>
    public static readonly Color SelectionBackgroundColor = Color.Parse("#15489E");

    /// <summary>Forced/focused highlight background (focused items, menu-bar focus, TurboVision-style
    /// buttons). Slightly brighter than the selection blue for a focus cue; white on this is
    /// 7.49:1 (AAA).</summary>
    public static readonly Color ActionBackgroundColor = Color.Parse("#1450B0");

    /// <summary>Muted dark navy behind edit controls (textboxes) when selected. White text on this is
    /// 16.2:1; it is also the background the watermark must contrast against.</summary>
    public static readonly Color EditSurfaceColor = Color.Parse("#0C2040");

    /// <summary>Watermark / placeholder text color. Equal to the brand text blue so every consumer of
    /// the overridden ThemeNoDisturbBrush clears AAA: on the edit surface #0C2040 it is &gt;10:1, and
    /// on the dark background #2D2D30 (disabled text) it is 7.19:1.</summary>
    public static readonly Color WatermarkColor = Color.Parse("#90BEFF");

    /// <summary>Semantic ERROR text (log Error severity, operation error output lines). #FFA8A8 →
    /// 7.46:1 on #2D2D30 (AAA). A light red tint: mid/saturated reds (e.g. the old #FF6B6B = 4.95:1)
    /// contribute too little luminance to clear 7:1 on this dark background. Use for TEXT only —
    /// the original saturated red is fine as a non-text fill/border.</summary>
    public static readonly Color ErrorTextColor = Color.Parse("#FFA8A8");

    /// <summary>Semantic SUCCESS / ready text (log Success severity, "ready" manager status). #8EEAA1 →
    /// 9.44:1 on #2D2D30 (AAA). Replaces the old #3FB950 (5.40:1) wherever it was rendered as TEXT.</summary>
    public static readonly Color SuccessTextColor = Color.Parse("#8EEAA1");

    public static readonly Color OnBrand = Colors.White;

    /// <summary>Applies the Devolutions accent overrides to the running application.</summary>
    public static void ApplyAccents(Application app)
    {
        var resources = app.Resources;
        resources["ThemeChooserBackgroundBrush"] = new SolidColorBrush(SelectionBackgroundColor);
        resources["ThemeChooserForegroundBrush"] = new SolidColorBrush(OnBrand);
        resources["ThemeActionBackgroundBrush"] = new SolidColorBrush(ActionBackgroundColor);
        resources["ThemeSelectionBackgroundBrush"] = new SolidColorBrush(EditSurfaceColor);
        resources["ThemeSelectionForegroundBrush"] = new SolidColorBrush(OnBrand);

        // The forked TextBox template binds both its inline and floating watermark Foreground to
        // ThemeNoDisturbBrush (see Consolonia TextBox.axaml). The base theme value is a low-contrast
        // #808080 (≈3.1:1 on the edit surface — fails AAA). Override it so placeholders render in a
        // readable, brand-tinted #90BEFF (>10:1 on the navy edit surface; 7.19:1 even when the same
        // brush styles disabled text on the dark background). The same brush is also exposed under a
        // dedicated ThemeWatermarkBrush key for clarity and any future direct consumers; the fork
        // itself keeps falling back to ThemeNoDisturbBrush so non-UniGetUI consumers stay consistent.
        var watermark = new SolidColorBrush(WatermarkColor);
        resources["ThemeNoDisturbBrush"] = watermark;
        resources["ThemeWatermarkBrush"] = watermark;
    }

    /// <summary>Brand blue brush for TEXT in code-built controls (titles, headers, labels).</summary>
    public static IBrush BrandBrush { get; } = new SolidColorBrush(BrandTextColor);

    /// <summary>Brand blue brush for chrome SURFACES that carry white text (title/menu bars).</summary>
    public static IBrush BrandSurfaceBrush { get; } = new SolidColorBrush(BrandSurfaceColor);

    /// <summary>Semantic error-text brush (#FFA8A8, 7.46:1 on the dark background). TEXT use only.</summary>
    public static IBrush ErrorTextBrush { get; } = new SolidColorBrush(ErrorTextColor);

    /// <summary>Semantic success/ready-text brush (#8EEAA1, 9.44:1 on the dark background). TEXT use only.</summary>
    public static IBrush SuccessTextBrush { get; } = new SolidColorBrush(SuccessTextColor);

    public static IBrush OnBrandBrush { get; } = new SolidColorBrush(OnBrand);
}
