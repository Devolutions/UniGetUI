using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Theme;

namespace UniGetUI.Tui.Views.Controls;

/// <summary>
/// The last line of the window: function keys (<c>F1 Help  F5 Reload …</c>, each key in a highlighted cell), then the
/// current notification inline (a severity badge and its text), and state badges such as FAKE DATA right-aligned.
/// Nothing is ever cut with "…": F1, F10 and Ctrl+Q always stay, the notification falls back from "title: message" to
/// the message, the title, then the badge alone, and the other keys are dropped whole, least important first.
/// </summary>
internal sealed class TuiFunctionBar : Border
{
    private const int Gap = 2;

    private readonly StackPanel _keys = new() { Orientation = Orientation.Horizontal, Spacing = Gap };
    private readonly StackPanel _badges = new() { Orientation = Orientation.Horizontal, Spacing = 1 };
    private readonly Border _notification;
    private readonly TextBlock _notificationBadge = new() { FontWeight = FontWeight.Bold, Foreground = TuiPalette.Background };
    private readonly TextBlock _notificationText = new() { Foreground = TuiPalette.Text };
    private IReadOnlyList<TuiKeyHint> _hints = [];
    private IReadOnlyList<string> _badgeTexts = [];
    private string _title = string.Empty;
    private string _message = string.Empty;
    private bool _notificationActive;

    public TuiFunctionBar()
    {
        Background = TuiPalette.TabBar;
        Padding = new Thickness(0, 0, 1, 0);
        ClipToBounds = true;

        var notificationRow = new StackPanel { Orientation = Orientation.Horizontal };
        notificationRow.Children.Add(_notificationBadge);
        notificationRow.Children.Add(_notificationText);
        _notification = new Border { IsVisible = false, Margin = new Thickness(Gap, 0, 0, 0), Child = notificationRow };

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_keys, Dock.Left);
        DockPanel.SetDock(_badges, Dock.Right);
        row.Children.Add(_keys);
        row.Children.Add(_badges);
        row.Children.Add(_notification);
        Child = row;
        SizeChanged += (_, _) => Layout();
    }

    public bool NotificationVisible => _notificationActive;

    /// <summary>The keys in display order; <see cref="TuiKeyHint.Priority"/> decides which are dropped first.</summary>
    public void SetKeys(IReadOnlyList<TuiKeyHint> hints)
    {
        if (_hints.SequenceEqual(hints)) return;
        _hints = hints;
        _keys.Children.Clear();
        foreach (TuiKeyHint hint in hints) _keys.Children.Add(KeyCell(hint));
        Layout();
    }

    /// <summary>State badges on the right, in the warning colour (e.g. FAKE DATA).</summary>
    public void SetBadges(IReadOnlyList<string> badges)
    {
        if (_badgeTexts.SequenceEqual(badges)) return;
        _badgeTexts = badges;
        _badges.Children.Clear();
        foreach (string text in badges)
            _badges.Children.Add(new TextBlock
            {
                Text = " " + text + " ",
                Background = TuiPalette.Warning,
                Foreground = TuiPalette.Background,
                FontWeight = FontWeight.Bold,
            });
        Layout();
    }

    public void ShowNotification(string badge, IBrush tint, IBrush strip, string title, string message)
    {
        _notificationBadge.Text = " " + badge + " ";
        _notificationBadge.Background = tint;
        _notification.Background = strip;
        // "Updates available!" + "13 packages…" reads "Updates available: 13 packages…".
        _title = title.Trim().TrimEnd('!', ':', '.').Trim();
        _message = message.Trim();
        _notificationActive = true;
        Layout();
    }

    public void HideNotification()
    {
        _notificationActive = false;
        _notification.IsVisible = false;
        Layout();
    }

    /// <summary>The notification texts to try, longest and most complete first.</summary>
    internal static IReadOnlyList<string> NotificationVariants(string title, string message)
    {
        var variants = new List<string>();
        if (title.Length > 0 && message.Length > 0) variants.Add(title + ": " + message);
        if (message.Length > 0) variants.Add(message);
        if (title.Length > 0) variants.Add(title);
        variants.Add(string.Empty);
        return variants.Distinct().ToList();
    }

    /// <summary>Keys at or below this priority (F1, F10, Ctrl+Q) are placed before the notification.</summary>
    internal const int EssentialPriority = 2;

    /// <summary>
    /// Which keys and which notification variant fit in <paramref name="width"/> columns, dropping the least important
    /// parts first: the essential keys are placed first, then the longest notification variant that fits, then the
    /// other keys by priority in what is left. Returns the visible key indices and the variant index (-1 when there is
    /// no notification or not even its badge fits).
    /// </summary>
    internal static (List<int> Keys, int Variant) Fit(IReadOnlyList<TuiKeyHint> keys, string? badge, IReadOnlyList<string> variants, double width)
    {
        int NotificationWidth(int variant) => Gap + badge!.Length + 2 + (variants[variant].Length > 0 ? variants[variant].Length + 2 : 0);

        var byPriority = Enumerable.Range(0, keys.Count).OrderBy(i => keys[i].Priority).ToList();
        var shown = new List<int>();
        double used = 0;

        void Place(IEnumerable<int> candidates)
        {
            foreach (int i in candidates)
            {
                double cost = CellWidth(keys[i]) + (shown.Count > 0 ? Gap : 0);
                if (used + cost > width) continue;
                used += cost;
                shown.Add(i);
            }
        }

        Place(byPriority.Where(i => keys[i].Priority <= EssentialPriority));
        int chosen = -1;
        if (badge is not null)
            for (int v = 0; v < variants.Count; v++)
                if (used + NotificationWidth(v) <= width)
                {
                    chosen = v;
                    used += NotificationWidth(v);
                    break;
                }

        Place(byPriority.Where(i => keys[i].Priority > EssentialPriority));
        shown.Sort();
        return (shown, chosen);
    }

    private static int CellWidth(TuiKeyHint hint) => hint.Key.Length + 2 + 1 + hint.Label.Length;

    private void Layout()
    {
        double width = Bounds.Width - Padding.Left - Padding.Right;
        if (width <= 0) return;
        double badges = _badgeTexts.Sum(b => b.Length + 2) + Math.Max(0, _badgeTexts.Count - 1) + (_badgeTexts.Count > 0 ? 1 : 0);
        IReadOnlyList<string> variants = NotificationVariants(_title, _message);
        string? badge = _notificationActive ? _notificationBadge.Text?.Trim() : null;
        (List<int> keys, int variant) = Fit(_hints, badge, variants, width - badges);

        for (int i = 0; i < _keys.Children.Count; i++) _keys.Children[i].IsVisible = keys.Contains(i);
        if (badge is null) return;
        _notification.IsVisible = variant >= 0;
        string text = variant < 0 ? string.Empty : variants[variant];
        _notificationText.Text = text.Length > 0 ? " " + text + " " : string.Empty;
    }

    /// <summary>A function-key cell: the key on the focus colour, then its label.</summary>
    private static Control KeyCell(TuiKeyHint hint)
    {
        var cell = new StackPanel { Orientation = Orientation.Horizontal };
        cell.Children.Add(new TextBlock
        {
            Text = " " + hint.Key + " ",
            Background = TuiPalette.Focus,
            Foreground = TuiPalette.FocusText,
            FontWeight = FontWeight.Bold,
        });
        cell.Children.Add(new TextBlock { Text = " " + hint.Label, Foreground = TuiPalette.TextMuted });
        return cell;
    }
}
