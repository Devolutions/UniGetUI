using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using UniGetUI.Core.Logging;

namespace UniGetUI.Extensions.Xcode;

/// <summary>What one command returned: its exit code and the text it wrote to each pipe.</summary>
public sealed record XcodesResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;

    /// <summary>Standard output followed by standard error, as a person reading a log expects.</summary>
    public string Output => string.Join('\n',
        new[] { StandardOutput, StandardError }.Select(text => text.Trim()).Where(text => text.Length > 0));
}

/// <summary>Thrown when an Xcode operation fails, with a message that can be shown to the user.</summary>
public sealed class XcodesException(string message, Exception? innerException = null)
    : Exception(message, innerException)
{
    /// <summary>Apple refused the account: signing in again, or with another Apple ID, may fix it.</summary>
    public bool IsAccessFailure { get; init; }

    /// <summary>Apple's developer download service reported that its session expired (result 1100).</summary>
    internal bool IsAdcSessionExpired { get; init; }
}

/// <summary>
/// Runs a command that never asks anything. Nothing can answer a question on these pipes, so standard input is
/// closed at once, and a command that outlives its time limit is killed.
/// </summary>
public sealed class XcodesRunner
{
    internal const int MaximumOutputCharacters = 4 * 1024 * 1024;

    /// <summary>Long enough to download the list of releases on a slow connection.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How long to wait for the pipes once the command has exited, in case a child it started kept them open.</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(5);

    /// <summary>How long a killed command has to be gone before the run stops waiting for it.</summary>
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(5);

    private readonly string _executable;
    private readonly IReadOnlyDictionary<string, string?> _environment;

    /// <param name="executable">The command to run.</param>
    /// <param name="environment">Extra environment variables; a null value removes the variable.</param>
    public XcodesRunner(string executable, IReadOnlyDictionary<string, string?>? environment = null)
    {
        _executable = executable;
        _environment = environment ?? new Dictionary<string, string?>();
    }

    public async Task<XcodesResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        string executable = _executable;
        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        ApplyEnvironment(startInfo, _environment);

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new XcodesException($"Could not start {executable}.");
        }
        catch (Win32Exception ex)
        {
            throw new XcodesException($"Could not start {executable}: {ex.Message}", ex);
        }

        using (process)
        {
            process.StandardInput.Close();
            using var readCancellation = new CancellationTokenSource();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout ?? DefaultTimeout);
            var readFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<string> output = ReadBoundedAsync(process.StandardOutput, "standard output", readFailure, readCancellation.Token);
            Task<string> errors = ReadBoundedAsync(process.StandardError, "standard error", readFailure, readCancellation.Token);
            Task reads = Task.WhenAll(output, errors);
            Task exited = process.WaitForExitAsync(CancellationToken.None);
            Task expired = Task.Delay(Timeout.InfiniteTimeSpan, limit.Token);
            try
            {
                await Task.WhenAny(exited, readFailure.Task, expired).ConfigureAwait(false);
                if (limit.IsCancellationRequested)
                {
                    Kill(process);
                    await WaitForKilledAsync(process).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new XcodesException(
                        $"{Path.GetFileName(executable)} {string.Join(' ', arguments)} did not finish within {timeout ?? DefaultTimeout}.");
                }
                if (readFailure.Task.IsCompletedSuccessfully)
                {
                    Kill(process);
                    await WaitForKilledAsync(process).ConfigureAwait(false);
                    throw ReadFailure(await readFailure.Task.ConfigureAwait(false));
                }

                await exited.ConfigureAwait(false);
                Task drained = await Task.WhenAny(reads, readFailure.Task, Task.Delay(DrainGrace, limit.Token)).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (limit.IsCancellationRequested)
                    throw new XcodesException($"{Path.GetFileName(executable)} timed out while reading its output.");
                if (readFailure.Task.IsCompletedSuccessfully)
                    throw ReadFailure(await readFailure.Task.ConfigureAwait(false));
                if (drained != reads)
                    throw new XcodesException(
                        $"{Path.GetFileName(executable)} exited without closing its output; incomplete output was discarded.");

                await reads.ConfigureAwait(false);
                var redactor = new XcodesOutputRedactor(null);
                string standardOutput = await output.ConfigureAwait(false);
                string standardError = await errors.ConfigureAwait(false);
                return new XcodesResult(
                    process.ExitCode,
                    process.ExitCode == 0 ? standardOutput : redactor.ForError(standardOutput),
                    redactor.ForError(standardError));
            }
            finally
            {
                readCancellation.Cancel();
                if (!exited.IsCompleted)
                {
                    Kill(process);
                    await WaitForKilledAsync(process).ConfigureAwait(false);
                }
                try
                {
                    await reads.WaitAsync(KillGrace, CancellationToken.None).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    Logger.Warn($"{Path.GetFileName(executable)} output readers did not stop after cancellation.");
                    _ = reads.ContinueWith(task => _ = task.Exception, CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
                catch (OperationCanceledException) when (readCancellation.IsCancellationRequested) { }
                catch (IOException) when (readCancellation.IsCancellationRequested) { }
                catch (ObjectDisposedException) when (readCancellation.IsCancellationRequested) { }
                catch (Exception) when (readFailure.Task.IsCompletedSuccessfully) { }
            }
        }
    }

    public Task<XcodesResult> RunAsync(params string[] arguments)
        => RunAsync(arguments, null, CancellationToken.None);

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader, string pipe, TaskCompletionSource<Exception> readFailure, CancellationToken cancellationToken)
    {
        try
        {
            var text = new StringBuilder();
            char[] buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (count > MaximumOutputCharacters - text.Length)
                    throw new XcodesException($"The command exceeded the {MaximumOutputCharacters}-character limit for {pipe}.");
                text.Append(buffer, 0, count);
            }
            return text.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            readFailure.TrySetResult(ex);
            throw;
        }
    }

    private static XcodesException ReadFailure(Exception error)
        => error as XcodesException ?? new XcodesException("Could not read the complete output of the command.", error);

    /// <summary>Stops a process and everything it started.</summary>
    internal static void Kill(Process process) => Kill(() => process.Kill(entireProcessTree: true));

    /// <summary>
    /// Runs <paramref name="kill"/>, whose failures do not matter to whoever wants the process gone: it may
    /// have just exited, or a child it started as root cannot be signalled by this user.
    /// </summary>
    internal static void Kill(Action kill)
    {
        try
        {
            kill();
        }
        catch (InvalidOperationException) { } // It has just exited.
        catch (Win32Exception ex)
        {
            Logger.Warn($"Could not stop everything the command started: {ex.Message}");
        }
        catch (AggregateException ex)
        {
            Logger.Warn($"Could not stop everything the command started: {ex.Message}");
        }
    }

    /// <summary>Waits for a process that has been killed to be gone, for as long as that should take.</summary>
    private static async Task WaitForKilledAsync(Process process)
    {
        using var grace = new CancellationTokenSource(KillGrace);
        try
        {
            await process.WaitForExitAsync(grace.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Logger.Warn($"The command was still running {KillGrace.TotalSeconds:0} seconds after it was killed.");
        }
    }

    /// <summary>The environment every command gets: no colour, and whatever the caller overrides.</summary>
    internal static void ApplyEnvironment(ProcessStartInfo startInfo, IReadOnlyDictionary<string, string?> overrides)
    {
        startInfo.Environment["NO_COLOR"] = "1";
        foreach ((string name, string? value) in overrides)
        {
            if (value is null) startInfo.Environment.Remove(name);
            else startInfo.Environment[name] = value;
        }
    }
}
