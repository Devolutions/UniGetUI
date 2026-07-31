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
            MinHeight = 170,
            Title = CoreTools.Translate("Are you sure?"),
            Background = Application.Current?.FindResource("AppDialogBackground") as IBrush,
        };

        var noButton = new Button
        {
            Content = CoreTools.Translate("No"),
            MinWidth = 100,
            Height = 40,
            Padding = new Thickness(12, 4),
            CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        noButton.Click += (_, _) => dialog.Close();

        var yesButton = new Button
        {
            Content = CoreTools.Translate("Yes"),
            MinWidth = 100,
            Height = 40,
            Padding = new Thickness(12, 4),
            CornerRadius = new CornerRadius(4),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        yesButton.Classes.Add("accent");
        yesButton.Click += (_, _) => { confirmed = true; dialog.Close(); };

        var content = new Grid
        {
            Margin = new Thickness(20),
        };
        content.RowDefinitions.Add(new RowDefinition(GridLength.Star));
        content.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        content.Children.Add(new TextBlock
        {
            Text = message,
            Opacity = 0.82,
            TextWrapping = TextWrapping.Wrap,
        });

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, 20, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { noButton, yesButton },
        };
        Grid.SetRow(actions, 1);
        content.Children.Add(actions);

        dialog.Content = content;

        await dialog.ShowDialog(owner);
        return confirmed;
    }
}
