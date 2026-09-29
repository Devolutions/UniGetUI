// Terminal I/O for the CLI: stdout/stderr routing, exit handling that mirrors
// Node's `process.exit`, TTY detection and Windows console VT mode. Environment
// and directory helpers live in the library's `Sys`.

using System.Runtime.InteropServices;
using System.Text;

namespace Skills;

internal sealed class CliExitException(int code) : Exception
{
    public int Code { get; } = code;
}

internal static partial class Term
{
    private static readonly object OutLock = new();
    private static readonly AsyncLocal<bool> Suppressed = new();
    private static volatile bool _redirectStdout;
    private static int _exitCode;
    private static readonly List<Action> ExitHooks = new();

    // ─── Output ───

    internal static bool OutputSuppressed => Suppressed.Value;

    internal static IDisposable SuppressOutput()
    {
        var previous = Suppressed.Value;
        Suppressed.Value = true;
        return new OutputScope(previous);
    }

    private sealed class OutputScope(bool previous) : IDisposable
    {
        public void Dispose() => Suppressed.Value = previous;
    }

    /// When set, everything written through <see cref="Out"/> goes to stderr. This
    /// is how `add --json` keeps stdout reserved for the final JSON value.
    public static bool StdoutRedirected
    {
        get => _redirectStdout;
        set => _redirectStdout = value;
    }

    private static readonly Lazy<Stream> StdoutStream = new(Console.OpenStandardOutput);
    private static readonly Lazy<Stream> StderrStream = new(Console.OpenStandardError);

    private static void WriteTo(bool stderr, string s)
    {
        if (s.Length == 0 || OutputSuppressed) return;
        lock (OutLock)
        {
            if (OperatingSystem.IsWindows() && WinConsole.TryWrite(stderr, s)) return;
            var stream = stderr ? StderrStream.Value : StdoutStream.Value;
            var bytes = Encoding.UTF8.GetBytes(s);
            try
            {
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();
            }
            catch (IOException)
            {
                // Broken pipe: ignore, like Node does for EPIPE on exit.
            }
        }
    }

    /// Write to (possibly redirected) stdout.
    public static void Out(string s) => WriteTo(StdoutRedirected, s);

    public static void OutLine(string s = "") => Out(s + "\n");

    /// Write to the real stdout, bypassing redirection.
    public static void RealStdout(string s) => WriteTo(false, s);

    /// Write raw bytes to the real stdout (a console gets them decoded as UTF-8).
    public static void OutBytes(byte[] bytes)
    {
        if (bytes.Length == 0 || OutputSuppressed) return;
        lock (OutLock)
        {
            if (OperatingSystem.IsWindows() && WinConsole.TryWrite(false, Encoding.UTF8.GetString(bytes))) return;
            try
            {
                StdoutStream.Value.Write(bytes, 0, bytes.Length);
                StdoutStream.Value.Flush();
            }
            catch (IOException)
            {
                // Broken pipe: ignore.
            }
        }
    }

    public static void Err(string s) => WriteTo(true, s);

    public static void ErrLine(string s = "") => Err(s + "\n");

    // ─── Exit ───

    public static int ExitCode
    {
        get => Volatile.Read(ref _exitCode);
        set => Volatile.Write(ref _exitCode, value);
    }

    /// Register a hook that runs before the process exits via <see cref="Exit"/>.
    public static void OnExit(Action hook)
    {
        lock (ExitHooks) ExitHooks.Add(hook);
    }

    public static void ClearExitHooks()
    {
        lock (ExitHooks) ExitHooks.Clear();
    }

    /// End this CLI invocation without terminating an embedding application's process.
    /// Process exit is handled by the host.
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    public static void Exit(int code)
    {
        List<Action> hooks;
        lock (ExitHooks)
        {
            hooks = new List<Action>(ExitHooks);
            ExitHooks.Clear();
        }
        foreach (var h in hooks) h();
        throw new CliExitException(code);
    }

    public static bool StdinIsTty() => !Console.IsInputRedirected;

    public static bool StdoutIsTty() => !Console.IsOutputRedirected;

    public static int? TerminalColumns()
    {
        if (!StdoutIsTty()) return null;
        try
        {
            var w = Console.WindowWidth;
            return w > 0 ? w : null;
        }
        catch
        {
            return null;
        }
    }

    public static int? TerminalRows()
    {
        if (!StdoutIsTty()) return null;
        try
        {
            var h = Console.WindowHeight;
            return h > 0 ? h : null;
        }
        catch
        {
            return null;
        }
    }

    /// Enable ANSI escape processing on the Windows console (Node's libuv
    /// translates escapes itself, so output must render the same way).
    public static void EnableVirtualTerminal()
    {
        if (OperatingSystem.IsWindows()) WinConsole.EnableVt();
    }

    private static partial class WinConsole
    {
        private const int StdOutputHandle = -11;
        private const int StdErrorHandle = -12;
        private const uint EnableVirtualTerminalProcessing = 0x0004;

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial IntPtr GetStdHandle(int nStdHandle);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool WriteConsoleW(IntPtr hConsoleOutput, string lpBuffer, uint nNumberOfCharsToWrite, out uint lpNumberOfCharsWritten, IntPtr lpReserved);

        private static readonly Lazy<IntPtr?> OutHandle = new(() => ConsoleHandle(StdOutputHandle));
        private static readonly Lazy<IntPtr?> ErrHandle = new(() => ConsoleHandle(StdErrorHandle));

        private static IntPtr? ConsoleHandle(int std)
        {
            var h = GetStdHandle(std);
            if (h == IntPtr.Zero || h == new IntPtr(-1)) return null;
            return GetConsoleMode(h, out _) ? h : null;
        }

        /// Write UTF-16 text directly to a console handle; false when the stream
        /// is not a console (pipe or file).
        public static bool TryWrite(bool stderr, string s)
        {
            var h = stderr ? ErrHandle.Value : OutHandle.Value;
            if (h == null) return false;
            var offset = 0;
            while (offset < s.Length)
            {
                var chunk = s.Substring(offset, Math.Min(8192, s.Length - offset));
                if (!WriteConsoleW(h.Value, chunk, (uint)chunk.Length, out var written, IntPtr.Zero) || written == 0) return true;
                offset += (int)written;
            }
            return true;
        }

        public static void EnableVt()
        {
            foreach (var h in new[] { OutHandle.Value, ErrHandle.Value })
            {
                if (h is { } handle && GetConsoleMode(handle, out var mode))
                    SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
            }
        }
    }
}
