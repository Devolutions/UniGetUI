using System.Diagnostics;
using System.Text;

namespace UniGetUI.Extensions.GitRepositories;

/// <summary>What one git command returned: its exit code and the text it wrote to each pipe.</summary>
public sealed record GitResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Success => ExitCode == 0;

    /// <summary>Standard output followed by standard error, as a person reading a log expects.</summary>
    public string Output => (StandardOutput + StandardError).Trim();
}

/// <summary>Thrown when git cannot be started, or does not finish within its time limit.</summary>
public sealed class GitException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Runs the git executable. Git is never allowed to prompt on a terminal (there is none to answer it),
/// both pipes are drained while it runs, and a command that outlives its time limit is killed.
/// </summary>
public sealed class GitRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    /// <summary>How long to wait for the pipes once git has exited. A credential-cache daemon that
    /// git started can inherit them and keep them open long after git itself is gone.</summary>
    private static readonly TimeSpan DrainGrace = TimeSpan.FromSeconds(5);

    private readonly string _executable;
    private readonly IReadOnlyDictionary<string, string?> _environment;

    /// <param name="executable">The git executable, resolved through PATH when it is a bare name.</param>
    /// <param name="environment">Extra environment variables; a null value removes the variable.</param>
    public GitRunner(string executable = "git", IReadOnlyDictionary<string, string?>? environment = null)
    {
        _executable = executable;
        _environment = environment ?? new Dictionary<string, string?>();
    }

    public async Task<GitResult> RunAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(_executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
        };
        // Paths come back verbatim rather than octal-escaped, so they can be compared and opened.
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("core.quotePath=false");
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach ((string name, string? value) in _environment)
        {
            if (value is null) startInfo.Environment.Remove(name);
            else startInfo.Environment[name] = value;
        }

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new GitException($"Could not start {_executable}.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new GitException($"Could not start {_executable}: {ex.Message}", ex);
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
                throw new GitException(
                    $"git {string.Join(' ', arguments)} did not finish within {timeout ?? DefaultTimeout}.");
            }

            await Task.WhenAny(Task.WhenAll(output, errors), Task.Delay(DrainGrace, CancellationToken.None));
            return new GitResult(
                process.ExitCode,
                output.IsCompletedSuccessfully ? output.Result : "",
                errors.IsCompletedSuccessfully ? errors.Result : "");
        }
    }

    public Task<GitResult> RunAsync(string workingDirectory, params string[] arguments)
        => RunAsync(workingDirectory, arguments, null, CancellationToken.None);
}
