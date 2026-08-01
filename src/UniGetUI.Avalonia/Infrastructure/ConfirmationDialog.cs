using Avalonia.Controls;
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
            return false;

        var messageBlock = new TextBlock
        {
            Text = message,
            FontSize = 14,
            LineHeight = 20,
            TextWrapping = TextWrapping.Wrap,
        };
        var dialog = new ImmersiveConfirmationDialog(
            CoreTools.Translate("Are you sure?"),
            messageBlock,
            CoreTools.Translate("Yes"),
            CoreTools.Translate("No"));

        await dialog.ShowDialog(owner);
        return dialog.Result is true;
    }
}
