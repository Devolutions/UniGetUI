// Two color implementations: Pc supports nested-close replacement and enables
// colors on Windows; Style only colors when stdout is a TTY or FORCE_COLOR is set.

namespace Skills;

internal static class Ansi
{
    public const string Reset = "\x1b[0m";
    public const string Bold = "\x1b[1m";
    public const string Dim = "\x1b[38;5;102m";
    public const string Text = "\x1b[38;5;145m";
    public const string Cyan = "\x1b[36m";
    public const string Yellow = "\x1b[33m";
}

internal static class Pc
{
    internal static string[]? CommandArguments { get; set; }

    private static bool Enabled
    {
        get
        {
            var argv = CommandArguments ?? [];
            var noColor = Sys.EnvTruthy("NO_COLOR") || argv.Contains("--no-color");
            var force = Sys.EnvTruthy("FORCE_COLOR")
                || argv.Contains("--color")
                || OperatingSystem.IsWindows()
                || (Term.StdoutIsTty() && Environment.GetEnvironmentVariable("TERM") != "dumb")
                || Sys.EnvTruthy("CI");
            return !noColor && force;
        }
    }

    private static string ReplaceClose(string s, string close, string replace, int index)
    {
        var result = new System.Text.StringBuilder();
        var cursor = 0;
        do
        {
            result.Append(s, cursor, index - cursor).Append(replace);
            cursor = index + close.Length;
            index = s.IndexOf(close, cursor, StringComparison.Ordinal);
        } while (index >= 0);
        result.Append(s, cursor, s.Length - cursor);
        return result.ToString();
    }

    /// picocolors `formatter(open, close, replace)`.
    internal static string Format(string input, string open, string close, string replace)
    {
        // picocolors: string.indexOf(close, open.length)
        var start = Math.Min(open.Length, input.Length);
        var idx = input.IndexOf(close, start, StringComparison.Ordinal);
        return idx >= 0 ? open + ReplaceClose(input, close, replace, idx) + close : open + input + close;
    }

    private static string F(object? s, string open, string close, string? replace = null)
    {
        var str = s?.ToString() ?? "";
        return Enabled ? Format(str, open, close, replace ?? open) : str;
    }

    public static string Bold(object? s) => F(s, "\x1b[1m", "\x1b[22m", "\x1b[22m\x1b[1m");
    public static string Dim(object? s) => F(s, "\x1b[2m", "\x1b[22m", "\x1b[22m\x1b[2m");
    public static string Underline(object? s) => F(s, "\x1b[4m", "\x1b[24m");
    public static string Inverse(object? s) => F(s, "\x1b[7m", "\x1b[27m");
    public static string Strikethrough(object? s) => F(s, "\x1b[9m", "\x1b[29m");
    public static string Black(object? s) => F(s, "\x1b[30m", "\x1b[39m");
    public static string Red(object? s) => F(s, "\x1b[31m", "\x1b[39m");
    public static string Green(object? s) => F(s, "\x1b[32m", "\x1b[39m");
    public static string Yellow(object? s) => F(s, "\x1b[33m", "\x1b[39m");
    public static string Cyan(object? s) => F(s, "\x1b[36m", "\x1b[39m");
    public static string White(object? s) => F(s, "\x1b[37m", "\x1b[39m");
    public static string BgRed(object? s) => F(s, "\x1b[41m", "\x1b[49m");
    public static string BgCyan(object? s) => F(s, "\x1b[46m", "\x1b[49m");
}

internal static class Style
{
    private static bool Enabled
    {
        get
        {
            var force = Environment.GetEnvironmentVariable("FORCE_COLOR");
            if (force != null) return force != "0" && force != "false";
            return Term.StdoutIsTty()
                && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))
                && Environment.GetEnvironmentVariable("TERM") != "dumb";
        }
    }

    private static string S(object? s, string open, string close)
    {
        var str = s?.ToString() ?? "";
        return Enabled ? open + str + close : str;
    }

    public static string Gray(object? s) => S(s, "\x1b[90m", "\x1b[39m");
    public static string Dim(object? s) => S(s, "\x1b[2m", "\x1b[22m");
    public static string Green(object? s) => S(s, "\x1b[32m", "\x1b[39m");
    public static string Red(object? s) => S(s, "\x1b[31m", "\x1b[39m");
    public static string Cyan(object? s) => S(s, "\x1b[36m", "\x1b[39m");
    public static string Yellow(object? s) => S(s, "\x1b[33m", "\x1b[39m");
    public static string Blue(object? s) => S(s, "\x1b[34m", "\x1b[39m");
    public static string Magenta(object? s) => S(s, "\x1b[35m", "\x1b[39m");
    public static string Reset(object? s) => S(s, "\x1b[0m", "\x1b[0m");
    public static string Strikethrough(object? s) => S(s, "\x1b[9m", "\x1b[29m");
    public static string Inverse(object? s) => S(s, "\x1b[7m", "\x1b[27m");
}
