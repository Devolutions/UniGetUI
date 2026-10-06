using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.Tui.Theme;

namespace UniGetUI.Tui.Views.Controls;

/// <summary>A compact labelled value drawn as a bracketed chip: <c>Sort [ Name ▲ ]</c>. Clicking it raises
/// <see cref="Activated"/> (the page opens the matching picker).</summary>
internal sealed class TuiChip : StackPanel
{
    private readonly TextBlock _label;
    private readonly TextBlock _value;

    public TuiChip(string label)
    {
        Orientation = Orientation.Horizontal;
        Background = Brushes.Transparent; // hit-testable across the whole chip, not just its text
        PointerPressed += (_, e) =>
        {
            if (Activated is null) return;
            e.Handled = true;
            Activated();
        };
        _label = new TextBlock { Text = label + " ", Foreground = TuiPalette.TextMuted };
        _value = new TextBlock { Foreground = TuiPalette.Text };
        Children.Add(_label);
        Children.Add(new TextBlock { Text = "[ ", Foreground = TuiPalette.TextDim });
        Children.Add(_value);
        Children.Add(new TextBlock { Text = " ]", Foreground = TuiPalette.TextDim });
    }

    public event Action? Activated;

    public string Value
    {
        get => _value.Text ?? string.Empty;
        set => _value.Text = value;
    }

    public bool ShowLabel
    {
        get => _label.IsVisible;
        set => _label.IsVisible = value;
    }

    private int Columns(bool withLabel) => (withLabel ? (_label.Text ?? string.Empty).Length : 0) + 4 + Value.Length;

    /// <summary>The columns a row of visible chips takes, with or without their labels.</summary>
    public static double WidthOf(StackPanel chips, bool withLabels)
    {
        var visible = chips.Children.OfType<TuiChip>().Where(c => c.IsVisible).ToList();
        return visible.Sum(c => c.Columns(withLabels)) + (chips.Spacing * Math.Max(0, visible.Count - 1)) + chips.Margin.Left;
    }
}
