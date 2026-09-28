using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using UniGetUI.Core.SettingsEngine;

namespace UniGetUI.Tui.Theme;

/// <summary>
/// The active theme as live brushes. Controls share these brush instances; applying a theme changes
/// their colours in place (so the whole UI recolours without rebuilding anything) and overrides
/// Consolonia's theme resources, which its own control templates resolve dynamically.
/// </summary>
internal static class TuiPalette
{
    public static TuiTheme Current { get; private set; } = TuiThemes.Default;

    /// <summary>Raised after a theme is applied, for anything that caches colours outside these brushes.</summary>
    public static event Action? ThemeChanged;

    public static SolidColorBrush Background { get; } = new();

    public static SolidColorBrush Surface { get; } = new();

    public static SolidColorBrush Chrome { get; } = new();

    public static SolidColorBrush ChromeText { get; } = new();

    public static SolidColorBrush MenuBar { get; } = new();

    public static SolidColorBrush MenuText { get; } = new();

    public static SolidColorBrush TabBar { get; } = new();

    public static SolidColorBrush DropDown { get; } = new();

    public static SolidColorBrush Text { get; } = new();

    public static SolidColorBrush TextMuted { get; } = new();

    public static SolidColorBrush TextDim { get; } = new();

    public static SolidColorBrush Brand { get; } = new();

    public static SolidColorBrush Border { get; } = new();

    public static SolidColorBrush Frame { get; } = new();

    public static SolidColorBrush Divider { get; } = new();

    public static SolidColorBrush Focus { get; } = new();

    public static SolidColorBrush FocusText { get; } = new();

    public static SolidColorBrush Selection { get; } = new();

    public static SolidColorBrush SelectionText { get; } = new();

    public static SolidColorBrush Error { get; } = new();

    public static SolidColorBrush Success { get; } = new();

    public static SolidColorBrush NewVersion { get; } = new();

    public static SolidColorBrush Warning { get; } = new();

    public static SolidColorBrush AccessKey { get; } = new();

    /// <summary>Placeholder text in text boxes.</summary>
    public static SolidColorBrush Watermark { get; } = new();

    public static SolidColorBrush InfoBackground { get; } = new();

    public static SolidColorBrush SuccessBackground { get; } = new();

    public static SolidColorBrush WarningBackground { get; } = new();

    public static SolidColorBrush ErrorBackground { get; } = new();

    static TuiPalette() => SetBrushes(TuiThemes.Default);

    /// <summary>The saved theme (Settings), falling back to the default.</summary>
    public static TuiTheme Saved => TuiThemes.Find(Settings.GetValue(Settings.K.TuiTheme)) ?? TuiThemes.Default;

    /// <summary>Applies <paramref name="theme"/> and, when <paramref name="save"/>, remembers it.</summary>
    public static void Apply(TuiTheme theme, bool save = false)
    {
        Current = theme;
        SetBrushes(theme);
        if (Application.Current is { } app)
        {
            SetConsoloniaResources(app, theme);
            app.RequestedThemeVariant = theme.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        }

        if (save) Settings.SetValue(Settings.K.TuiTheme, theme.Id);
        ThemeChanged?.Invoke();
    }

    private static void SetBrushes(TuiTheme t)
    {
        Background.Color = t.Background;
        Surface.Color = t.Surface;
        Chrome.Color = t.Chrome;
        ChromeText.Color = t.ChromeText;
        MenuBar.Color = t.MenuBar;
        MenuText.Color = t.MenuText;
        TabBar.Color = t.TabBar;
        DropDown.Color = t.DropDown;
        Text.Color = t.Text;
        TextMuted.Color = t.TextMuted;
        TextDim.Color = t.TextDim;
        Brand.Color = t.Brand;
        Border.Color = t.Border;
        Frame.Color = t.Frame;
        Divider.Color = t.Divider;
        Focus.Color = t.Focus;
        FocusText.Color = t.FocusText;
        Selection.Color = t.Selection;
        SelectionText.Color = t.SelectionText;
        Error.Color = t.Error;
        Success.Color = t.Success;
        NewVersion.Color = t.NewVersion;
        Warning.Color = t.Warning;
        AccessKey.Color = t.AccessKey;
        Watermark.Color = t.Watermark;
        InfoBackground.Color = t.NotificationBackground(t.Brand);
        SuccessBackground.Color = t.NotificationBackground(t.Success);
        WarningBackground.Color = t.NotificationBackground(t.Warning);
        ErrorBackground.Color = t.NotificationBackground(t.Error);
    }

    /// <summary>
    /// Consolonia's Modern and TurboVision themes draw their controls from these keys (see
    /// Consolonia.Themes/Modern/ModernColors.axaml). Application resources win over the theme's own
    /// dictionaries, so overriding them restyles every stock control.
    /// </summary>
    private static void SetConsoloniaResources(Application app, TuiTheme t)
    {
        var r = app.Resources;
        r["ThemeForegroundBrush"] = new SolidColorBrush(t.Text);
        r["ThemeBackgroundBrush"] = new SolidColorBrush(t.Background);
        r["ThemeAlternativeBackgroundBrush"] = new SolidColorBrush(t.Surface);
        r["ThemeChooserBackgroundBrush"] = new SolidColorBrush(t.Selection);
        r["ThemeChooserForegroundBrush"] = new SolidColorBrush(t.SelectionText);
        r["ThemeActionBackgroundBrush"] = new SolidColorBrush(t.Focus);
        r["ThemeSelectionBackgroundBrush"] = new SolidColorBrush(t.EditSurface);
        r["ThemeSelectionForegroundBrush"] = new SolidColorBrush(t.EditText);
        r["ThemeHighlightForegroundBrush"] = new SolidColorBrush(t.AccessKey);
        r["ThemeHighlightForegroundBrush2"] = new SolidColorBrush(t.AccessKey);
        r["ThemeBorderBrush"] = new SolidColorBrush(t.Border);
        r["ThemeErrorBrush"] = new SolidColorBrush(t.Error);
        r["ThemeShadeBrush"] = new SolidColorBrush(t.IsLight ? Color.Parse("#7F767676") : Color.Parse("#7F000000"));
        r["ThemePseudoShadeBrush"] = new SolidColorBrush(t.TextDim);

        // The forked TextBox template binds its watermark to ThemeNoDisturbBrush (Consolonia's disabled
        // colour, a low-contrast grey by default), so the theme's watermark colour goes there.
        var watermark = new SolidColorBrush(t.Watermark);
        r["ThemeNoDisturbBrush"] = watermark;
        r["ThemeWatermarkBrush"] = watermark;
    }
}
