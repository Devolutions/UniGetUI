using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace UniGetUI.Extensions.Xcode;

/// <summary>What one xcodes command returned: its exit code and the text it wrote to each pipe.</summary>
public sealed record XcodesResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;

    /// <summary>Standard output followed by standard error, as a person reading a log expects.</summary>
    public string Output => string.Join('\n',
        new[] { StandardOutput, StandardError }.Select(text => text.Trim()).Where(text => text.Length > 0));
}

/// <summary>Thrown when xcodes cannot be started, or does not finish within its time limit.</summary>
public sealed class XcodesException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Runs xcodes for the commands that never ask anything: listing, refreshing, uninstalling. Nothing can
/// answer a question on these pipes, so standard input is closed at once, and a command that outlives
/// its time limit is killed. Commands that may need an Apple ID or a password go through
/// <see cref="XcodesTerminalRunner"/> instead.
/// </summary>
public sealed class XcodesRunner
{
    /// <summary>Long enough to download the list of releases on a slow connection.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    /// <summary>How long to wait for the pipes once xcodes has exited, in case a child it started kept them open.</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(5);

    private readonly string? _executable;
    private readonly IReadOnlyDictionary<string, string?> _environment;

    /// <param name="executable">The xcodes to run. When null it is looked up on every run, so one installed
    /// while the app is open is used at once.</param>
    /// <param name="environment">Extra environment variables; a null value removes the variable.</param>
    public XcodesRunner(string? executable = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        _executable = executable;
        _environment = environment ?? new Dictionary<string, string?>();
    }

    public async Task<XcodesResult> RunAsync(
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        string executable = _executable ?? XcodesLocator.Find()
            ?? throw new XcodesException("Could not find xcodes.");
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
        startInfo.ArgumentList.Add("--no-color");
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
            Task<string> output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            Task<string> errors = process.StandardError.ReadToEndAsync(CancellationToken.None);

            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout ?? DefaultTimeout);
            try
            {
                await process.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                // Disposing a Process leaves it running: stop it, and let it exit, before going on.
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { } // It has just exited.
                await process.WaitForExitAsync(CancellationToken.None);
                if (cancellationToken.IsCancellationRequested)
                    throw;
                throw new XcodesException(
                    $"xcodes {string.Join(' ', arguments)} did not finish within {timeout ?? DefaultTimeout}.");
            }

            await Task.WhenAny(Task.WhenAll(output, errors), Task.Delay(DrainGrace, CancellationToken.None));
            return new XcodesResult(
                process.ExitCode,
                output.IsCompletedSuccessfully ? output.Result : "",
                errors.IsCompletedSuccessfully ? errors.Result : "");
        }
    }

    public Task<XcodesResult> RunAsync(params string[] arguments)
        => RunAsync(arguments, null, CancellationToken.None);

    /// <summary>The environment every xcodes run gets: no colour, and whatever the caller overrides.</summary>
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
