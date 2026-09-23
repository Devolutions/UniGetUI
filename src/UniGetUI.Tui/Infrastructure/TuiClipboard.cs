using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using UniGetUI.Core.Logging;

namespace UniGetUI.Tui.Infrastructure;

internal static class TuiClipboard
{
    public static async Task<bool> CopyAsync(Visual owner, string description, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            TuiNotifications.Warning("Nothing to copy", description);
            return false;
        }

        var clipboard = TopLevel.GetTopLevel(owner)?.Clipboard;
        if (clipboard is null)
        {
            TuiNotifications.Warning("Clipboard unavailable", "This terminal does not expose clipboard access.");
            return false;
        }

        try
        {
            await clipboard.SetTextAsync(text);
            TuiNotifications.Success("Copied to clipboard", description);
            return true;
        }
        catch (Exception ex)
        {
            TuiNotifications.Error("Clipboard copy failed", ex.Message);
            Logger.Error(ex);
            return false;
        }
    }
}
