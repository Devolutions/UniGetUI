using Avalonia;
using Consolonia.Controls;

namespace UniGetUI.Tui.Infrastructure;

internal static class TuiDialogs
{
    public static async Task<bool> ConfirmAsync(Visual owner, string title, string message)
    {
        MessageBoxResult result = await MessageBox.ShowDialog(owner, title, message, MessageBoxStyle.YesNo);
        return result == MessageBoxResult.Yes;
    }
}
