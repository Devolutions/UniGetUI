using System.Diagnostics;
using System.Text;

namespace UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;

/// <summary>What a command-line tool returned: its exit code, raw standard output and error text.</summary>
internal sealed record ToolResult(int ExitCode, byte[] StandardOutput, string StandardError)
{
    public string Text => Encoding.UTF8.GetString(StandardOutput);

    /// <summary>Standard output followed by standard error, as a person reading a log expects.</summary>
    public string Output => Text + StandardError;
}

/// <summary>
/// Runs the macOS tools this manager drives (pkgutil, hdiutil, plutil, sips, spctl, installer).
/// Both pipes are drained while the tool runs, so a chatty tool can never stall on a full pipe.
/// </summary>
internal static class Tool
{
    public static ToolResult Run(string file, IReadOnlyList<string> arguments, byte[]? input = null,
        TimeSpan? timeout = null, Action<Process>? started = null) =>
        RunAsync(file, arguments, input, timeout, started, CancellationToken.None).GetAwaiter().GetResult();

    public static async Task<ToolResult> RunAsync(string file, IReadOnlyList<string> arguments, byte[]? input = null,
        TimeSpan? timeout = null, Action<Process>? started = null, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(file)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidDataException($"Could not start {file}.");
        started?.Invoke(process);

        using var output = new MemoryStream();
        var reading = process.StandardOutput.BaseStream.CopyToAsync(output, CancellationToken.None);
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            if (input is not null)
                await process.StandardInput.BaseStream.WriteAsync(input, CancellationToken.None);
            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // The tool exited without reading all of its input; its exit code says why.
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } duration)
            limit.CancelAfter(duration);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new InvalidDataException($"{Path.GetFileName(file)} did not finish within {timeout}.");
        }
        await Task.WhenAll(reading, errors);
        return new ToolResult(process.ExitCode, output.ToArray(), errors.Result);
    }
}
