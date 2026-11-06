using System.Diagnostics;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;

namespace UniGetUI.PackageEngine.Managers.MacAppStoreManager.Internal;

/// <summary>
/// Runs mas as root through <c>sudo -A</c>, which asks for the password with a native macOS dialog
/// (the askpass script below) instead of a terminal.
/// mas needs root to install, update and uninstall. Under sudo it keeps talking to the App Store as
/// the user who ran it (it drops back to SUDO_UID), so purchases still go to that user's Apple account.
/// </summary>
internal static class ElevatedMas
{
    internal const string SudoPath = "/usr/bin/sudo";

    // sudo passes its prompt as the first argument and reads the password from standard output.
    // Cancelling the dialog prints nothing, which sudo reports as "no password was provided".
    internal const string AskpassScript =
        "#!/bin/sh\n"
        + "exec /usr/bin/osascript"
        + " -e 'on run argv'"
        + " -e 'activate'"
        + " -e 'text returned of (display dialog (item 1 of argv) default answer \"\" with hidden answer with title \"UniGetUI\" with icon caution)'"
        + " -e 'end run' -- \"$1\"\n";

    /// <summary>Starts a process and waits for it, streaming its output. Replaceable for tests.</summary>
    internal static Func<ProcessStartInfo, Action<string>, CancellationToken, Task<int>> RunProcess { get; set; } =
        RunAndStreamAsync;

    /// <summary>The command sudo runs, with a prompt it shows verbatim (sudo expands % sequences).</summary>
    internal static IReadOnlyList<string> SudoArguments(string prompt, string masPath, IEnumerable<string> masArguments) =>
        ["-A", "-p", prompt.Replace("%", "%%"), "--", masPath, .. masArguments];

    internal static string WriteAskpassScript()
    {
        string path = Path.Join(CoreData.UniGetUIDataDirectory, "mac-askpass.sh");
        // Rewritten before every use, so whatever sudo runs is this script.
        File.Delete(path);
        File.WriteAllText(path, AskpassScript);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    /// <summary>Runs mas through sudo; returns its exit code and everything it printed.</summary>
    public static async Task<(int ExitCode, IReadOnlyList<string> Output)> RunAsync(
        string masPath,
        IEnumerable<string> masArguments,
        string prompt,
        Action<string> log,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new(SudoPath)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };
        foreach (string argument in SudoArguments(prompt, masPath, masArguments))
            startInfo.ArgumentList.Add(argument);
        startInfo.Environment["SUDO_ASKPASS"] = WriteAskpassScript();

        List<string> output = [];
        int exitCode = await RunProcess(startInfo, line =>
        {
            lock (output)
                output.Add(line);
            log(line);
        }, cancellationToken);
        lock (output)
            return (exitCode, output.ToArray());
    }

    private static async Task<int> RunAndStreamAsync(
        ProcessStartInfo startInfo,
        Action<string> onLine,
        CancellationToken cancellationToken)
    {
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {startInfo.FileName}.");
        process.StandardInput.Close();
        Task stdout = PumpAsync(process.StandardOutput, onLine);
        Task stderr = PumpAsync(process.StandardError, onLine);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await StopAsync(process);
            throw;
        }
        await Task.WhenAny(Task.WhenAll(stdout, stderr), Task.Delay(TimeSpan.FromSeconds(10), CancellationToken.None));
        return process.ExitCode;
    }

    // sudo runs mas as root, so this user cannot kill mas itself; sudo relays a SIGTERM to it instead.
    private static async Task StopAsync(Process process)
    {
        try
        {
            ProcessStartInfo signal = new("/bin/kill") { UseShellExecute = false, CreateNoWindow = true };
            signal.ArgumentList.Add("-TERM");
            signal.ArgumentList.Add(process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            using Process? kill = Process.Start(signal);
            if (kill is not null)
                await kill.WaitForExitAsync(CancellationToken.None);
            using CancellationTokenSource grace = new(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(grace.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) { Logger.Warn($"Could not stop mas after cancelling: {ex.Message}"); }
        }
        catch (InvalidOperationException) { } // It has already exited.
    }

    private static async Task PumpAsync(StreamReader reader, Action<string> onLine)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync()) is not null)
                if (line.Trim().Length > 0)
                    onLine(line);
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }
}
