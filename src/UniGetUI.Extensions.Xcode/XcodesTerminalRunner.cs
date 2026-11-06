using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using UniGetUI.Core.Logging;

namespace UniGetUI.Extensions.Xcode;

/// <summary>
/// Runs xcodes the way a person would, in a terminal, for the commands that may ask a question: signing in
/// to an Apple ID, a two-factor code, the macOS password. Without a terminal xcodes prints no progress and
/// buffers its questions where nothing can see them, so it is started under <c>script</c>, which gives it a
/// pseudo-terminal, and its output is read the way a terminal would draw it. When it stops at a question,
/// <see cref="XcodesPromptHandler"/> supplies the answer. Secrets are passed through and never logged.
/// </summary>
public sealed class XcodesTerminalRunner
{
    public const string DefaultHost = "/usr/bin/script";

    /// <summary>How long a recognised question has to sit unfinished, with nothing more written, before it is asked.</summary>
    private static readonly TimeSpan KnownPromptSettleTime = TimeSpan.FromMilliseconds(150);

    /// <summary>Any other line that ends like a question has to sit for longer, in case it is just slow output.</summary>
    private static readonly TimeSpan UnknownPromptSettleTime = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(5);

    private const int MaximumContextLines = 12;

    private readonly string? _executable;
    private readonly IReadOnlyDictionary<string, string?> _environment;
    private readonly string _host;

    /// <param name="executable">The xcodes to run. When null it is looked up on every run.</param>
    /// <param name="environment">Extra environment variables; a null value removes the variable.</param>
    /// <param name="host">The program that provides the pseudo-terminal: BSD <c>script</c>, as macOS has it.</param>
    public XcodesTerminalRunner(
        string? executable = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        string host = DefaultHost)
    {
        _executable = executable;
        _environment = environment ?? new Dictionary<string, string?>();
        _host = host;
    }

    /// <param name="prompts">Answers the questions xcodes asks. When null, a question fails the command.</param>
    /// <param name="output">Receives each line as it is written.</param>
    /// <returns>The result, whose standard output is the log as a terminal would have shown it.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled, or a prompt was left unanswered.</exception>
    public async Task<XcodesResult> RunAsync(
        IReadOnlyList<string> arguments,
        XcodesPromptHandler? prompts = null,
        Action<XcodesOutputLine>? output = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsMacOS())
            throw new XcodesException("Xcode can only be installed on macOS.");
        string executable = _executable ?? XcodesLocator.Find()
            ?? throw new XcodesException("Could not find xcodes.");

        var startInfo = new ProcessStartInfo(_host)
        {
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-q");
        startInfo.ArgumentList.Add("/dev/null");
        startInfo.ArgumentList.Add(executable);
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        startInfo.ArgumentList.Add("--no-color");
        XcodesRunner.ApplyEnvironment(startInfo, _environment);
        string? terminal = startInfo.Environment.TryGetValue("TERM", out string? term) ? term : null;
        if (string.IsNullOrEmpty(terminal) || terminal == "dumb")
            startInfo.Environment["TERM"] = "xterm-256color";

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new XcodesException($"Could not start {_host}.");
        }
        catch (Win32Exception ex)
        {
            throw new XcodesException($"Could not start {_host}: {ex.Message}", ex);
        }

        using (process)
        {
            // The command ends the moment this is cancelled; reading the terminal then runs out.
            using CancellationTokenRegistration killOnCancel = cancellationToken.Register(() => XcodesRunner.Kill(process));
            try
            {
                return await ReadAsync(process, prompts, output, cancellationToken);
            }
            finally
            {
                // Disposing a Process leaves it running, which an exception out of here must not.
                if (!HasExited(process))
                    XcodesRunner.Kill(process);
            }
        }
    }

    private static async Task<XcodesResult> ReadAsync(
        Process process,
        XcodesPromptHandler? prompts,
        Action<XcodesOutputLine>? output,
        CancellationToken cancellationToken)
    {
        process.StandardInput.AutoFlush = true;
        Task<string> errors = process.StandardError.ReadToEndAsync(CancellationToken.None);

        var decoder = new XcodesTerminalDecoder();
        List<string> log = [];
        List<string> context = [];
        string? echo = null; // The answer just given, which a terminal prints back for questions it does not hide.

        void Emit(XcodesOutputLine line)
        {
            if (echo is not null)
            {
                string text = line.Text.Trim();
                if (text == echo)
                {
                    echo = null;
                    return;
                }
                if (text.Length > 0)
                    echo = null;
            }

            // Whatever is kept or shown from here on never names the Apple ID.
            line = line with { Text = XcodesPrompts.Redact(line.Text) };
            if (line.ReplacesPrevious && log.Count > 0)
                log.RemoveAt(log.Count - 1);
            log.Add(line.Text);

            context.Add(line.Text);
            if (context.Count > MaximumContextLines)
                context.RemoveAt(0);
            output?.Invoke(line);
        }

        char[] buffer = new char[4096];
        Task<int>? read = null;
        while (true)
        {
            read ??= ReadChunkAsync(process.StandardOutput, buffer);

            XcodesPromptKind? kind = decoder.HasPending ? XcodesPrompts.Detect(decoder.Pending) : null;
            if (kind is not null)
            {
                using var quiet = new CancellationTokenSource();
                Task settled = Task.Delay(
                    kind == XcodesPromptKind.Unknown ? UnknownPromptSettleTime : KnownPromptSettleTime,
                    quiet.Token);
                if (await Task.WhenAny(read, settled) == settled)
                {
                    string message = XcodesPrompts.Redact(decoder.Pending.Trim());
                    decoder.ClearPending();
                    string[] before = [.. context];
                    context.Clear();
                    Emit(new XcodesOutputLine(message));
                    context.Clear();

                    var prompt = new XcodesPrompt(kind.Value, message, before);
                    string answer = await AskAsync(process, prompts, prompt, cancellationToken);
                    // A password is not echoed back by the terminal, and is not kept either.
                    echo = !prompt.IsSecret && answer.Trim().Length > 0 ? answer.Trim() : null;
                    continue;
                }
                quiet.Cancel();
            }

            int count = await read;
            read = null;
            if (count == 0)
                break;
            foreach (XcodesOutputLine line in decoder.Feed(new string(buffer, 0, count)))
                Emit(line);
        }

        if (decoder.Flush() is { } rest)
            Emit(rest);

        await process.WaitForExitAsync(CancellationToken.None);
        cancellationToken.ThrowIfCancellationRequested();
        await Task.WhenAny(errors, Task.Delay(DrainGrace, CancellationToken.None));
        return new XcodesResult(
            process.ExitCode,
            string.Join('\n', log),
            errors.IsCompletedSuccessfully ? errors.Result : "");
    }

    private static async Task<int> ReadChunkAsync(StreamReader reader, char[] buffer)
    {
        try
        {
            return await reader.ReadAsync(buffer.AsMemory(), CancellationToken.None);
        }
        catch (IOException)
        {
            return 0; // The process is gone, and took its pipe with it.
        }
        catch (ObjectDisposedException)
        {
            return 0;
        }
    }

    private static async Task<string> AskAsync(
        Process process,
        XcodesPromptHandler? prompts,
        XcodesPrompt prompt,
        CancellationToken cancellationToken)
    {
        if (prompts is null)
        {
            XcodesRunner.Kill(process);
            throw new XcodesException($"xcodes is waiting for input: {prompt.Message}");
        }

        string? answer = await prompts(prompt, cancellationToken);
        if (answer is null)
        {
            XcodesRunner.Kill(process);
            throw new OperationCanceledException("The question was not answered.");
        }

        // An answer is one line: a line break in it would answer the next question too.
        answer = answer.Replace("\r", "").Replace("\n", "");
        try
        {
            await process.StandardInput.WriteLineAsync(answer.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        catch (IOException ex)
        {
            // xcodes ended while the question was open; the output loop reports how.
            Logger.Debug($"xcodes closed its input before the answer was written: {ex.Message}");
        }
        return answer;
    }

    private static bool HasExited(Process process)
    {
        try { return process.HasExited; }
        catch (InvalidOperationException) { return true; }
    }
}
