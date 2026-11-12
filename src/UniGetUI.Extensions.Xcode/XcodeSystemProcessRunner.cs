using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace UniGetUI.Extensions.Xcode;

/// <summary>Runs a long-lived system tool without a terminal, retaining only bounded, redacted output.</summary>
internal static class XcodeSystemProcessRunner
{
    private const int MaximumLineLength = XcodesOutputLog.MaximumLineLength;
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(5);

    internal static async Task<XcodesResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        IReadOnlyDictionary<string, string?> environment,
        Action<XcodesOutputLine>? output,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
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
        XcodesRunner.ApplyEnvironment(startInfo, environment);

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
        using (var readCancellation = new CancellationTokenSource())
        {
            process.StandardInput.Close();
            var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var readFailure = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
            var forwarder = new OutputForwarder(output);
            using CancellationTokenRegistration registration = cancellationToken.Register(() =>
            {
                XcodesRunner.Kill(process);
                stopped.TrySetResult();
            });
            Task<string> stdout = ReadAsync(process.StandardOutput, forwarder, false, readFailure, readCancellation.Token);
            Task<string> stderr = ReadAsync(process.StandardError, forwarder, true, readFailure, readCancellation.Token);
            Task exited = process.WaitForExitAsync(CancellationToken.None);
            try
            {
                Task first = await Task.WhenAny(exited, stopped.Task, readFailure.Task).ConfigureAwait(false);
                if (first == stopped.Task || cancellationToken.IsCancellationRequested)
                {
                    XcodesRunner.Kill(process);
                    await WaitForExitAsync(exited).ConfigureAwait(false);
                    throw new OperationCanceledException(cancellationToken);
                }
                if (first == readFailure.Task)
                {
                    XcodesRunner.Kill(process);
                    await WaitForExitAsync(exited).ConfigureAwait(false);
                    throw new XcodesException($"Could not read output from {Path.GetFileName(executable)}.",
                        await readFailure.Task.ConfigureAwait(false));
                }

                await exited.ConfigureAwait(false);
                Task drains = Task.WhenAll(stdout, stderr);
                if (await Task.WhenAny(drains, Task.Delay(ExitGrace, cancellationToken)).ConfigureAwait(false) != drains)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    throw new XcodesException($"{Path.GetFileName(executable)} exited without closing its output. "
                        + "The system may still be processing the runtime.");
                }
                try
                {
                    await drains.ConfigureAwait(false);
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested && readFailure.Task.IsCompletedSuccessfully)
                {
                    throw new XcodesException($"Could not read output from {Path.GetFileName(executable)}.",
                        await readFailure.Task.ConfigureAwait(false));
                }
                cancellationToken.ThrowIfCancellationRequested();
                return new XcodesResult(process.ExitCode, stdout.Result, stderr.Result);
            }
            finally
            {
                readCancellation.Cancel();
                if (!exited.IsCompleted)
                    XcodesRunner.Kill(process);
                try
                {
                    await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (readCancellation.IsCancellationRequested) { }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
                catch (Exception) when (readFailure.Task.IsCompleted) { }
            }
        }
    }

    private static async Task WaitForExitAsync(Task exited)
        => await Task.WhenAny(exited, Task.Delay(ExitGrace)).ConfigureAwait(false);

    private static async Task<string> ReadAsync(
        StreamReader reader,
        OutputForwarder forwarder,
        bool isStandardError,
        TaskCompletionSource<Exception> readFailure,
        CancellationToken cancellationToken)
    {
        var redactor = new XcodesOutputRedactor(null);
        var line = new StringBuilder();
        var retained = new XcodesOutputLog();
        bool overflow = false;
        bool replacesPrevious = false;

        void Emit()
        {
            if (line.Length == 0 && !overflow)
                return;
            string text = overflow ? XcodesOutputLog.LongLineOmitted : redactor.ForError(line.ToString());
            line.Clear();
            overflow = false;
            if (text.Length == 0)
                return;
            text = XcodesOutputLog.LimitLine(text);
            retained.Add(new XcodesOutputLine(text, replacesPrevious));
            forwarder.Emit(text, replacesPrevious, isStandardError);
        }

        try
        {
            char[] buffer = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                for (int i = 0; i < count; i++)
                {
                    char character = buffer[i];
                    if (character is '\r' or '\n')
                    {
                        Emit();
                        replacesPrevious = character == '\r';
                    }
                    else if (!overflow)
                    {
                        if (line.Length == MaximumLineLength)
                        {
                            line.Clear();
                            overflow = true;
                        }
                        else
                            line.Append(character);
                    }
                }

            }
            Emit();
            return retained.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            readFailure.TrySetResult(ex);
            throw;
        }
    }

    private sealed class OutputForwarder(Action<XcodesOutputLine>? output)
    {
        private readonly object _lock = new();
        private bool? _lastWasStandardError;

        public void Emit(string text, bool replacesPrevious, bool isStandardError)
        {
            lock (_lock)
            {
                output?.Invoke(new XcodesOutputLine(text, replacesPrevious && _lastWasStandardError == isStandardError));
                _lastWasStandardError = isStandardError;
            }
        }
    }
}
