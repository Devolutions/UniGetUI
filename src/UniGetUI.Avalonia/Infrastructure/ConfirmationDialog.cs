using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.Avalonia.Views;
using UniGetUI.Avalonia.Views.DialogPages;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>A minimal yes/no confirmation dialog used for destructive history actions.</summary>
internal static class ConfirmationDialog
{
    public static async Task<bool> ShowAsync(string message)
    {
        if (MainWindow.Instance is not { } owner)
            return true;

        bool confirmed = false;
        var dialog = new ImmersiveDialog
        {
            MaxWidth = 548,
            MinWidth = 320,
            MinHeight = 136,
            Title = CoreTools.Translate("Are you sure?"),
            Background = Application.Current?.FindResource("AppDialogBackground") as IBrush,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };

        var noButton = new Button
        {
            Content = CoreTools.Translate("No"),
            MinWidth = 0,
            Height = 32,
            Padding = new Thickness(11, 5, 11, 6),
            CornerRadius = new CornerRadius(4),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        noButton.Click += (_, _) => dialog.Close();

        var yesButton = new Button
        {
            Content = CoreTools.Translate("Yes"),
            MinWidth = 0,
            Height = 32,
            Padding = new Thickness(11, 5, 11, 6),
            CornerRadius = new CornerRadius(4),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        yesButton.Classes.Add("accent");
        yesButton.Click += (_, _) => { confirmed = true; dialog.Close(); };

        var content = new Grid
        {
            Background = Application.Current?.FindResource("AppDialogBackground") as IBrush,
        };
        content.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        content.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var messageBlock = new TextBlock
        {
            Text = message,
            FontSize = 14,
            LineHeight = 20,
            Margin = new Thickness(24, 12, 24, 24),
            Foreground = Application.Current?.FindResource("TextFillColorPrimaryBrush") as IBrush,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top,
        };
        content.Children.Add(messageBlock);

        var actions = new Grid
        {
            ColumnSpacing = 8,
            Margin = new Thickness(24),
        };
        actions.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        actions.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        actions.Children.Add(yesButton);
        Grid.SetColumn(noButton, 1);
        actions.Children.Add(noButton);

        var footer = new Border
        {
            Background = Application.Current?.FindResource("AppDialogDarkBackground") as IBrush,
            Child = actions,
        };
        Grid.SetRow(footer, 1);
        content.Children.Add(footer);

        dialog.Content = content;

        await dialog.ShowDialog(owner);
        return confirmed;
    }
}
