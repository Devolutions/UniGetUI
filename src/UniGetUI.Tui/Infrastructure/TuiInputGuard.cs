using System.Text;
using Avalonia.Controls;
using Avalonia.Input;

namespace UniGetUI.Tui.Infrastructure;

internal static class TuiInputGuard
{
    public const int DefaultMaxTextLength = 256;

    public static bool HandleTextInput(
        AutoCompleteBox target,
        TextInputEventArgs e,
        string targetName,
        int maxTextLength = DefaultMaxTextLength)
    {
        string incoming = e.Text ?? string.Empty;
        if (incoming.Length == 0) return false;

        string current = target.Text ?? string.Empty;
        int caret = Math.Clamp(target.CaretIndex, 0, current.Length);
        int available = Math.Max(0, maxTextLength - current.Length);
        string sanitized = Sanitize(incoming, available, out bool changed, out bool truncated);
        if (!changed && !truncated) return false;

        e.Handled = true;
        if (sanitized.Length > 0)
        {
            target.Text = current.Insert(caret, sanitized);
            target.CaretIndex = caret + sanitized.Length;
        }

        string reason = truncated
            ? $"Pasted text was sanitized and capped at {maxTextLength} characters."
            : "Pasted text was sanitized before insertion.";
        TuiNotifications.Warning("Input sanitized", $"{targetName}: {reason}");
        return true;
    }

    internal static string Sanitize(string text, int maxLength, out bool changed, out bool truncated)
    {
        if (maxLength <= 0)
        {
            changed = text.Length > 0;
            truncated = text.Length > 0;
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(text.Length, maxLength));
        changed = false;
        truncated = false;

        foreach (char c in text)
        {
            if (builder.Length == maxLength)
            {
                truncated = true;
                break;
            }

            if (c is '\r' or '\n' or '\t')
            {
                builder.Append(' ');
                changed = true;
            }
            else if (char.IsControl(c))
            {
                changed = true;
            }
            else
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }
}
