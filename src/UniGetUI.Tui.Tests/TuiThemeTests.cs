using Avalonia.Media;
using NUnit.Framework;
using UniGetUI.Tui.Theme;
using NAssert = NUnit.Framework.Assert;

namespace UniGetUI.Tui.Tests;

[TestFixture]
internal sealed class TuiThemeTests
{
    private static IEnumerable<TuiTheme> Themes() => TuiThemes.All;

    [Test]
    public void IdsAndNamesAreUniqueAndFindable()
    {
        NAssert.That(TuiThemes.All.Select(t => t.Id), Is.Unique);
        NAssert.That(TuiThemes.All.Select(t => t.Name), Is.Unique);
        foreach (TuiTheme theme in TuiThemes.All)
        {
            NAssert.That(TuiThemes.Find(theme.Id), Is.SameAs(theme));
            NAssert.That(TuiThemes.Find(theme.Name.ToUpperInvariant()), Is.SameAs(theme));
        }

        NAssert.That(TuiThemes.Find("no-such-theme"), Is.Null);
        NAssert.That(TuiThemes.Default.Name, Is.EqualTo("Devolutions - Graphite"));
    }

    [Test]
    public void TheDevolutionsThemesAreAllThere()
        => NAssert.That(TuiThemes.All.Where(t => t.Name.StartsWith("Devolutions - ", StringComparison.Ordinal)).Select(t => t.Name),
            Is.EquivalentTo(new[]
            {
                "Devolutions - Graphite", "Devolutions - Black", "Devolutions - Light", "Devolutions - Dark Blue", "Devolutions - Blue",
                "Devolutions - Gray", "Devolutions - High contrast",
            }));

    /// <summary>Every text colour the UI draws must be readable on the background it is drawn on.</summary>
    [TestCaseSource(nameof(Themes))]
    public void TextIsReadableOnItsBackground(TuiTheme t)
    {
        var failures = new List<string>();

        void Check(string what, Color fg, Color bg, double min)
        {
            double ratio = Contrast(fg, bg);
            if (ratio < min) failures.Add($"{what}: {ratio:0.00}:1 < {min}:1");
        }

        Check("Text on Background", t.Text, t.Background, 7);
        Check("Text on Surface", t.Text, t.Surface, 4.5);
        Check("TextMuted on Background", t.TextMuted, t.Background, 4.5);
        Check("Brand on Background", t.Brand, t.Background, 4.5);
        Check("Brand on Surface", t.Brand, t.Surface, 3);
        Check("Error on Background", t.Error, t.Background, 3);
        Check("Success on Background", t.Success, t.Background, 3);
        Check("Warning on Background", t.Warning, t.Background, 3);
        Check("ChromeText on Chrome", t.ChromeText, t.Chrome, 4.5);
        Check("MenuText on MenuBar", t.MenuText, t.MenuBar, 4.5);
        Check("MenuText on DropDown", t.MenuText, t.DropDown, 4.5);
        Check("FocusText on Focus", t.FocusText, t.Focus, 4.5);
        Check("Text on Focus (Consolonia pairing)", t.Text, t.Focus, 3);
        Check("SelectionText on Selection", t.SelectionText, t.Selection, 4.5);
        Check("EditText on EditSurface", t.EditText, t.EditSurface, 4.5);
        Check("Watermark on EditSurface", t.Watermark, t.EditSurface, 3);
        Check("TextMuted on TabBar", t.TextMuted, t.TabBar, 4.5);
        Check("AccessKey on MenuBar", t.AccessKey, t.MenuBar, 3);
        Check("AccessKey on DropDown", t.AccessKey, t.DropDown, 3);
        // Status bar key caps (page keys on Brand, global keys on TextMuted) and notification badges.
        Check("Background on Brand (key cap, info badge)", t.Background, t.Brand, 4.5);
        Check("Background on TextMuted (key cap)", t.Background, t.TextMuted, 4.5);
        Check("Background on Success (badge)", t.Background, t.Success, 3);
        Check("Background on Warning (badge)", t.Background, t.Warning, 3);
        Check("Background on Error (badge)", t.Background, t.Error, 3);
        foreach ((string name, Color tint) in new[] { ("info", t.Brand), ("success", t.Success), ("warning", t.Warning), ("error", t.Error) })
            Check($"Text on the {name} notification strip", t.Text, t.NotificationBackground(tint), 4.5);

        NAssert.That(failures, Is.Empty, $"{t.Name}:\n  " + string.Join("\n  ", failures));
    }

    /// <summary>WCAG 2 contrast ratio.</summary>
    private static double Contrast(Color a, Color b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }
}
