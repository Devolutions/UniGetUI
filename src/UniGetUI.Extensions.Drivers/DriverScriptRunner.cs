using System.Diagnostics;
using System.Text;
using UniGetUI.Core.Logging;

namespace UniGetUI.Extensions.Drivers;

public sealed record ScriptOutput(int ExitCode, string StandardOutput, string StandardError);

public sealed record DriversElevation(
    bool IsAdministrator,
    bool IsProhibited,
    string ElevatorPath,
    IReadOnlyList<string> ElevatorArguments,
    Action? BeforePrompt = null);

public interface IDriverScriptRunner
{
    Task<ScriptOutput> RunAsync(string script, bool elevated, CancellationToken cancellationToken);
}

public sealed class PowerShellDriverScriptRunner(Func<DriversElevation> elevation) : IDriverScriptRunner
{
    internal const int ErrorCancelled = 1223;
    internal const int ElevatorCancelled = 999;
    internal const string ElevatorCancelledByUser = "The operation was canceled by the user";

    internal static string PowerShellPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    public async Task<ScriptOutput> RunAsync(string script, bool elevated, CancellationToken cancellationToken)
    {
        DriversElevation? context = elevated ? elevation() : null;
        if (context is { IsProhibited: true })
            throw new DriversException(DriversErrorKind.ElevationProhibited, "Elevation is prohibited by the settings.");
        bool throughElevator = context is { IsAdministrator: false };
        ProcessStartInfo startInfo = BuildStartInfo(throughElevator ? context : null);

        if (throughElevator) context!.BeforePrompt?.Invoke();

        Process process;
        try
        {
            process = Process.Start(startInfo) ?? throw new DriversException(DriversErrorKind.NotSupported, "PowerShell could not be started.");
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new DriversException(DriversErrorKind.NotSupported, $"PowerShell could not be started: {ex.Message}", ex.NativeErrorCode, ex);
        }

        using (process)
        {
            Task<string> stdout = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            Task<string> stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);
            try
            {
                await process.StandardInput.WriteAsync(script);
                process.StandardInput.Close();
            }
            catch (IOException ex)
            {
                Logger.Warn($"The driver script could not be sent to PowerShell: {ex.Message}");
            }
            try
            {
                await process.WaitForExitAsync(throughElevator ? CancellationToken.None : cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
                throw;
            }

            string output = await stdout;
            string error = await stderr;
            if (error.Length > 0)
                Logger.Warn($"Driver script wrote to standard error: {error.Trim()}");
            if (throughElevator && IsElevationCancelled(process.ExitCode, output, error))
                throw new DriversException(DriversErrorKind.ElevationCancelled, "The administrator prompt was cancelled.", ErrorCancelled);
            return new ScriptOutput(process.ExitCode, output, error);
        }
    }

    internal static ProcessStartInfo BuildStartInfo(DriversElevation? elevator)
    {
        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        if (elevator is null)
        {
            startInfo.FileName = PowerShellPath;
        }
        else
        {
            if (elevator.IsProhibited)
                throw new DriversException(DriversErrorKind.ElevationProhibited, "Elevation is prohibited by the settings.");
            if (elevator.ElevatorPath.Length == 0)
                throw new DriversException(DriversErrorKind.NoElevator, "No elevator is available to install drivers.");
            startInfo.FileName = elevator.ElevatorPath;
            foreach (string argument in elevator.ElevatorArguments)
                startInfo.ArgumentList.Add(argument);
            startInfo.ArgumentList.Add(PowerShellPath);
        }

        foreach (string argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand", DriverScripts.EncodedStandardInputLoader })
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    internal static bool IsElevationCancelled(int exitCode, string output, string error)
    {
        if (output.Contains(DriverScripts.OutputMarker, StringComparison.Ordinal)) return false;
        if (exitCode == ErrorCancelled) return true;
        return exitCode == ElevatorCancelled
            && (output + error).Contains(ElevatorCancelledByUser, StringComparison.OrdinalIgnoreCase);
    }
}
