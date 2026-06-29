using System.Diagnostics;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine;

namespace UniGetUI.Tui.Infrastructure;

/// <summary>
/// TUI-specific engine bootstrap.
///
/// This is intentionally NOT a trimmed <c>AvaloniaBootstrapper</c>: the desktop bootstrap wires up
/// the IPC API, telemetry, the icon database, the background auto-updater and integrity dialogs —
/// all of which are GUI-coupled and post to a desktop dispatcher. The TUI only needs the headless
/// engine sequence proven by <c>WinUiHeadlessHost</c>:
///   reload language engine → apply proxy → LoadLoaders() → LoadManagers() (+ best-effort elevator).
///
/// Optional services (telemetry, IPC, icon cache, auto-update) are deliberately left off / no-op in
/// the TUI; they can be opted into later behind explicit flags.
/// </summary>
internal static class TuiBootstrapper
{
    private static bool _started;

    /// <summary>
    /// Runs the engine bootstrap synchronously to completion. Call this BEFORE starting the
    /// Consolonia application so the first frame can render real manager state. Manager
    /// initialization is internally bounded by PEInterface's own load timeout.
    /// </summary>
    public static void Initialize()
    {
        if (_started) return;
        _started = true;

        Logger.Info("Starting UniGetUI TUI bootstrap");

        CoreTools.ReloadLanguageEngineInstance();
        ApplyProxySettingsToProcess();

        PEInterface.LoadLoaders();

        // Load the elevator in the background; it is only required once an operation runs (M3),
        // so it must never block first paint.
        _ = Task.Run(LoadElevatorAsync)
            .ContinueWith(
                t => Logger.Error(t.Exception!),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);

        // LoadManagers blocks until every manager has initialized or the engine's load timeout
        // elapses. Running it here (rather than in the background) means the shell's Managers page
        // shows fully-resolved state on first render.
        PEInterface.LoadManagers();

        Logger.Info("UniGetUI TUI bootstrap completed");
    }

    private static void ApplyProxySettingsToProcess()
    {
        try
        {
            var proxyUri = Settings.GetProxyUrl();
            if (proxyUri is null || !Settings.Get(Settings.K.EnableProxy))
            {
                Environment.SetEnvironmentVariable("HTTP_PROXY", "", EnvironmentVariableTarget.Process);
                return;
            }

            string content;
            if (!Settings.Get(Settings.K.EnableProxyAuth))
            {
                content = proxyUri.ToString();
            }
            else
            {
                var creds = Settings.GetProxyCredentials();
                content = creds is null
                    ? proxyUri.ToString()
                    : $"{proxyUri.Scheme}://{Uri.EscapeDataString(creds.UserName)}"
                      + $":{Uri.EscapeDataString(creds.Password)}"
                      + $"@{proxyUri.AbsoluteUri.Replace($"{proxyUri.Scheme}://", "")}";
            }

            Environment.SetEnvironmentVariable("HTTP_PROXY", content, EnvironmentVariableTarget.Process);
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to apply proxy settings:");
            Logger.Error(ex);
        }
    }

    private static async Task LoadElevatorAsync()
    {
        try
        {
            if (Settings.Get(Settings.K.ProhibitElevation))
            {
                Logger.Warn("Elevation is prohibited by settings; skipping elevator load.");
                return;
            }

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                var (sudoFound, sudoPath) = await CoreTools.WhichAsync("sudo");
                if (sudoFound)
                {
                    CoreData.ElevatorPath = sudoPath;
                    Logger.Debug($"Using sudo at {sudoPath} for elevation.");
                }
                else
                {
                    Logger.Warn("No 'sudo' found; elevated operations will fail.");
                }
                return;
            }

            if (SecureSettings.Get(SecureSettings.K.ForceUserGSudo))
            {
                var forced = await CoreTools.WhichAsync("gsudo.exe");
                if (forced.Item1)
                {
                    CoreData.ElevatorPath = forced.Item2;
                    Logger.Warn($"Using user GSudo (forced) at {CoreData.ElevatorPath}");
                    return;
                }
            }

            // The TUI is not (yet) shipping the bundled "UniGetUI Elevator"; fall back to a
            // gsudo.exe on PATH. Bundled-elevator resolution is a packaging concern for M7.
            var (found, path) = await CoreTools.WhichAsync("gsudo.exe");
            if (found)
            {
                CoreData.ElevatorPath = path;
                Logger.Debug($"Using gsudo.exe at {path} for elevation.");
            }
            else
            {
                Logger.Warn("No 'gsudo.exe' found on PATH; elevated operations will fail until packaged.");
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Elevator failed to load:");
            Logger.Error(ex);
        }
    }

    [Conditional("DEBUG")]
    public static void LogTimings(string phase, long elapsedMs)
        => Logger.Debug($"[TUI bootstrap] {phase}: {elapsedMs} ms");
}
