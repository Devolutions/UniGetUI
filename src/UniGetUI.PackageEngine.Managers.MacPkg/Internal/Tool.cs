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
        // On the thread pool, so a caller's synchronization context (the UI thread) can never deadlock it.
        Task.Run(() => RunAsync(file, arguments, input, timeout, started, CancellationToken.None)).GetAwaiter().GetResult();

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

        // One deadline covers the whole exchange: writing the input, waiting, and draining both pipes.
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout is { } duration)
            limit.CancelAfter(duration);
        var output = new Drain(process.StandardOutput.BaseStream);
        var errors = new Drain(process.StandardError.BaseStream);
        try
        {
            try
            {
                if (input is not null)
                    await process.StandardInput.BaseStream.WriteAsync(input, limit.Token);
                process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The tool exited without reading all of its input; its exit code says why.
            }
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            // Disposing a Process leaves it running: stop it, and let it exit, before going on.
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { } // It has just exited.
            await process.WaitForExitAsync(CancellationToken.None);
            await Drain.SettleAsync(output, errors, DrainGrace);
            if (cancellationToken.IsCancellationRequested)
                throw;
            throw new InvalidDataException($"{Path.GetFileName(file)} did not finish within {timeout}.");
        }
        // A process the tool left running can hold its pipes open: what arrived in time is what it said.
        await Drain.SettleAsync(output, errors, DrainGrace);
        return new ToolResult(process.ExitCode, output.Bytes, Encoding.UTF8.GetString(errors.Bytes));
    }

    /// <summary>How long to wait for the pipes once the tool has exited. Replaceable for tests.</summary>
    internal static TimeSpan DrainGrace { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Reads a pipe to its end in the background; what it read so far can be taken at any time.</summary>
    private sealed class Drain
    {
        private readonly MemoryStream _buffer = new();
        private readonly Task _reading;

        public Drain(Stream pipe) => _reading = ReadAsync(pipe);

        public byte[] Bytes
        {
            get { lock (_buffer) return _buffer.ToArray(); }
        }

        private async Task ReadAsync(Stream pipe)
        {
            byte[] chunk = new byte[81920];
            try
            {
                int read;
                while ((read = await pipe.ReadAsync(chunk)) > 0)
                    lock (_buffer) _buffer.Write(chunk, 0, read);
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
        }

        public static Task SettleAsync(Drain output, Drain errors, TimeSpan grace) =>
            Task.WhenAny(Task.WhenAll(output._reading, errors._reading), Task.Delay(grace));
    }
}
