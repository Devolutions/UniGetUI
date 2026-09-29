// Terminal prompts used by the CLI: intro/outro, cancel,
// log.*, note, spinner, select, confirm and multiselect.
//
// Output formatting follows clack 1.x. Interactive prompts read keys with
// Console.ReadKey. When stdin is not a TTY, prompts behave like clack on EOF:
// they render a cancelled frame and report cancellation.

using System.Text;
using System.Text.RegularExpressions;

namespace Skills;

internal enum Key
{
    Up,
    Down,
    Left,
    Right,
    Enter,
    Space,
    Backspace,
    Escape,
    CtrlC,
    Tab,
    Char,
    Other,
}

internal readonly record struct KeyPress(Key Key, char Ch = '\0');

internal sealed record SelectOption<T>(T Value, string Label, string? Hint = null);

internal static partial class Ui
{
    // ─── Symbols ───

    private static readonly Lazy<bool> Unicode = new(() =>
    {
        var term = Environment.GetEnvironmentVariable("TERM") ?? "";
        if (!OperatingSystem.IsWindows()) return term != "linux";
        var termProgram = Environment.GetEnvironmentVariable("TERM_PROGRAM") ?? "";
        return Sys.EnvTruthy("WT_SESSION")
            || Sys.EnvTruthy("TERMINUS_SUBLIME")
            || Environment.GetEnvironmentVariable("ConEmuTask") == "{cmd::Cmder}"
            || termProgram is "Terminus-Sublime" or "vscode"
            || term is "xterm-256color" or "alacritty" or "rxvt-unicode" or "rxvt-unicode-256color"
            || Environment.GetEnvironmentVariable("TERMINAL_EMULATOR") == "JetBrains-JediTerm"
            || Sys.EnvTruthy("CI");
    });

    private static string U(string unicode, string fallback) => Unicode.Value ? unicode : fallback;

    public static string StepActive => U("◆", "*");
    public static string StepCancel => U("■", "x");
    public static string StepSubmit => U("◇", "o");
    public static string BarStart => U("┌", "T");
    public static string Bar => U("│", "|");
    public static string BarEnd => U("└", "—");
    public static string RadioActive => U("●", ">");
    public static string RadioInactive => U("○", " ");
    private static string CheckboxActive => U("◻", "[•]");
    private static string CheckboxSelected => U("◼", "[+]");
    private static string CheckboxInactive => U("◻", "[ ]");
    private static string BarH => U("─", "-");
    private static string CornerTopRight => U("╮", "+");
    private static string ConnectLeft => U("├", "+");
    private static string CornerBottomRight => U("╯", "+");
    private static string Info => U("●", "•");
    private static string Success => U("◆", "*");
    private static string Warn => U("▲", "!");
    private static string Error => U("■", "x");

    // ─── Width helpers ───

    [GeneratedRegex(@"[\u001B\u009B][[\]()#;?]*(?:(?:(?:(?:;[-a-zA-Z\d\/#&.:=?%@~_]+)*|[a-zA-Z\d]+(?:;[-a-zA-Z\d\/#&.:=?%@~_]*)*)?(?:\u0007|\u001B\|\u009C))|(?:(?:\d{1,4}(?:;\d{0,4})*)?[\dA-PR-TZcf-nq-uy=><~]))")]
    private static partial Regex AnsiRegex();

    /// Node's `stripVTControlCharacters`.
    public static string StripAnsi(string s) => AnsiRegex().Replace(s, "");

    private static bool IsWide(int code) =>
        (code >= 0x1100 && code <= 0x115f) || (code >= 0x231a && code <= 0x231b) || (code >= 0x2329 && code <= 0x232a)
        || (code >= 0x23e9 && code <= 0x23ec) || code == 0x23f0 || code == 0x23f3 || (code >= 0x25fd && code <= 0x25fe)
        || (code >= 0x2614 && code <= 0x2615) || (code >= 0x2648 && code <= 0x2653) || code == 0x267f || code == 0x2693
        || code == 0x26a1 || (code >= 0x26aa && code <= 0x26ab) || (code >= 0x26bd && code <= 0x26be) || (code >= 0x26c4 && code <= 0x26c5)
        || code == 0x26ce || code == 0x26d4 || code == 0x26ea || (code >= 0x26f2 && code <= 0x26f3) || code == 0x26f5 || code == 0x26fa
        || code == 0x26fd || code == 0x2705 || (code >= 0x270a && code <= 0x270b) || code == 0x2728 || code == 0x274c || code == 0x274e
        || (code >= 0x2753 && code <= 0x2755) || code == 0x2757 || (code >= 0x2795 && code <= 0x2797) || code == 0x27b0 || code == 0x27bf
        || (code >= 0x2b1b && code <= 0x2b1c) || code == 0x2b50 || code == 0x2b55 || (code >= 0x2e80 && code <= 0xa4cf && code != 0x303f)
        || (code >= 0xa960 && code <= 0xa97c) || (code >= 0xac00 && code <= 0xd7a3) || (code >= 0xf900 && code <= 0xfaff)
        || (code >= 0xfe10 && code <= 0xfe19) || (code >= 0xfe30 && code <= 0xfe6f) || (code >= 0xff00 && code <= 0xff60)
        || (code >= 0xffe0 && code <= 0xffe6) || (code >= 0x1f000 && code <= 0x1f9ff);

    /// Approximate display width of plain text.
    public static int ApproxStringWidth(string plain)
    {
        var w = 0;
        foreach (var r in plain.EnumerateRunes())
        {
            if (r.Value == 0) continue;
            w += IsWide(r.Value) ? 2 : 1;
        }
        return w;
    }

    public static int VisibleWidth(string s) => ApproxStringWidth(StripAnsi(s));

    public static int VisualRowsForLine(string line, int columns)
    {
        var cols = Math.Max(1, columns);
        var w = VisibleWidth(line);
        return Math.Max(1, (w + cols - 1) / cols);
    }

    public static int CountVisualRows(IEnumerable<string> lines, int? columns)
    {
        var cols = columns is > 0 ? columns.Value : Term.TerminalColumns() ?? 80;
        return lines.Sum(l => VisualRowsForLine(l, cols));
    }

    private static int Columns() => Term.TerminalColumns() ?? 80;

    // ─── wrap-ansi ({ hard: true, trim: false }) ───

    private static int? SgrClose(int code) => code switch
    {
        0 => 0,
        1 or 2 => 22,
        3 => 23,
        4 => 24,
        7 => 27,
        8 => 28,
        9 => 29,
        53 => 55,
        >= 30 and <= 37 or >= 90 and <= 97 => 39,
        >= 40 and <= 47 or >= 100 and <= 107 => 49,
        _ => null,
    };

    private static int CharWidth(int c) => c < 0x20 || (c >= 0x7f && c < 0xa0) ? 0 : IsWide(c) ? 2 : 1;

    private static void WrapWord(List<StringBuilder> rows, string word, int columns)
    {
        var runes = word.EnumerateRunes().ToList();
        var insideEscape = false;
        var visible = VisibleWidth(rows[^1].ToString());
        for (var index = 0; index < runes.Count; index++)
        {
            var c = runes[index].Value;
            var len = CharWidth(c);
            if (visible + len <= columns) rows[^1].Append(runes[index].ToString());
            else
            {
                rows.Add(new StringBuilder(runes[index].ToString()));
                visible = 0;
            }
            if (c is 0x1b or 0x9b) insideEscape = true;
            if (insideEscape)
            {
                if (c == 'm') insideEscape = false;
                continue;
            }
            visible += len;
            if (visible == columns && index < runes.Count - 1)
            {
                rows.Add(new StringBuilder());
                visible = 0;
            }
        }
        if (visible == 0 && rows[^1].Length > 0 && rows.Count > 1)
        {
            var last = rows[^1].ToString();
            rows.RemoveAt(rows.Count - 1);
            rows[^1].Append(last);
        }
    }

    [GeneratedRegex(@"\G\x1b\[(\d+)m")]
    private static partial Regex SgrAt();

    private static string WrapLine(string line, int columns)
    {
        var words = line.Split(' ');
        var lengths = words.Select(VisibleWidth).ToArray();
        var rows = new List<StringBuilder> { new() };
        for (var index = 0; index < words.Length; index++)
        {
            var rowLen = VisibleWidth(rows[^1].ToString());
            if (index != 0)
            {
                if (rowLen >= columns)
                {
                    rows.Add(new StringBuilder());
                    rowLen = 0;
                }
                rows[^1].Append(' ');
                rowLen++;
            }
            if (lengths[index] > columns)
            {
                var remaining = columns - rowLen;
                var startingThis = 1 + (int)Math.Floor((lengths[index] - remaining - 1) / (double)columns);
                var startingNext = (lengths[index] - 1) / columns;
                if (startingNext < startingThis) rows.Add(new StringBuilder());
                WrapWord(rows, words[index], columns);
                continue;
            }
            if (rowLen + lengths[index] > columns && rowLen > 0 && lengths[index] > 0) rows.Add(new StringBuilder());
            rows[^1].Append(words[index]);
        }
        var pre = string.Join("\n", rows.Select(r => r.ToString()));
        // Re-open the active SGR style across inserted line breaks.
        var sb = new StringBuilder();
        int? escapeCode = null;
        for (var i = 0; i < pre.Length; i++)
        {
            var c = pre[i];
            sb.Append(c);
            if (c == '\x1b')
            {
                var m = SgrAt().Match(pre, i);
                if (m.Success)
                {
                    var code = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                    escapeCode = code == 39 ? null : code;
                }
            }
            var close = escapeCode is { } ec ? SgrClose(ec) : null;
            var active = escapeCode is { } a && a != 0 ? a : (int?)null;
            if (i + 1 < pre.Length && pre[i + 1] == '\n')
            {
                if (active != null && close != null) sb.Append($"\x1b[{close}m");
            }
            else if (c == '\n')
            {
                if (active != null && close != null) sb.Append($"\x1b[{active}m");
            }
        }
        return sb.ToString();
    }

    /// wrap-ansi with `{ hard: true, trim: false }` (used by note).
    public static string WrapAnsi(string s, int columns)
    {
        columns = Math.Max(1, columns);
        return string.Join("\n", s.Replace("\r\n", "\n").Split('\n').Select(l => WrapLine(l, columns)));
    }

    // ─── Static output ───

    public static void Intro(string title) => Term.Out($"{Style.Gray(BarStart)}  {title}\n");

    public static void Outro(string message) => Term.Out($"{Style.Gray(Bar)}\n{Style.Gray(BarEnd)}  {message}\n\n");

    public static void Cancel(string message) => Term.Out($"{Style.Gray(BarEnd)}  {Style.Red(message)}\n\n");

    private static void LogWithSymbol(string message, string symbol)
    {
        var bar = Style.Gray(Bar);
        var lines = new List<string> { bar };
        var parts = message.Split('\n');
        lines.Add(parts[0].Length > 0 ? $"{symbol}  {parts[0]}" : symbol);
        foreach (var p in parts.Skip(1)) lines.Add(p.Length > 0 ? $"{bar}  {p}" : bar);
        Term.Out(string.Join("\n", lines) + "\n");
    }

    public static class Log
    {
        public static void Message(string msg) => LogWithSymbol(msg, Style.Gray(Bar));
        public static void Info(string msg) => LogWithSymbol(msg, Style.Blue(Ui.Info));
        public static void Success(string msg) => LogWithSymbol(msg, Style.Green(Ui.Success));
        public static void Step(string msg) => LogWithSymbol(msg, Style.Green(StepSubmit));
        public static void Warn(string msg) => LogWithSymbol(msg, Style.Yellow(Ui.Warn));
        public static void Error(string msg) => LogWithSymbol(msg, Style.Red(Ui.Error));
    }

    public static void Note(string message, string title)
    {
        var wrapped = WrapAnsi(message, Columns() - 6);
        var lines = new List<string> { "" };
        lines.AddRange(wrapped.Split('\n').Select(Style.Dim));
        lines.Add("");
        var titleLen = VisibleWidth(title);
        var len = Math.Max(lines.Max(VisibleWidth), titleLen) + 2;
        var body = lines.Select(m => $"{Style.Gray(Bar)}  {m}{new string(' ', Math.Max(0, len - VisibleWidth(m)))}{Style.Gray(Bar)}");
        var top = $"{Style.Green(StepSubmit)}  {Style.Reset(title)} {Style.Gray(Repeat(BarH, Math.Max(len - titleLen - 1, 1)) + CornerTopRight)}";
        var bottom = Style.Gray(ConnectLeft + Repeat(BarH, len + 2) + CornerBottomRight);
        Term.Out($"{Style.Gray(Bar)}\n{top}\n{string.Join("\n", body)}\n{bottom}\n");
    }

    public static string Repeat(string s, int n) => n <= 0 ? "" : string.Concat(Enumerable.Repeat(s, n));

    // ─── Spinner ───

    private static readonly object FrameLock = new();
    private static Spinner? _activeSpinner;

    private static string StopSymbol(int code) => code switch
    {
        0 => Style.Green(StepSubmit),
        1 => Style.Red(StepCancel),
        _ => Style.Red(U("▲", "x")),
    };

    /// clack's spinner `exit` handler: an active spinner is stopped with
    /// "Canceled" (or "Something went wrong" for exit codes > 1).
    public static void OnProcessExit(int code)
    {
        Spinner? active;
        lock (FrameLock)
        {
            active = _activeSpinner;
            _activeSpinner = null;
        }
        active?.StopForExit(code);
    }

    public sealed class Spinner
    {
        private readonly bool _inert;
        private volatile string _message = "";
        private volatile bool _running;
        private volatile bool _stop;
        private bool _animated;
        private Thread? _thread;

        public Spinner(bool inert = false) => _inert = inert;

        /// A spinner that prints nothing (used in --json mode).
        public static Spinner Inert() => new(true);

        private static bool Animated() => Term.StdoutIsTty() && !Term.StdoutRedirected && !Term.OutputSuppressed;

        public void Start(string msg)
        {
            if (_inert) return;
            if (_running)
            {
                HaltThread();
                _running = false;
            }
            _message = msg;
            _running = true;
            _stop = false;
            lock (FrameLock) _activeSpinner = this;
            // clack hides the cursor for the spinner's lifetime, even without a TTY.
            Term.Out($"\x1b[?25l{Style.Gray(Bar)}\n");
            _animated = Animated();
            if (!_animated) return;
            string[] frames = Unicode.Value ? ["◒", "◐", "◓", "◑"] : ["•", "o", "O", "0"];
            var delay = Unicode.Value ? 80 : 120;
            _thread = new Thread(() =>
            {
                var i = 0;
                var dots = 0.0;
                while (true)
                {
                    lock (FrameLock)
                    {
                        if (_stop) break;
                        var d = new string('.', Math.Min(3, (int)Math.Floor(dots)));
                        var clear = i > 0 ? "\x1b[1G\x1b[J" : "";
                        Term.Out($"{clear}{Style.Magenta(frames[i % frames.Length])}  {_message}{d}");
                    }
                    i++;
                    dots = dots < 4 ? dots + 0.125 : 0;
                    Thread.Sleep(delay);
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public void Message(string msg)
        {
            if (!_inert) _message = msg;
        }

        private void HaltThread()
        {
            lock (FrameLock) _stop = true;
            if (_thread != null)
            {
                _thread.Join();
                _thread = null;
                Term.Out("\x1b[1G\x1b[J");
            }
        }

        public void Stop(string msg)
        {
            if (_inert || !_running) return;
            HaltThread();
            _running = false;
            lock (FrameLock)
            {
                if (ReferenceEquals(_activeSpinner, this)) _activeSpinner = null;
            }
            Term.Out($"{Style.Green(StepSubmit)}  {msg}\n\x1b[?25h");
        }

        internal void StopForExit(int code)
        {
            lock (FrameLock) _stop = true;
            if (!_running) return;
            _running = false;
            var clear = _animated ? "\x1b[1G\x1b[J" : "";
            var msg = code > 1 ? "Something went wrong" : "Canceled";
            Term.Out($"{clear}{StopSymbol(code)}  {msg}\n\x1b[?25h");
        }
    }

    // ─── Raw terminal input ───

    private static bool _cursorHidden;

    public static void RestoreTerminal()
    {
        if (_cursorHidden)
        {
            _cursorHidden = false;
            Term.Out("\x1b[?25h");
        }
        try
        {
            if (Term.StdinIsTty()) Console.TreatControlCAsInput = false;
        }
        catch
        {
            // no console
        }
    }

    /// Prepare the console for key-by-key input; false when stdin is not a TTY.
    public static bool EnterRawMode()
    {
        if (!Term.StdinIsTty()) return false;
        try
        {
            Console.TreatControlCAsInput = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static void LeaveRawMode()
    {
        try
        {
            Console.TreatControlCAsInput = false;
        }
        catch
        {
            // ignore
        }
    }

    private static KeyPress Map(ConsoleKeyInfo k)
    {
        var ctrl = k.Modifiers.HasFlag(ConsoleModifiers.Control);
        var alt = k.Modifiers.HasFlag(ConsoleModifiers.Alt);
        if (ctrl && k.Key == ConsoleKey.C) return new KeyPress(Key.CtrlC);
        return k.Key switch
        {
            ConsoleKey.UpArrow => new KeyPress(Key.Up),
            ConsoleKey.DownArrow => new KeyPress(Key.Down),
            ConsoleKey.LeftArrow => new KeyPress(Key.Left),
            ConsoleKey.RightArrow => new KeyPress(Key.Right),
            ConsoleKey.Enter => new KeyPress(Key.Enter),
            ConsoleKey.Backspace => new KeyPress(Key.Backspace),
            ConsoleKey.Escape => new KeyPress(Key.Escape),
            ConsoleKey.Tab => new KeyPress(Key.Tab),
            ConsoleKey.Spacebar when !ctrl && !alt => new KeyPress(Key.Space),
            _ when k.KeyChar != '\0' && !ctrl && !alt && !char.IsControl(k.KeyChar) => new KeyPress(Key.Char, k.KeyChar),
            _ => new KeyPress(Key.Other),
        };
    }

    /// Block until the next key press.
    public static KeyPress ReadKey()
    {
        try
        {
            return Map(Console.ReadKey(intercept: true));
        }
        catch (InvalidOperationException)
        {
            return new KeyPress(Key.CtrlC);
        }
    }

    /// Wait up to `timeout` for a key press.
    public static KeyPress? PollKey(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (true)
        {
            try
            {
                if (Console.KeyAvailable) return Map(Console.ReadKey(intercept: true));
            }
            catch (InvalidOperationException)
            {
                return new KeyPress(Key.CtrlC);
            }
            if (DateTime.UtcNow >= deadline) return null;
            Thread.Sleep(10);
        }
    }

    public static void HideCursor()
    {
        _cursorHidden = true;
        Term.Out("\x1b[?25l");
    }

    public static void ShowCursor()
    {
        _cursorHidden = false;
        Term.Out("\x1b[?25h");
    }

    /// Redraws a block of lines in place.
    public sealed class Frame
    {
        private int _lastHeight;

        public void Render(string text)
        {
            var clear = _lastHeight > 0 ? $"\x1b[{_lastHeight}A\x1b[J" : "";
            var body = text.TrimEnd('\n');
            Term.Out($"\r{clear}{body}\n");
            _lastHeight = CountVisualRows(body.Split('\n'), null);
        }
    }

    // ─── Prompts ───

    private enum PromptState
    {
        Active,
        Submit,
        Cancel,
    }

    private static string TitleBlock(PromptState state, string message)
    {
        var sym = state switch
        {
            PromptState.Active => Style.Cyan(StepActive),
            PromptState.Submit => Style.Green(StepSubmit),
            _ => Style.Red(StepCancel),
        };
        var lines = message.Split('\n');
        var sb = new StringBuilder($"{Style.Gray(Bar)}\n{sym}  {lines[0]}");
        foreach (var l in lines.Skip(1)) sb.Append($"\n{Style.Gray(Bar)}  {l}");
        return sb.Append('\n').ToString();
    }

    private static bool RunPrompt(Func<PromptState, string> render, Func<KeyPress, PromptState?> onKey)
    {
        var frame = new Frame();
        if (!EnterRawMode())
        {
            frame.Render(render(PromptState.Cancel));
            return false;
        }
        HideCursor();
        frame.Render(render(PromptState.Active));
        PromptState result;
        while (true)
        {
            var next = onKey(ReadKey());
            if (next is { } s && s != PromptState.Active)
            {
                result = s;
                break;
            }
            frame.Render(render(PromptState.Active));
        }
        frame.Render(render(result));
        LeaveRawMode();
        ShowCursor();
        return result == PromptState.Submit;
    }

    /// `p.select`. Returns false when cancelled.
    public static bool Select<T>(string message, IReadOnlyList<SelectOption<T>> options, int initial, out T value)
    {
        var cursor = Math.Clamp(initial, 0, options.Count - 1);
        string Render(PromptState state)
        {
            var s = new StringBuilder(TitleBlock(state, message));
            switch (state)
            {
                case PromptState.Submit:
                    s.Append($"{Style.Gray(Bar)}  {Style.Dim(options[cursor].Label)}\n");
                    break;
                case PromptState.Cancel:
                    s.Append($"{Style.Gray(Bar)}  {Style.Strikethrough(Style.Dim(options[cursor].Label))}\n{Style.Gray(Bar)}\n");
                    break;
                default:
                    var bar = $"{Style.Cyan(Bar)}  ";
                    var rows = options.Select((o, i) => i == cursor
                        ? $"{bar}{Style.Green(RadioActive)} {o.Label}{(o.Hint != null ? " " + Style.Dim($"({o.Hint})") : "")}"
                        : $"{bar}{Style.Dim(RadioInactive)} {Style.Dim(o.Label)}");
                    s.Append(string.Join("\n", rows)).Append($"\n{Style.Cyan(BarEnd)}\n");
                    break;
            }
            return s.ToString();
        }
        var ok = RunPrompt(Render, k =>
        {
            switch (k.Key)
            {
                case Key.Up or Key.Left:
                case Key.Char when k.Ch is 'k' or 'h':
                    cursor = cursor == 0 ? options.Count - 1 : cursor - 1;
                    return null;
                case Key.Down or Key.Right:
                case Key.Char when k.Ch is 'j' or 'l':
                    cursor = (cursor + 1) % options.Count;
                    return null;
                case Key.Enter: return PromptState.Submit;
                case Key.Escape or Key.CtrlC: return PromptState.Cancel;
                default: return null;
            }
        });
        value = options[cursor].Value;
        return ok;
    }

    /// `p.confirm`. Returns null when cancelled.
    public static bool? Confirm(string message, bool initial = true)
    {
        var value = initial;
        string Render(PromptState state)
        {
            var s = new StringBuilder(TitleBlock(state, message));
            var label = value ? "Yes" : "No";
            switch (state)
            {
                case PromptState.Submit:
                    s.Append($"{Style.Gray(Bar)}  {Style.Dim(label)}\n");
                    break;
                case PromptState.Cancel:
                    s.Append($"{Style.Gray(Bar)}  {Style.Strikethrough(Style.Dim(label))}\n{Style.Gray(Bar)}\n");
                    break;
                default:
                    var yes = value ? $"{Style.Green(RadioActive)} Yes" : $"{Style.Dim(RadioInactive)} {Style.Dim("Yes")}";
                    var no = value ? $"{Style.Dim(RadioInactive)} {Style.Dim("No")}" : $"{Style.Green(RadioActive)} No";
                    s.Append($"{Style.Cyan(Bar)}  {yes} {Style.Dim("/")} {no}\n{Style.Cyan(BarEnd)}\n");
                    break;
            }
            return s.ToString();
        }
        var ok = RunPrompt(Render, k =>
        {
            switch (k.Key)
            {
                case Key.Up or Key.Down or Key.Left or Key.Right:
                case Key.Char when k.Ch is 'h' or 'j' or 'k' or 'l':
                    value = !value;
                    return null;
                case Key.Char when k.Ch is 'y' or 'Y':
                    value = true;
                    return PromptState.Submit;
                case Key.Char when k.Ch is 'n' or 'N':
                    value = false;
                    return PromptState.Submit;
                case Key.Enter: return PromptState.Submit;
                case Key.Escape or Key.CtrlC: return PromptState.Cancel;
                default: return null;
            }
        });
        return ok ? value : null;
    }

    /// `p.multiselect`. Returns null when cancelled.
    public static List<T>? Multiselect<T>(string message, IReadOnlyList<SelectOption<T>> options, IReadOnlyCollection<T> initial, bool required)
    {
        var selected = options.Select(o => initial.Contains(o.Value)).ToArray();
        var cursor = 0;
        var warning = false;
        string Render(PromptState state)
        {
            var s = new StringBuilder(TitleBlock(state, message));
            var chosen = options.Where((_, i) => selected[i]).Select(o => o.Label).ToList();
            switch (state)
            {
                case PromptState.Submit:
                    s.Append($"{Style.Gray(Bar)}  {(chosen.Count == 0 ? Style.Dim("none") : string.Join(Style.Dim(", "), chosen.Select(Style.Dim)))}\n");
                    break;
                case PromptState.Cancel:
                    var label = string.Join(", ", chosen);
                    s.Append(label.Trim().Length == 0 ? $"{Style.Gray(Bar)}\n" : $"{Style.Gray(Bar)}  {Style.Strikethrough(Style.Dim(label))}\n{Style.Gray(Bar)}\n");
                    break;
                default:
                    var bar = $"{(warning ? Style.Yellow(Bar) : Style.Cyan(Bar))}  ";
                    var rows = options.Select((o, i) =>
                    {
                        var hint = o.Hint != null ? " " + Style.Dim($"({o.Hint})") : "";
                        var body = (i == cursor, selected[i]) switch
                        {
                            (true, true) => $"{Style.Green(CheckboxSelected)} {o.Label}{hint}",
                            (true, false) => $"{Style.Cyan(CheckboxActive)} {o.Label}{hint}",
                            (false, true) => $"{Style.Green(CheckboxSelected)} {Style.Dim(o.Label)}",
                            _ => $"{Style.Dim(CheckboxInactive)} {Style.Dim(o.Label)}",
                        };
                        return bar + body;
                    });
                    s.Append(string.Join("\n", rows));
                    s.Append(warning
                        ? $"\n{Style.Yellow(BarEnd)}  {Style.Yellow("Please select at least one option.")}\n"
                        : $"\n{Style.Cyan(BarEnd)}\n");
                    break;
            }
            return s.ToString();
        }
        var ok = RunPrompt(Render, k =>
        {
            warning = false;
            switch (k.Key)
            {
                case Key.Up or Key.Left:
                case Key.Char when k.Ch is 'k' or 'h':
                    cursor = cursor == 0 ? options.Count - 1 : cursor - 1;
                    return null;
                case Key.Down or Key.Right:
                case Key.Char when k.Ch is 'j' or 'l':
                    cursor = (cursor + 1) % options.Count;
                    return null;
                case Key.Space:
                    selected[cursor] = !selected[cursor];
                    return null;
                case Key.Char when k.Ch == 'a':
                    var all = selected.All(x => x);
                    for (var i = 0; i < selected.Length; i++) selected[i] = !all;
                    return null;
                case Key.Enter:
                    if (required && !selected.Any(x => x))
                    {
                        warning = true;
                        return null;
                    }
                    return PromptState.Submit;
                case Key.Escape or Key.CtrlC: return PromptState.Cancel;
                default: return null;
            }
        });
        return ok ? options.Where((_, i) => selected[i]).Select(o => o.Value).ToList() : null;
    }
}
