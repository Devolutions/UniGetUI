using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Threading;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// Forwards command-line arguments from a second UniGetUI instance to the already-running
/// first instance via a named pipe, mirroring WinUI3's AppInstance.RedirectActivationToAsync().
///
/// The first instance calls <see cref="StartListener"/> immediately after acquiring the
/// single-instance mutex.  Any subsequent launch that cannot acquire the mutex calls
/// <see cref="TryForwardToFirstInstance"/> and exits.
/// </summary>
internal static class SingleInstanceRedirector
{
    // One pipe name per user session (the MainWindowIdentifier is a stable constant).
    private static readonly string PipeName = BuildPipeName();

    // On Unix the pipe is a domain socket at $TMPDIR/CoreFxPipe_<name>, capped at 104 chars (macOS
    // sun_path). The descriptive Windows name overflows that, so non-Windows uses a short stable hash.
    private static string BuildPipeName()
    {
        string name = $"UniGetUI_Pipe_{CoreData.MainWindowIdentifier}_{Environment.UserName}";
        if (OperatingSystem.IsWindows())
            return name;

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(name));
        return "UniGetUI_" + Convert.ToHexString(hash, 0, 6);
    }

    private static Thread? _listener;

    // The mutex proves a first instance exists, so keep probing this long before giving up.
    private static readonly TimeSpan ForwardBudget = TimeSpan.FromSeconds(5);
    // Bound each read so a client that connects but never sends EOF can't wedge the loop.
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Start a background listener thread.  When a second instance forwards its args,
    /// <paramref name="onArgsReceived"/> is invoked on the Avalonia UI thread.
    /// </summary>
    public static void StartListener(Action<string[]> onArgsReceived)
    {
        _listener = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    var pipe = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: NamedPipeServerStream.MaxAllowedServerInstances,
                        transmissionMode: PipeTransmissionMode.Byte,
                        options: PipeOptions.Asynchronous);

                    pipe.WaitForConnection();

                    // Read off-thread and immediately re-arm: a single slow/stuck client can
                    // never block the next accept nor "busy out" the pipe into timeouts.
                    HandleConnection(pipe, onArgsReceived);
                }
                catch (Exception ex) when (ex is not ThreadAbortException)
                {
                    // Keep the listener alive through errors (the pipe breaks on disconnect, etc.)
                    Logger.Warn($"SingleInstanceRedirector listener error: {ex.Message}");
                }
            }
        })
        {
            IsBackground = true,
            Name = "SingleInstancePipeListener",
        };

        _listener.Start();
    }

    private static void HandleConnection(NamedPipeServerStream pipe, Action<string[]> onArgsReceived)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                using (pipe)
                using (var reader = new StreamReader(pipe, Encoding.UTF8))
                using (var cts = new CancellationTokenSource(ReadTimeout))
                {
                    string payload = await reader.ReadToEndAsync(cts.Token);
                    // Args are newline-delimited.
                    var args = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    Dispatcher.UIThread.Post(() => onArgsReceived(args));
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"SingleInstanceRedirector read error: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Try to forward <paramref name="args"/> to the already-running first instance.
    /// </summary>
    /// <returns><c>true</c> if the message was delivered successfully.</returns>
    public static bool TryForwardToFirstInstance(string[] args)
    {
        var deadline = DateTime.UtcNow + ForwardBudget;
        Exception? last;
        do
        {
            try
            {
                using var pipe = new NamedPipeClientStream(
                    serverName: ".",
                    pipeName: PipeName,
                    direction: PipeDirection.Out);

                pipe.Connect(500);

                using var writer = new StreamWriter(pipe, Encoding.UTF8);
                // Newline-delimited args payload.
                writer.Write(string.Join('\n', args));
                writer.Flush();
                return true;
            }
            catch (Exception ex)
            {
                last = ex;
                Thread.Sleep(100);
            }
        } while (DateTime.UtcNow < deadline);

        Logger.Warn($"Could not forward args to first instance: {last?.Message}");
        return false;
    }
}
