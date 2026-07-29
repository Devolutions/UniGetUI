using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using UniGetUI.Avalonia.Views;
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
        var dialog = new Window
        {
            Width = 460,
            SizeToContent = SizeToContent.Height,
            MinHeight = 170,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = CoreTools.Translate("Are you sure?"),
        };

        var noButton = new Button { Content = CoreTools.Translate("No"), MinWidth = 100 };
        noButton.Click += (_, _) => dialog.Close();

        var yesButton = new Button { Content = CoreTools.Translate("Yes"), MinWidth = 100 };
        yesButton.Classes.Add("accent");
        yesButton.Click += (_, _) => { confirmed = true; dialog.Close(); };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 16,
            Children =
            {
                new TextBlock
                {
                    Text = CoreTools.Translate("Are you sure?"),
                    FontSize = 18,
                    FontWeight = FontWeight.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                },
                new TextBlock { Text = message, Opacity = 0.82, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { noButton, yesButton },
                },
            },
        };

        await dialog.ShowDialog(owner);
        return confirmed;
    }
}
