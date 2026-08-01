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
            MaxWidth = 460,
            MinWidth = 380,
            Title = CoreTools.Translate("Are you sure?"),
            Background = Application.Current?.FindResource("AppDialogBackground") as IBrush,
        };

        var noButton = new Button
        {
            Content = CoreTools.Translate("No"),
            MinWidth = 0,
            Height = 36,
            Padding = new Thickness(12, 3),
            CornerRadius = new CornerRadius(4),
            Background = Application.Current?.FindResource("SettingsCardBackground") as IBrush,
            BorderThickness = new Thickness(0),
            Foreground = Application.Current?.FindResource("TextFillColorPrimaryBrush") as IBrush,
            FontSize = 14,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        noButton.Click += (_, _) => dialog.Close();

        var yesButton = new Button
        {
            Content = CoreTools.Translate("Yes"),
            MinWidth = 0,
            Height = 36,
            Padding = new Thickness(12, 3),
            CornerRadius = new CornerRadius(4),
            FontSize = 14,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        yesButton.Classes.Add("accent");
        yesButton.Click += (_, _) => { confirmed = true; dialog.Close(); };

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        content.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        var messageBlock = new TextBlock
        {
            Text = message,
            FontSize = 14,
            LineHeight = 20,
            Margin = new Thickness(20, 12, 20, 20),
            Foreground = Application.Current?.FindResource("TextFillColorPrimaryBrush") as IBrush,
            TextWrapping = TextWrapping.Wrap,
        };
        content.Children.Add(messageBlock);

        var actions = new Grid
        {
            ColumnSpacing = 8,
            Margin = new Thickness(20, 12),
        };
        actions.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        actions.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        actions.Children.Add(noButton);
        Grid.SetColumn(yesButton, 1);
        actions.Children.Add(yesButton);

        var footer = new Border
        {
            Background = Application.Current?.FindResource("AppWindowBackground") as IBrush,
            Child = actions,
        };
        Grid.SetRow(footer, 1);
        content.Children.Add(footer);

        dialog.Content = content;

        await dialog.ShowDialog(owner);
        return confirmed;
    }
}
