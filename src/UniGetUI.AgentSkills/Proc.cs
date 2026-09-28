// Subprocess execution with timeouts (stands in for execFile/spawn/spawnSync).
// Arguments are passed as an argv list, never through a shell.

using System.Diagnostics;
using System.Text;

namespace Skills;

internal sealed record ProcOutput(int? Status, byte[] Stdout, byte[] Stderr)
{
    public bool Success => Status == 0;
    public string StdoutText => Encoding.UTF8.GetString(Stdout);
    public string StderrText => Encoding.UTF8.GetString(Stderr);
}

internal enum ProcErrorKind
{
    NotFound,
    Timeout,
    TooMuchOutput,
    Other,
}

internal sealed class ProcException(ProcErrorKind kind, string message) : Exception(message)
{
    public ProcErrorKind Kind { get; } = kind;
}

internal sealed class Proc
{
    private readonly string _program;
    private readonly List<string> _args;
    private readonly Dictionary<string, string> _env = new();
    private TimeSpan? _timeout;
    private int? _maxOutput;
    private bool _hideWindow;

    private Proc(string program, IEnumerable<string> args)
    {
        _program = program;
        _args = args.ToList();
    }

    /// Children share the parent's console, like Node's default `windowsHide: false`.
    public static Proc Command(string program, params string[] args) => new(program, args);

    public static Proc Command(string program, IEnumerable<string> args) => new(program, args);

    public Proc Env(string k, string v)
    {
        _env[k] = v;
        return this;
    }

    public Proc Timeout(TimeSpan t)
    {
        _timeout = t;
        return this;
    }

    public Proc MaxOutput(int bytes)
    {
        _maxOutput = bytes;
        return this;
    }

    /// Node's `windowsHide: true`.
    public Proc HideWindow()
    {
        _hideWindow = true;
        return this;
    }

    private ProcessStartInfo StartInfo(bool redirectStdin, bool redirectOut)
    {
        var psi = new ProcessStartInfo(_program)
        {
            UseShellExecute = false,
            RedirectStandardInput = redirectStdin,
            RedirectStandardOutput = redirectOut,
            RedirectStandardError = redirectOut,
            // A library host (a GUI app) has no console to share: never flash one.
            CreateNoWindow = _hideWindow || Sys.Context != null,
        };
        foreach (var a in _args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in _env) psi.Environment[k] = v;
        return psi;
    }

    private static Process Start(ProcessStartInfo psi)
    {
        try
        {
            return Process.Start(psi) ?? throw new ProcException(ProcErrorKind.Other, "failed to start process");
        }
        catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode is 2 or 3)
        {
            throw new ProcException(ProcErrorKind.NotFound, "command not found");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new ProcException(ProcErrorKind.Other, e.Message);
        }
    }

    /// Run with stdin closed and stdout/stderr captured.
    public ProcOutput Output()
    {
        var cancel = Sys.Context?.Cancel ?? CancellationToken.None;
        cancel.ThrowIfCancellationRequested();
        using var p = Start(StartInfo(redirectStdin: true, redirectOut: true));
        using var onCancel = cancel.Register(() => Kill(p));
        p.StandardInput.Close();
        var outTask = ReadAllAsync(p.StandardOutput.BaseStream);
        var errTask = ReadAllAsync(p.StandardError.BaseStream);
        var ms = _timeout.HasValue ? (int)Math.Min(int.MaxValue, _timeout.Value.TotalMilliseconds) : -1;
        if (!p.WaitForExit(ms))
        {
            Kill(p);
            cancel.ThrowIfCancellationRequested();
            throw new ProcException(ProcErrorKind.Timeout, "timed out");
        }
        p.WaitForExit();
        cancel.ThrowIfCancellationRequested();
        var stdout = outTask.GetAwaiter().GetResult();
        var stderr = errTask.GetAwaiter().GetResult();
        if (_maxOutput.HasValue && stdout.Length + stderr.Length > _maxOutput.Value)
            throw new ProcException(ProcErrorKind.TooMuchOutput, "output exceeded buffer");
        return new ProcOutput(p.ExitCode, stdout, stderr);
    }

    /// Run with inherited stdio, returning the exit code.
    public int StatusInherit()
    {
        using var p = Start(StartInfo(redirectStdin: false, redirectOut: false));
        p.WaitForExit();
        return p.ExitCode;
    }

    /// spawnSync with `stdio: ['inherit', 'pipe', 'pipe']`.
    public ProcOutput OutputInheritStdin()
    {
        using var p = Start(StartInfo(redirectStdin: false, redirectOut: true));
        var outTask = ReadAllAsync(p.StandardOutput.BaseStream);
        var errTask = ReadAllAsync(p.StandardError.BaseStream);
        p.WaitForExit();
        return new ProcOutput(p.ExitCode, outTask.GetAwaiter().GetResult(), errTask.GetAwaiter().GetResult());
    }

    /// Run with stdout/stderr inherited, feeding `input` on stdin (a pager).
    /// A child that exits early (closing the pipe) is not an error.
    public int RunWithStdin(byte[] input)
    {
        using var p = Start(StartInfo(redirectStdin: true, redirectOut: false));
        try
        {
            p.StandardInput.BaseStream.Write(input, 0, input.Length);
            p.StandardInput.Close();
        }
        catch (IOException)
        {
            // the pager quit before reading everything
        }
        p.WaitForExit();
        return p.ExitCode;
    }

    private static void Kill(Process p)
    {
        try { p.Kill(entireProcessTree: true); } catch { /* already exited */ }
    }

    private static async Task<byte[]> ReadAllAsync(Stream s)
    {
        using var ms = new MemoryStream();
        await s.CopyToAsync(ms).ConfigureAwait(false);
        return ms.ToArray();
    }
}
