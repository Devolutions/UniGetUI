using System.Threading;
using Avalonia;
#if AVALONIA_DIAGNOSTICS_ENABLED
using AvaloniaUI.DiagnosticsProtocol;
#endif
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia;

internal sealed class Program
{
    private const string DevToolsEnvVar = "UNIGETUI_AVALONIA_DEVTOOLS";

    private enum DevToolsRuntimeMode
    {
        Auto,
        Enabled,
        Disabled,
    }

    // Kept alive for the lifetime of the process to enforce single-instance
    // ReSharper disable once NotAccessedField.Local
    private static Mutex? _singleInstanceMutex;

    private static DevToolsRuntimeMode _devToolsRuntimeMode = DevToolsRuntimeMode.Auto;
    private static string _devToolsRuntimeModeSource = "default";

    [STAThread]
    public static void Main(string[] args)
    {
        // ── Pre-UI headless CLI dispatch (A1) ──────────────────────────────
        // These commands operate purely on settings/files and exit without
        // showing any UI.  They mirror WinUI's EntryPoint.cs dispatch block.
        if (args.Contains("--import-settings"))
        {
            Environment.Exit(RunCli_ImportSettings(args));
            return;
        }
        if (args.Contains("--export-settings"))
        {
            Environment.Exit(RunCli_ExportSettings(args));
            return;
        }
        if (args.Contains("--enable-setting"))
        {
            Environment.Exit(RunCli_EnableSetting(args));
            return;
        }
        if (args.Contains("--disable-setting"))
        {
            Environment.Exit(RunCli_DisableSetting(args));
            return;
        }
        if (args.Contains("--set-setting-value"))
        {
            Environment.Exit(RunCli_SetSettingValue(args));
            return;
        }

        // ── A6: WinGetUI→UniGetUI shortcut migration (called by installer) ──
        if (args.Contains("--migrate-wingetui-to-unigetui"))
        {
            Environment.Exit(RunCli_MigrateWingetUI());
            return;
        }

        // ── Stub: prevents re-launch during MSI/MSIX uninstall ────────────
        if (args.Contains("--uninstall-unigetui") || args.Contains("--uninstall-wingetui"))
        {
            Environment.Exit(0);
            return;
        }

        (_devToolsRuntimeMode, _devToolsRuntimeModeSource) = ResolveDevToolsRuntimeMode(args);
        Logger.Info(
            $"Avalonia DevTools runtime mode: {_devToolsRuntimeMode} (source: {_devToolsRuntimeModeSource})");

        // ── Single-instance enforcement ────────────────────────────────────
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: "UniGetUI_" + CoreData.MainWindowIdentifier,
            createdNew: out bool createdNew);

        if (!createdNew)
        {
            // Forward args to the first instance then exit (mirrors WinUI3's AppInstance.RedirectActivationToAsync).
            Logger.Warn("UniGetUI is already running. Forwarding args to first instance.");
            SingleInstanceRedirector.TryForwardToFirstInstance(args);
            _singleInstanceMutex.Close();
            _singleInstanceMutex = null;
            return;
        }

        // Start the pipe listener so future second instances can forward their args.
        SingleInstanceRedirector.StartListener(OnIncomingArgs);

        // Register global exception handlers for logging and crash reporting
        RegisterErrorHandling();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // A7: top-level crash handler
            ReportFatalException(ex);
        }
        finally
        {
            _singleInstanceMutex.ReleaseMutex();
            _singleInstanceMutex.Dispose();
            _singleInstanceMutex = null;
        }
    }

    // ── Second-instance arg handler ──────────────────────────────────────

    private static void OnIncomingArgs(string[] incomingArgs)
    {
        // Show the main window.
        MainWindow.Instance?.ShowFromTray();

        // Route the forwarded arguments through the shell's arg processor.
        if (MainWindow.Instance?.Content is Views.MainShellView shell)
            shell.ProcessIncomingArgs(incomingArgs);
    }

    // ── Headless CLI helpers ───────────────────────────────────────────────

    private static int RunCli_ImportSettings(string[] args)
    {
        int pos = Array.IndexOf(args, "--import-settings");
        if (pos < 0 || pos + 1 >= args.Length) return -1073741811; // STATUS_INVALID_PARAMETER
        string file = args[pos + 1].Trim('"').Trim('\'');
        if (!File.Exists(file)) return -1073741809; // STATUS_NO_SUCH_FILE
        try { Settings.ImportFromFile_JSON(file); return 0; }
        catch (Exception ex) { return ex.HResult; }
    }

    private static int RunCli_ExportSettings(string[] args)
    {
        int pos = Array.IndexOf(args, "--export-settings");
        if (pos < 0 || pos + 1 >= args.Length) return -1073741811;
        string file = args[pos + 1].Trim('"').Trim('\'');
        try { Settings.ExportToFile_JSON(file); return 0; }
        catch (Exception ex) { return ex.HResult; }
    }

    private static int RunCli_EnableSetting(string[] args)
    {
        int pos = Array.IndexOf(args, "--enable-setting");
        if (pos < 0 || pos + 1 >= args.Length) return -1073741811;
        string name = args[pos + 1].Trim('"').Trim('\'');
        if (!Enum.TryParse(name, out Settings.K key)) return -2; // STATUS_UNKNOWN_SETTINGS_KEY
        try { Settings.Set(key, true); return 0; }
        catch (Exception ex) { return ex.HResult; }
    }

    private static int RunCli_DisableSetting(string[] args)
    {
        int pos = Array.IndexOf(args, "--disable-setting");
        if (pos < 0 || pos + 1 >= args.Length) return -1073741811;
        string name = args[pos + 1].Trim('"').Trim('\'');
        if (!Enum.TryParse(name, out Settings.K key)) return -2;
        try { Settings.Set(key, false); return 0; }
        catch (Exception ex) { return ex.HResult; }
    }

    private static int RunCli_SetSettingValue(string[] args)
    {
        int pos = Array.IndexOf(args, "--set-setting-value");
        if (pos < 0 || pos + 2 >= args.Length) return -1073741811;
        string name = args[pos + 1].Trim('"').Trim('\'');
        string value = args[pos + 2];
        if (!Enum.TryParse(name, out Settings.K key)) return -2;
        try { Settings.SetValue(key, value); return 0; }
        catch (Exception ex) { return ex.HResult; }
    }

    // ── A6: WinGetUI→UniGetUI shortcut migrator ────────────────────────────

    private static int RunCli_MigrateWingetUI()
    {
        try
        {
            string[] basePaths =
            [
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            ];

            foreach (string basePath in basePaths)
            {
                foreach (string oldName in new[]
                {
                    "WingetUI.lnk",
                    "WingetUI .lnk",
                    "UniGetUI (formerly WingetUI) .lnk",
                    "UniGetUI (formerly WingetUI).lnk",
                })
                {
                    try
                    {
                        string oldFile = Path.Join(basePath, oldName);
                        string newFile = Path.Join(basePath, "UniGetUI.lnk");
                        if (!File.Exists(oldFile))
                            continue;

                        if (File.Exists(newFile))
                        {
                            Logger.Info($"Deleting old shortcut '{oldFile}' (new one already exists)");
                            File.Delete(oldFile);
                        }
                        else
                        {
                            Logger.Info($"Renaming shortcut '{oldFile}' → '{newFile}'");
                            File.Move(oldFile, newFile);
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Warn($"Could not migrate shortcut '{Path.Join(basePath, oldName)}'");
                        Logger.Warn(ex);
                    }
                }
            }

            return 0;
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            return ex.HResult;
        }
    }

    // ── A7: crash reporter ─────────────────────────────────────────────────

    private static void RegisterErrorHandling()
    {
        // Log unobserved task exceptions and mark them as observed to
        // prevent .NET from terminating the process on GC finalization.
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.Error("Unobserved task exception:");
            Logger.Error(e.Exception);
            e.SetObserved();
        };

        // Log truly unhandled exceptions on any thread. These are fatal —
        // the process is about to terminate (e.IsTerminating == true).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                ReportFatalException(ex);
        };
    }

    private static void ReportFatalException(Exception ex)
    {
        try
        {
            // Write a crash log next to the settings directory
            string crashPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "UniGetUI",
                $"crash_{DateTime.UtcNow:yyyyMMdd_HHmmss}.log");
            Directory.CreateDirectory(Path.GetDirectoryName(crashPath)!);
            File.WriteAllText(crashPath,
                $"UniGetUI fatal crash at {DateTime.UtcNow:O}\n\n{ex}");
            Logger.Error("FATAL EXCEPTION — crash log written to: " + crashPath);
            Logger.Error(ex);
        }
        catch { /* best-effort */ }
    }

    private static (DevToolsRuntimeMode Mode, string Source) ResolveDevToolsRuntimeMode(
        string[] args)
    {
        if (TryGetDevToolsModeFromCli(args, out DevToolsRuntimeMode cliMode))
        {
            return (cliMode, "cli");
        }

        string? envValue = Environment.GetEnvironmentVariable(DevToolsEnvVar);
        if (TryParseDevToolsRuntimeMode(envValue, out DevToolsRuntimeMode envMode))
        {
            return (envMode, "env");
        }

        if (!string.IsNullOrWhiteSpace(envValue))
        {
            Logger.Warn(
                $"Ignoring invalid {DevToolsEnvVar} value '{envValue}'. Expected one of: auto, on, off, true, false, 1, 0.");
        }

        return (DevToolsRuntimeMode.Auto, "default");
    }

    private static bool TryGetDevToolsModeFromCli(
        IEnumerable<string> args,
        out DevToolsRuntimeMode mode)
    {
        bool found = false;
        mode = DevToolsRuntimeMode.Auto;

        foreach (string rawArg in args)
        {
            string arg = rawArg.Trim();

            if (arg.Equals("--enable-devtools", StringComparison.OrdinalIgnoreCase))
            {
                mode = DevToolsRuntimeMode.Enabled;
                found = true;
                continue;
            }

            if (arg.Equals("--disable-devtools", StringComparison.OrdinalIgnoreCase))
            {
                mode = DevToolsRuntimeMode.Disabled;
                found = true;
                continue;
            }

            const string modePrefix = "--devtools-mode=";
            if (!arg.StartsWith(modePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string modeValue = arg[modePrefix.Length..].Trim();
            if (TryParseDevToolsRuntimeMode(modeValue, out DevToolsRuntimeMode parsedMode))
            {
                mode = parsedMode;
                found = true;
            }
            else
            {
                Logger.Warn(
                    $"Ignoring invalid --devtools-mode value '{modeValue}'. Expected one of: auto, enabled, disabled.");
            }
        }

        return found;
    }

    private static bool TryParseDevToolsRuntimeMode(
        string? value,
        out DevToolsRuntimeMode mode)
    {
        mode = DevToolsRuntimeMode.Auto;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "auto":
                mode = DevToolsRuntimeMode.Auto;
                return true;
            case "enabled":
            case "enable":
            case "on":
            case "true":
            case "1":
                mode = DevToolsRuntimeMode.Enabled;
                return true;
            case "disabled":
            case "disable":
            case "off":
            case "false":
            case "0":
                mode = DevToolsRuntimeMode.Disabled;
                return true;
            default:
                return false;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

#if AVALONIA_DIAGNOSTICS_ENABLED
        bool isWsl = IsRunningInWsl();
        bool shouldEnableDevTools = _devToolsRuntimeMode switch
        {
            DevToolsRuntimeMode.Enabled => true,
            DevToolsRuntimeMode.Disabled => false,
            _ => !isWsl,
        };

        if (_devToolsRuntimeMode == DevToolsRuntimeMode.Auto && isWsl)
        {
            Logger.Warn("Avalonia DevTools auto mode disabled on WSL to avoid avdt runner crashes.");
        }

        if (_devToolsRuntimeMode == DevToolsRuntimeMode.Enabled && isWsl)
        {
            Logger.Warn("Avalonia DevTools explicitly enabled on WSL. This configuration may be unstable.");
        }

        if (shouldEnableDevTools)
        {
            builder = builder.WithDeveloperTools(options =>
            {
                options.ApplicationName = "UniGetUI.Avalonia";
                options.ConnectOnStartup = true;
                options.EnableDiscovery = true;
                options.DiagnosticLogger = DiagnosticLogger.CreateConsole();
            });
            Logger.Info(
                $"Avalonia DevTools enabled (mode: {_devToolsRuntimeMode}, source: {_devToolsRuntimeModeSource}).");
        }
        else
        {
            Logger.Info(
                $"Avalonia DevTools disabled (mode: {_devToolsRuntimeMode}, source: {_devToolsRuntimeModeSource}).");
        }
#else
        if (_devToolsRuntimeMode != DevToolsRuntimeMode.Auto)
        {
            Logger.Warn(
                "Avalonia DevTools runtime toggle was requested, but diagnostics support is not included in this build.");
        }
#endif

        return builder;
    }

#if AVALONIA_DIAGNOSTICS_ENABLED
    private static bool IsRunningInWsl()
    {
        if (!OperatingSystem.IsLinux())
            return false;

        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WSL_DISTRO_NAME")))
            return true;

        return !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WSL_INTEROP"));
    }
#endif
}
