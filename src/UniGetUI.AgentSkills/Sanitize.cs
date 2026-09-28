// Sanitize untrusted strings before terminal output (port of sanitize.ts).
//
// Strips CSI/OSC/DCS/PM/APC sequences, simple two-byte escapes, C1 control
// codes and raw control characters (except \t and \n), defending against
// terminal escape injection from skill metadata or remote APIs.

using System.Text.RegularExpressions;

namespace Skills;

internal static partial class Sanitize
{
    [GeneratedRegex(@"\x1b\][\s\S]*?(?:\x07|\x1b\\)")]
    private static partial Regex Osc();

    [GeneratedRegex(@"\x1b[P^_][\s\S]*?\x1b\\")]
    private static partial Regex DcsPmApc();

    [GeneratedRegex(@"\x1b\[[\x30-\x3f]*[\x20-\x2f]*[\x40-\x7e]")]
    private static partial Regex Csi();

    [GeneratedRegex(@"\x1b[\x20-\x7e]")]
    private static partial Regex SimpleEsc();

    [GeneratedRegex(@"[\u0080-\u009f]")]
    private static partial Regex C1();

    [GeneratedRegex(@"[\x00-\x08\x0b\x0c\x0d-\x1a\x1c-\x1f\x7f]")]
    private static partial Regex Control();

    [GeneratedRegex(@"[\r\n]+")]
    private static partial Regex Newlines();

    /// Strip all terminal escape sequences and dangerous control characters.
    public static string StripTerminalEscapes(string s)
    {
        s = Osc().Replace(s, "");
        s = DcsPmApc().Replace(s, "");
        s = Csi().Replace(s, "");
        s = SimpleEsc().Replace(s, "");
        s = C1().Replace(s, "");
        return Control().Replace(s, "");
    }

    /// Sanitize a metadata string for single-line terminal display.
    public static string Metadata(string s) => JsTrim(Newlines().Replace(StripTerminalEscapes(s), " "));

    private static bool IsJsWhitespace(char c) =>
        (int)c is 0x09 or 0x0A or 0x0B or 0x0C or 0x0D or 0x20 or 0xA0 or 0x1680
            or (>= 0x2000 and <= 0x200A) or 0x2028 or 0x2029 or 0x202F or 0x205F or 0x3000 or 0xFEFF;

    /// JavaScript `String.prototype.trim`.
    public static string JsTrim(string s)
    {
        int start = 0, end = s.Length;
        while (start < end && IsJsWhitespace(s[start])) start++;
        while (end > start && IsJsWhitespace(s[end - 1])) end--;
        return s[start..end];
    }
}
