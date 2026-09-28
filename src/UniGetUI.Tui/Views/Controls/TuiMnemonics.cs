using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using UniGetUI.Tui.Theme;

namespace UniGetUI.Tui.Views.Controls;

/// <summary>
/// Access keys ("mnemonics") for the menu bar headers: Alt+letter opens a menu. Letters are picked at
/// runtime from the (translated) labels, so every language gets unique keys: the first letter of a word
/// is preferred, then any other letter or digit. Menu items have no access keys; they are reached with
/// the arrows or their global shortcut.
/// </summary>
internal static class TuiMnemonics
{
    /// <summary>
    /// For each label, the index of its access-key character, or -1 when it has none (null or empty
    /// labels, separators, or when every candidate letter is already taken).
    /// </summary>
    public static int[] Assign(IReadOnlyList<string?> labels)
    {
        var used = new HashSet<char>();
        var result = new int[labels.Count];
        Array.Fill(result, -1);

        // Pass 1: every label's own first letter, so "Package details" gets P even when a later
        // label such as "Reinstall package" could also have used it.
        for (int i = 0; i < labels.Count; i++)
        {
            string? label = labels[i];
            if (string.IsNullOrWhiteSpace(label)) continue;
            int first = Candidates(label).DefaultIfEmpty(-1).First();
            if (first >= 0 && used.Add(char.ToLowerInvariant(label[first]))) result[i] = first;
        }

        // Pass 2: labels that lost their first letter take the next free word start, then any letter.
        for (int i = 0; i < labels.Count; i++)
        {
            string? label = labels[i];
            if (result[i] >= 0 || string.IsNullOrWhiteSpace(label)) continue;
            foreach (int candidate in Candidates(label))
            {
                if (!used.Add(char.ToLowerInvariant(label[candidate]))) continue;
                result[i] = candidate;
                break;
            }
        }

        return result;
    }

    private static IEnumerable<int> Candidates(string label)
    {
        // Word starts first ("Clear finished operations" → C, f, o), then every other letter or digit.
        for (int i = 0; i < label.Length; i++)
            if (char.IsLetterOrDigit(label[i]) && (i == 0 || !char.IsLetterOrDigit(label[i - 1])))
                yield return i;
        for (int i = 0; i < label.Length; i++)
            if (char.IsLetterOrDigit(label[i]) && i > 0 && char.IsLetterOrDigit(label[i - 1]))
                yield return i;
    }

    /// <summary>The letter or digit a key press stands for, lowercased, or null.</summary>
    public static char? KeyChar(KeyEventArgs e)
    {
        if (e.Key is >= Key.A and <= Key.Z) return (char)('a' + (e.Key - Key.A));
        if (e.Key is >= Key.D0 and <= Key.D9) return (char)('0' + (e.Key - Key.D0));
        if (e.KeySymbol is { Length: 1 } symbol && char.IsLetterOrDigit(symbol[0])) return char.ToLowerInvariant(symbol[0]);
        return null;
    }

    public static bool Matches(string label, int index, char key)
        => index >= 0 && index < label.Length && char.ToLowerInvariant(label[index]) == key;

    /// <summary>
    /// Builds prefix + label + suffix as a row of text blocks with the access-key character in its own
    /// block, coloured and underlined. (Consolonia does not carry per-<c>Run</c> styling through to the
    /// terminal, so the letter must be a separate text block.)
    /// </summary>
    public static Control Build(string prefix, string label, int index, string suffix, IBrush foreground, bool showKey = true)
    {
        var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal };
        if (!showKey || index < 0 || index >= label.Length)
        {
            row.Children.Add(new TextBlock { Text = prefix + label + suffix, Foreground = foreground });
            return row;
        }

        row.Children.Add(new TextBlock { Text = prefix + label[..index], Foreground = foreground });
        row.Children.Add(new TextBlock
        {
            Text = label[index].ToString(),
            Foreground = TuiPalette.AccessKey,
            TextDecorations = TextDecorations.Underline,
        });
        row.Children.Add(new TextBlock { Text = label[(index + 1)..] + suffix, Foreground = foreground });
        return row;
    }
}
