using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using UniGetUI.Core.Logging;

namespace UniGetUI.Tui.Infrastructure;

internal static class TuiClipboard
{
    /// <summary>
    /// Removes the Avalonia clipboard service on Windows. Consolonia's Win32 console runs every input
    /// batch through a clipboard-paste sniffer whenever an <see cref="IClipboard"/> service is registered;
    /// under ConPTY each keystroke arrives as a two-event batch, so the sniffer swallows every other typed
    /// character. Without the service, typed and pasted text (a terminal paste is a burst of keystrokes)
    /// reaches the controls unchanged, and copying goes straight to the Win32 clipboard instead.
    /// </summary>
    public static AppBuilder WithoutConsolePasteSniffer(this AppBuilder builder)
        => OperatingSystem.IsWindows() ? builder.With<IClipboard>(default(IClipboard)!) : builder;

    public static async Task<bool> CopyAsync(Visual owner, string description, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            TuiNotifications.Warning("Nothing to copy", description);
            return false;
        }

        try
        {
#if WINDOWS
            await Task.Run(() => ExternalLibraries.Clipboard.WindowsClipboard.SetText(text));
#else
            var clipboard = TopLevel.GetTopLevel(owner)?.Clipboard;
            if (clipboard is null)
            {
                TuiNotifications.Warning("Clipboard unavailable", "This terminal does not expose clipboard access.");
                return false;
            }

            await clipboard.SetTextAsync(text);
#endif
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
