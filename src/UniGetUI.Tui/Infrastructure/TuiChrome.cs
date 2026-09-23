using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Consolonia.Controls;
using UniGetUI.Tui.Theme;

namespace UniGetUI.Tui.Infrastructure;

internal static class TuiChrome
{
    private static readonly IBrush DividerBrush = new SolidColorBrush(Color.Parse("#3A3A3A"));

    public static Control PageTitle(string marker, string title)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 1, Margin = new Thickness(0, 0, 0, 1),
        };
        row.Children.Add(new SymbolsControl
        {
            Text = marker, Foreground = DevolutionsPalette.BrandBrush, VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = DevolutionsPalette.BrandBrush,
            FontWeight = FontWeight.Bold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        return row;
    }

    public static Control Separator()
    {
        return new LineSeparator { Brush = DividerBrush, Margin = new Thickness(0, 0, 0, 1), };
    }
}
