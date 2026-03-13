using System.IO.Pipes;
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
    private static readonly string PipeName =
        $"UniGetUI_Pipe_{CoreData.MainWindowIdentifier}_{Environment.UserName}";

    private static Thread? _listener;

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
                    using var pipe = new NamedPipeServerStream(
                        PipeName,
                        PipeDirection.In,
                        maxNumberOfServerInstances: 1,
                        transmissionMode: PipeTransmissionMode.Byte);

                    pipe.WaitForConnection();

                    using var reader = new StreamReader(pipe, Encoding.UTF8);
                    string payload = reader.ReadToEnd();

                    // Args are newline-delimited.
                    var args = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    Dispatcher.UIThread.Post(() => onArgsReceived(args));
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

    /// <summary>
    /// Try to forward <paramref name="args"/> to the already-running first instance.
    /// </summary>
    /// <returns><c>true</c> if the message was delivered successfully.</returns>
    public static bool TryForwardToFirstInstance(string[] args)
    {
        if (args.Length == 0)
        {
            // Nothing to forward — still show the window by sending an empty payload.
        }

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
            Logger.Warn($"Could not forward args to first instance: {ex.Message}");
            return false;
        }
    }
}
