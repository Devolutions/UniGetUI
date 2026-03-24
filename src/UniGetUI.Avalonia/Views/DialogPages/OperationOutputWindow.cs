using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Views;

/// <summary>
/// Simple window that shows the full output log of a completed or failed operation.
/// Created in code so no AXAML file is required.
/// </summary>
public sealed class OperationOutputWindow : Window
{
    public OperationOutputWindow(AbstractOperation operation)
    {
        Title = operation.Metadata.Title;
        Width = 700;
        Height = 500;
        MinWidth = 400;
        MinHeight = 300;
        Background = (Application.Current?.FindResource("AppDialogBackground") as IBrush) ?? new SolidColorBrush(Color.Parse("#1e2025"));

        var lines = string.Join("\n", operation.GetOutput().Select(x => x.Item1));

        var textBox = new TextBox
        {
            Text = lines,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono,Consolas,Menlo,monospace"),
            FontSize = 12,
            Foreground = (Application.Current?.FindResource("SystemControlForegroundBaseHighBrush") as IBrush) ?? new SolidColorBrush(Color.Parse("#d4d4d4")),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        var scroll = new ScrollViewer
        {
            Content = textBox,
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Margin = new Thickness(8),
        };

        Content = scroll;

        // Scroll to bottom once shown
        Opened += (_, _) => scroll.ScrollToEnd();
    }
}
