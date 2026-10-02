using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Consolonia.Controls;
using UniGetUI.Tui.Theme;

namespace UniGetUI.Tui.Views.Controls;

/// <summary>
/// A framed panel whose title sits in its top edge, with an optional summary right-aligned in the same edge:
/// <c>┌─ Title ────────── summary ─┐</c>. With <c>box: false</c> only the top edge is drawn, which makes it a
/// titled rule (the page title of the simpler pages).
/// </summary>
internal sealed class TuiPanel : Panel
{
    private readonly StackPanel _title;
    private readonly Border _summaryHost;

    /// <param name="content">The panel body (ignored for a rule).</param>
    /// <param name="box">False for a titled rule instead of a box.</param>
    public TuiPanel(Control? content = null, bool box = true)
    {
        // Consolonia draws a Border only as a full box, so the rule variant is a line separator instead.
        Control frame = box
            ? new Border
            {
                BorderThickness = new Thickness(1),
                BorderBrush = TuiPalette.Frame,
                Padding = new Thickness(1, 0, 1, 0),
                Child = content,
            }
            : new LineSeparator { Brush = TuiPalette.Frame, VerticalAlignment = VerticalAlignment.Top };

        // The edge texts paint the page background behind themselves, so they cut the frame line.
        _title = new StackPanel { Orientation = Orientation.Horizontal };
        var titleHost = new Border { Background = TuiPalette.Background, Padding = new Thickness(1, 0, 1, 0), Child = _title };
        _summaryHost = new Border
        {
            Background = TuiPalette.Background,
            Padding = new Thickness(1, 0, 1, 0),
            Margin = new Thickness(2, 0, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            IsVisible = false,
        };

        var edge = new DockPanel { LastChildFill = true, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(2, 0, 2, 0) };
        DockPanel.SetDock(titleHost, Dock.Left);
        edge.Children.Add(titleHost);
        edge.Children.Add(_summaryHost);

        Children.Add(frame);
        Children.Add(edge);
    }

    /// <summary>A panel titled with a single bold brand-coloured text.</summary>
    public TuiPanel(string title, Control? content = null, bool box = true) : this(content, box) => SetTitle(title);

    public void SetTitle(string title) => SetTitle(TitleText(title));

    /// <summary>Replaces the title with these parts (for example a name followed by a muted id).</summary>
    public void SetTitle(params Control[] parts)
    {
        _title.Children.Clear();
        foreach (Control part in parts) _title.Children.Add(part);
    }

    /// <summary>The control drawn right-aligned in the top edge (typically a muted <see cref="TextBlock"/>).</summary>
    public Control? Summary
    {
        get => _summaryHost.Child;
        set
        {
            _summaryHost.Child = value;
            _summaryHost.IsVisible = value is not null;
        }
    }

    public static TextBlock TitleText(string text) => new()
    {
        Text = text,
        Foreground = TuiPalette.Brand,
        FontWeight = FontWeight.Bold,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
}
