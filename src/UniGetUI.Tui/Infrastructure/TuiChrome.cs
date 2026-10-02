using Avalonia;
using Avalonia.Controls;
using UniGetUI.Core.Tools;
using UniGetUI.Tui.Views.Controls;

namespace UniGetUI.Tui.Infrastructure;

internal static class TuiChrome
{
    /// <summary>
    /// The page title as a titled rule (<c>── Title ─────── summary ──</c>), with a blank row above it so it does not
    /// sit against the page tabs. <paramref name="summary"/>, when given, is drawn right-aligned in the rule.
    /// </summary>
    public static TuiPanel PageTitle(string title, Control? summary = null)
        => new(title, box: false) { Summary = summary, Margin = new Thickness(0, 1, 0, 1) };

    /// <summary>Key hints with translated labels, most important first, for <see cref="ITuiPage.KeyHints"/>.</summary>
    public static IReadOnlyList<TuiKeyHint> Hints(params (string Key, string Label)[] hints)
        => hints.Select((h, i) => new TuiKeyHint(h.Key, CoreTools.Translate(h.Label), i)).ToList();
}
