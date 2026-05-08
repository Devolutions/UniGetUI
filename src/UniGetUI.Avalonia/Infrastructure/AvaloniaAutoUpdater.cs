using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Threading;
using Microsoft.Win32;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// Avalonia port of the WinUI AutoUpdater.  Checks for new UniGetUI versions and
/// lets the user trigger an in-place upgrade.
/// </summary>
internal static partial class AvaloniaAutoUpdater
{
    // ------------------------------------------------------------------ constants
    private const string REGISTRY_PATH = @"Software\Devolutions\UniGetUI";
    private const string DEFAULT_PRODUCTINFO_URL = "https://devolutions.net/productinfo.json";
    private const string DEFAULT_PRODUCTINFO_KEY = "Devolutions.UniGetUI";

    private const string REG_PRODUCTINFO_URL = "UpdaterProductInfoUrl";
    private const string REG_PRODUCTINFO_KEY = "UpdaterProductKey";
    private const string REG_ALLOW_UNSAFE_URLS = "UpdaterAllowUnsafeUrls";
    private const string REG_SKIP_HASH_VALIDATION = "UpdaterSkipHashValidation";
    private const string REG_SKIP_SIGNER_THUMBPRINT_CHECK = "UpdaterSkipSignerThumbprintCheck";
    private const string REG_DISABLE_TLS_VALIDATION = "UpdaterDisableTlsValidation";

    private static readonly string[] DEVOLUTIONS_CERT_THUMBPRINTS =
    [
        "3f5202a9432d54293bdfe6f7e46adb0a6f8b3ba6",
        "8db5a43bb8afe4d2ffb92da9007d8997a4cc4e13",
        "50f753333811ff11f1920274afde3ffd4468b210",
    ];

    private static readonly string[] DEVOLUTIONS_MAC_DEVELOPER_IDS =
    [
        "N592S9ASDB",
    ];

#if !DEBUG
    private static readonly string[] RELEASE_IGNORED_REGISTRY_VALUES =
    [
        REG_PRODUCTINFO_KEY,
        REG_ALLOW_UNSAFE_URLS,
        REG_SKIP_HASH_VALIDATION,
        REG_SKIP_SIGNER_THUMBPRINT_CHECK,
        REG_DISABLE_TLS_VALIDATION,
    ];
#endif

    private static readonly AutoUpdaterJsonContext _jsonContext = new(
        new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
    );

    // ------------------------------------------------------------------ public API
    /// <summary>
    /// Fired on the UI thread when a validated installer is ready.  Argument is the
    /// human-readable version string, e.g. "4.2.1".
    /// </summary>
    public static event Action<string>? UpdateAvailable;

    /// <summary>
    /// Fired on the UI thread to surface progress/result of an update check or
    /// install attempt to the UI banner.  Mirrors the verbose feedback the WinUI
    /// AutoUpdater shows in its <c>InfoBar</c>.
    /// </summary>
    public static event Action<UpdateStatusInfo>? StatusChanged;

    public sealed record UpdateStatusInfo(
        string Title,
        string Message,
        InfoBarSeverity Severity,
        bool IsClosable,
        string? ActionButtonText = null,
        Action? ActionButtonAction = null);

    private static void RaiseStatus(
        string title,
        string message,
        InfoBarSeverity severity,
        bool isClosable,
        string? actionButtonText = null,
        Action? actionButtonAction = null)
    {
        var info = new UpdateStatusInfo(title, message, severity, isClosable, actionButtonText, actionButtonAction);
        Dispatcher.UIThread.Post(() => StatusChanged?.Invoke(info));
    }

    // ------------------------------------------------------------------ per-attempt log
    // Captures auto-updater log entries for the current update attempt. We keep a
    // dedicated buffer (in addition to the global session log) so the "View log"
    // banner button can show the user only the entries relevant to their failed
    // update, instead of dumping the entire noisy session log.
    private static readonly Lock _updateLogLock = new();
    private static StringBuilder? _updateLogBuilder;
    private static readonly string _updateLogPath = Path.Combine(
        Path.GetTempPath(),
        "UniGetUI",
        "last-update-attempt.log"
    );

    private static void ResetUpdateLog(bool manualCheck, bool autoLaunch)
    {
        lock (_updateLogLock)
        {
            _updateLogBuilder = new StringBuilder()
                .AppendLine($"=== UniGetUI update attempt started at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===")
                .AppendLine($"Current version: {CoreData.VersionName} (build {CoreData.BuildNumber})")
                .AppendLine($"Manual check: {manualCheck}")
                .AppendLine($"Auto-launch: {autoLaunch}")
                .AppendLine($"Process architecture: {RuntimeInformation.ProcessArchitecture}")
                .AppendLine();
            FlushUpdateLogToDiskNoLock();
        }
    }

    private static void AppendToUpdateLog(string severity, string message)
    {
        lock (_updateLogLock)
        {
            if (_updateLogBuilder is null) return;
            _updateLogBuilder.AppendLine($"[{DateTime.Now:HH:mm:ss}] [{severity}] {message}");
            FlushUpdateLogToDiskNoLock();
        }
    }

    // Persists the current buffer to _updateLogPath. Caller MUST hold _updateLogLock.
    // Failures are silently swallowed — a missing log file should never break the
    // update flow itself.
    private static void FlushUpdateLogToDiskNoLock()
    {
        if (_updateLogBuilder is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_updateLogPath)!);
            File.WriteAllText(_updateLogPath, _updateLogBuilder.ToString());
        }
        catch { /* see comment above */ }
    }

    private const string AttemptFinishedMarker = "=== Attempt finished:";

    // Appends a structured line indicating the update flow reached a terminal state.
    // The presence/absence of this marker on disk lets a subsequent app launch tell
    // whether the previous attempt completed cleanly or was killed mid-flow (e.g.,
    // by the installer terminating us during file replacement).
    private static void MarkAttemptFinished(string outcome)
    {
        lock (_updateLogLock)
        {
            if (_updateLogBuilder is null) return;
            _updateLogBuilder
                .AppendLine()
                .AppendLine($"{AttemptFinishedMarker} {outcome} at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ===");
            FlushUpdateLogToDiskNoLock();
        }
    }

    private static void RecordTargetVersion(string version)
    {
        lock (_updateLogLock)
        {
            _updateLogBuilder?.AppendLine($"Target version: {version}");
            FlushUpdateLogToDiskNoLock();
        }
    }

    /// <summary>
    /// On app startup, detects an interrupted update attempt — the log file
    /// from the previous attempt has no <see cref="AttemptFinishedMarker"/>,
    /// indicating the app was killed mid-flow (almost always because the
    /// installer terminated us during file replacement).
    ///
    /// If the running version equals the target version we recorded, the
    /// install succeeded and we are now the new version — silently appends
    /// a marker so we don't re-prompt next time.
    ///
    /// Otherwise, surfaces a Warning banner with a "View log" button so the
    /// user can investigate what happened.
    /// </summary>
    public static void CheckForOrphanedUpdateAttempt()
    {
        try
        {
            if (!File.Exists(_updateLogPath)) return;

            var info = new FileInfo(_updateLogPath);
            if ((DateTime.Now - info.LastWriteTime).TotalMinutes > 10)
                return;

            string content = File.ReadAllText(_updateLogPath);
            if (content.Contains(AttemptFinishedMarker))
                return;

            string currentVer = CoreData.VersionName;
            string? targetVer = null;
            foreach (string line in content.Split('\n'))
            {
                if (line.StartsWith("Target version: "))
                {
                    targetVer = line["Target version: ".Length..].Trim();
                    break;
                }
            }

            if (targetVer is not null && targetVer == currentVer)
            {
                Logger.Info($"Previous update attempt killed mid-flow but install succeeded (running version {currentVer} matches target). Marking as finished.");
                try
                {
                    File.AppendAllText(
                        _updateLogPath,
                        $"{Environment.NewLine}{AttemptFinishedMarker} installer succeeded (detected on next launch — running version is {currentVer}) at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
                }
                catch { /* swallow */ }
                return;
            }

            Logger.Warn($"Detected interrupted update attempt. Running={currentVer}, Target={targetVer ?? "(unknown)"}");

            RaiseStatus(
                CoreTools.Translate("Your last update attempt did not complete."),
                CoreTools.Translate("UniGetUI could not confirm whether the update succeeded. Open the log to see what happened."),
                InfoBarSeverity.Warning,
                isClosable: true,
                actionButtonText: CoreTools.Translate("View log"),
                actionButtonAction: OpenUpdateLog);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not check for orphaned update attempt: {ex.Message}");
        }
    }

    private static void LogUpdateInfo(string message, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        Logger.Info(message, caller);
        AppendToUpdateLog("INFO ", message);
    }

    private static void LogUpdateWarn(string message, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        Logger.Warn(message, caller);
        AppendToUpdateLog("WARN ", message);
    }

    private static void LogUpdateWarn(Exception ex, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        Logger.Warn(ex, caller);
        AppendToUpdateLog("WARN ", ex.ToString());
    }

    private static void LogUpdateError(string message, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        Logger.Error(message, caller);
        AppendToUpdateLog("ERROR", message);
    }

    private static void LogUpdateError(Exception ex, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        Logger.Error(ex, caller);
        AppendToUpdateLog("ERROR", ex.ToString());
    }

    private static void LogUpdateDebug(string message, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        Logger.Debug(message, caller);
        AppendToUpdateLog("DEBUG", message);
    }

    private static void OpenUpdateLog()
    {
        // The buffer is flushed to disk on every append/reset, so the file should
        // already be current. Only fall back to the full session log if no flow
        // has ever run (button shouldn't appear in that case, but be defensive).
        string pathToOpen = File.Exists(_updateLogPath)
            ? _updateLogPath
            : Logger.GetSessionLogPath();

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = pathToOpen,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not open log file '{pathToOpen}': {ex.Message}");
        }
    }

    /// <summary>
    /// Translates an Inno Setup installer exit code into a short human-readable
    /// reason. The codes come from the Inno Setup documentation
    /// (https://jrsoftware.org/ishelp/index.php?topic=setupexitcodes).
    /// </summary>
    private static string DescribeInstallerExitCode(int code) => code switch
    {
        0 => CoreTools.Translate("The installer reported success but did not restart UniGetUI."),
        1 => CoreTools.Translate("The installer failed to initialize."),
        2 => CoreTools.Translate("Setup was canceled before installation began."),
        3 => CoreTools.Translate("A fatal error occurred during the preparation phase."),
        4 => CoreTools.Translate("A fatal error occurred during installation."),
        5 => CoreTools.Translate("Installation was canceled while in progress."),
        6 => CoreTools.Translate("The installer was terminated by another process."),
        7 => CoreTools.Translate("The preparation phase determined the installation cannot proceed."),
        8 => CoreTools.Translate("The installer could not start. UniGetUI may already be running, or you do not have permission to install."),
        _ => CoreTools.Translate("Unexpected installer error."),
    };

    private static volatile bool _installRequested;
    private static string? _pendingInstallerPath;

    /// <summary>
    /// Set to <c>true</c> when the main window is closing (user quit or hidden path).
    /// Mirrors WinUI's <c>AutoUpdater.ReleaseLockForAutoupdate_Window</c> — once set,
    /// a pending installer is allowed to launch even if the user has not yet clicked
    /// the banner (e.g. user quits via tray while an update is ready).
    /// </summary>
    public static bool ReleaseLockForAutoupdate_Window;

    /// <summary>
    /// Set to <c>true</c> when the user clicks the "Update now" button in the Windows toast
    /// notification.  Mirrors WinUI's <c>AutoUpdater.ReleaseLockForAutoupdate_Notification</c>.
    /// </summary>
    public static bool ReleaseLockForAutoupdate_Notification;

    /// <summary>
    /// Called by the user when they click "Update now" in the update banner.
    /// </summary>
    public static void TriggerInstall()
    {
        LogUpdateInfo("Auto-updater: TriggerInstall invoked (user clicked Update now).");
        _installRequested = true;
    }
    public static async Task UpdateCheckLoopAsync()
    {
        if (Settings.Get(Settings.K.DisableAutoUpdateWingetUI))
        {
            LogUpdateWarn("Auto-updater: disabled by user setting, skipping.");
            return;
        }

        await CoreTools.WaitForInternetConnection();

        bool isFirstLaunch = true;
        while (true)
        {
            if (Settings.Get(Settings.K.DisableAutoUpdateWingetUI))
            {
                LogUpdateWarn("Auto-updater: disabled by user setting, stopping loop.");
                return;
            }

            bool success = await CheckAndInstallUpdatesAsync(autoLaunch: isFirstLaunch);
            isFirstLaunch = false;

            await Task.Delay(TimeSpan.FromMinutes(success ? 60 : 10));
        }
    }

    // ------------------------------------------------------------------ core logic
    internal static async Task<bool> CheckAndInstallUpdatesAsync(bool autoLaunch = false, bool manualCheck = false)
    {
        ResetUpdateLog(manualCheck, autoLaunch);
        UpdaterOverrides overrides = LoadUpdaterOverrides();
        bool wasCheckingForUpdates = true;

        try
        {
            if (manualCheck)
            {
                RaiseStatus(
                    CoreTools.Translate("We are checking for updates."),
                    CoreTools.Translate("Please wait"),
                    InfoBarSeverity.Informational,
                    isClosable: false);
            }

            UpdateCandidate candidate = await GetUpdateCandidateAsync(overrides);
            LogUpdateInfo(
                $"Auto-updater source '{candidate.SourceName}' returned version {candidate.VersionName} (upgradable={candidate.IsUpgradable})"
            );

            if (!candidate.IsUpgradable)
            {
                if (manualCheck)
                {
                    RaiseStatus(
                        CoreTools.Translate("Great! You are on the latest version."),
                        CoreTools.Translate("There are no new UniGetUI versions to be installed"),
                        InfoBarSeverity.Success,
                        isClosable: true);
                }
                MarkAttemptFinished("no update available");
                return true;
            }

            wasCheckingForUpdates = false;
            RecordTargetVersion(candidate.VersionName);
            LogUpdateInfo($"Update to UniGetUI {candidate.VersionName} is available.");

            string installerName;
            if (OperatingSystem.IsWindows())
                installerName = "UniGetUI Updater.exe";
            else if (OperatingSystem.IsMacOS())
                installerName = "UniGetUI Updater.pkg";
            else
                installerName = "UniGetUI Updater.AppImage";
            string installerPath = Path.Join(CoreData.UniGetUIDataDirectory, installerName);

            // Try cached installer first
            if (
                File.Exists(installerPath)
                && await CheckInstallerHashAsync(installerPath, candidate.InstallerHash, overrides)
                && CheckInstallerSignerThumbprint(installerPath, overrides)
            )
            {
                LogUpdateInfo("Cached valid installer found, preparing to launch...");
                return await PrepareAndLaunchAsync(installerPath, candidate.VersionName, autoLaunch, manualCheck);
            }

            // Delete invalid/outdated cached copy
            try { File.Delete(installerPath); } catch { }

            RaiseStatus(
                CoreTools.Translate(
                    "UniGetUI version {0} is being downloaded.",
                    candidate.VersionName.ToString(CultureInfo.InvariantCulture)),
                CoreTools.Translate("This may take a minute or two"),
                InfoBarSeverity.Informational,
                isClosable: false);

            LogUpdateInfo("Downloading installer...");
            await DownloadInstallerAsync(candidate.InstallerDownloadUrl, installerPath, overrides);

            if (
                await CheckInstallerHashAsync(installerPath, candidate.InstallerHash, overrides)
                && CheckInstallerSignerThumbprint(installerPath, overrides)
            )
            {
                LogUpdateInfo("Downloaded installer is valid, preparing to launch...");
                return await PrepareAndLaunchAsync(installerPath, candidate.VersionName, autoLaunch, manualCheck);
            }

            LogUpdateError("Installer authenticity could not be verified. Aborting update.");
            RaiseStatus(
                CoreTools.Translate("The installer authenticity could not be verified."),
                CoreTools.Translate("The update process has been aborted."),
                InfoBarSeverity.Error,
                isClosable: true,
                actionButtonText: CoreTools.Translate("View log"),
                actionButtonAction: OpenUpdateLog);
            MarkAttemptFinished("authenticity verification failed");
            return false;
        }
        catch (PlatformArtifactMissingException ex)
        {
            // A newer version exists in productinfo but no installer artifact is
            // published for the current OS/arch yet. Surface this as a friendly
            // "manual update required" notice rather than a generic error.
            LogUpdateWarn(ex.Message);
            if (manualCheck)
            {
                RaiseStatus(
                    CoreTools.Translate("Auto-update is not yet available on this platform."),
                    CoreTools.Translate("Please update UniGetUI manually."),
                    InfoBarSeverity.Warning,
                    isClosable: true);
            }
            MarkAttemptFinished("platform artifact missing");
            return false;
        }
        catch (Exception ex)
        {
            LogUpdateError("An error occurred while checking for updates:");
            LogUpdateError(ex);
            if (manualCheck || !wasCheckingForUpdates)
            {
                RaiseStatus(
                    CoreTools.Translate("An error occurred when checking for updates: "),
                    ex.Message,
                    InfoBarSeverity.Error,
                    isClosable: true,
                    actionButtonText: CoreTools.Translate("View log"),
                    actionButtonAction: OpenUpdateLog);
            }
            MarkAttemptFinished($"exception: {ex.Message}");
            return false;
        }
    }

    // ------------------------------------------------------------------ update flow
    private static async Task<bool> PrepareAndLaunchAsync(
        string installerPath,
        string versionName,
        bool autoLaunch,
        bool manualCheck)
    {
        _pendingInstallerPath = installerPath;
        _installRequested = false;
        ReleaseLockForAutoupdate_Notification = false;

        // Notify UI (update banner + toast)
        Dispatcher.UIThread.Post(() => UpdateAvailable?.Invoke(versionName));
        if (OperatingSystem.IsWindows())
            WindowsAppNotificationBridge.ShowSelfUpdateAvailableNotification(versionName);
        else if (OperatingSystem.IsMacOS())
            MacOsNotificationBridge.ShowSelfUpdateAvailableNotification(versionName);

        if (autoLaunch)
        {
            // On first launch in background we wait for user interaction
        }

        // Wait until user requests install, clicks the toast, or the window is being closed
        while (!_installRequested && !ReleaseLockForAutoupdate_Window && !ReleaseLockForAutoupdate_Notification)
        {
            if (!manualCheck && Settings.Get(Settings.K.DisableAutoUpdateWingetUI))
            {
                LogUpdateWarn("Auto-updater: disabled while waiting for user \u2014 aborting.");
                MarkAttemptFinished("aborted - auto-update disabled while waiting");
                return true;
            }
            await Task.Delay(500);
        }

        LogUpdateInfo("Installing update \u2014 launching installer.");
        await LaunchInstallerAsync(installerPath);
        return true;
    }

    private static async Task LaunchInstallerAsync(string installerLocation)
    {
        if (OperatingSystem.IsMacOS())
        {
            await LaunchMacInstallerAsync(installerLocation);
            return;
        }

        if (OperatingSystem.IsLinux())
        {
            LaunchLinuxInstaller(installerLocation);
            return;
        }

        LogUpdateInfo($"Launching installer: {installerLocation}");
        using Process p = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = installerLocation,
                Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NoVCRedist /NoEdgeWebView /NoWinGet",
                UseShellExecute = true,
                CreateNoWindow = true,
            },
        };

        bool started;
        try
        {
            started = p.Start();
        }
        catch (Exception ex)
        {
            LogUpdateError("Process.Start threw while launching the installer:");
            LogUpdateError(ex);
            RaiseStatus(
                CoreTools.Translate("The updater could not be launched."),
                ex.Message,
                InfoBarSeverity.Error,
                isClosable: true,
                actionButtonText: CoreTools.Translate("View log"),
                actionButtonAction: OpenUpdateLog);
            MarkAttemptFinished($"installer launch threw: {ex.Message}");
            return;
        }

        if (!started)
        {
            LogUpdateError("Failed to start installer process (Process.Start returned false).");
            RaiseStatus(
                CoreTools.Translate("The updater could not be launched."),
                CoreTools.Translate("The operating system did not start the installer process."),
                InfoBarSeverity.Error,
                isClosable: true,
                actionButtonText: CoreTools.Translate("View log"),
                actionButtonAction: OpenUpdateLog);
            MarkAttemptFinished("Process.Start returned false");
            return;
        }

        LogUpdateInfo($"Installer process started (PID {p.Id}). The installer is expected to terminate UniGetUI before file replacement.");

        RaiseStatus(
            CoreTools.Translate("UniGetUI is being updated..."),
            CoreTools.Translate("This may take a minute or two"),
            InfoBarSeverity.Informational,
            isClosable: false);

        await p.WaitForExitAsync();

        // If we reach here, the installer exited without terminating this process.
        // Distinguish two cases:
        //   - Exit code 0: installer succeeded; the new version IS installed at the
        //     install location, but the running copy was not replaced (almost always
        //     because UniGetUI is running from outside the install location — typically
        //     a development build). This is not really an error.
        //   - Any other code: installer reported a failure; the update did not apply.
        int exitCode = p.ExitCode;
        string reason = DescribeInstallerExitCode(exitCode);

        if (exitCode == 0)
        {
            string runningPath = Environment.ProcessPath ?? "(unknown)";
            LogUpdateWarn($"Installer reported success (exit code 0) but did not replace this running copy. Running from: {runningPath}");

            RaiseStatus(
                CoreTools.Translate("Update installed."),
                CoreTools.Translate("UniGetUI was updated successfully, but this running copy was not replaced. This usually means you are running a development build. Close this copy and start the newly-installed version to finish."),
                InfoBarSeverity.Warning,
                isClosable: true,
                actionButtonText: CoreTools.Translate("View log"),
                actionButtonAction: OpenUpdateLog);
            MarkAttemptFinished("installer succeeded but did not replace running copy");
            return;
        }

        LogUpdateError($"Installer exited with code {exitCode} ({reason}) without restarting UniGetUI.");

        RaiseStatus(
            CoreTools.Translate("The update could not be applied."),
            CoreTools.Translate("Installer exit code {0}: {1}", exitCode, reason),
            InfoBarSeverity.Error,
            isClosable: true,
            actionButtonText: CoreTools.Translate("View log"),
            actionButtonAction: OpenUpdateLog);
        MarkAttemptFinished($"installer failed with code {exitCode}");
    }

    private static async Task LaunchMacInstallerAsync(string installerLocation)
    {
        LogUpdateInfo($"Launching macOS installer: {installerLocation}");

        // Escape for inclusion in the AppleScript string literal.
        string scriptPath = installerLocation.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string appleScript =
            $"do shell script \"/usr/sbin/installer -pkg \\\"{scriptPath}\\\" -target /\" with administrator privileges";

        using Process p = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/usr/bin/osascript",
                ArgumentList = { "-e", appleScript },
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };

        bool started;
        try
        {
            started = p.Start();
        }
        catch (Exception ex)
        {
            LogUpdateError("osascript threw while launching the macOS installer:");
            LogUpdateError(ex);
            RaiseStatus(
                CoreTools.Translate("The updater could not be launched."),
                ex.Message,
                InfoBarSeverity.Error,
                isClosable: true,
                actionButtonText: CoreTools.Translate("View log"),
                actionButtonAction: OpenUpdateLog);
            MarkAttemptFinished($"installer launch threw: {ex.Message}");
            return;
        }

        if (!started)
        {
            LogUpdateError("Failed to start osascript process (Process.Start returned false).");
            RaiseStatus(
                CoreTools.Translate("The updater could not be launched."),
                CoreTools.Translate("The operating system did not start the installer process."),
                InfoBarSeverity.Error,
                isClosable: true,
                actionButtonText: CoreTools.Translate("View log"),
                actionButtonAction: OpenUpdateLog);
            MarkAttemptFinished("Process.Start returned false");
            return;
        }

        RaiseStatus(
            CoreTools.Translate("UniGetUI is being updated..."),
            CoreTools.Translate("This may take a minute or two"),
            InfoBarSeverity.Informational,
            isClosable: false);

        string stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        int exitCode = p.ExitCode;

        if (exitCode != 0)
        {
            // osascript exits 1 with stderr "User canceled." when the user dismisses
            // the admin authentication prompt. Treat that as a normal cancellation.
            bool userCancelled = stderr.Contains("User canceled", StringComparison.OrdinalIgnoreCase)
                                 || stderr.Contains("(-128)");
            string trimmed = stderr.Trim();
            LogUpdateError(
                userCancelled
                    ? "macOS installer cancelled at the authentication prompt."
                    : $"macOS installer failed (exit {exitCode}): {trimmed}"
            );

            RaiseStatus(
                userCancelled
                    ? CoreTools.Translate("Update cancelled.")
                    : CoreTools.Translate("The update could not be applied."),
                userCancelled
                    ? CoreTools.Translate("Authentication was cancelled.")
                    : (string.IsNullOrWhiteSpace(trimmed)
                        ? CoreTools.Translate("Installer exit code {0}", exitCode)
                        : trimmed),
                userCancelled ? InfoBarSeverity.Warning : InfoBarSeverity.Error,
                isClosable: true,
                actionButtonText: CoreTools.Translate("View log"),
                actionButtonAction: OpenUpdateLog);
            MarkAttemptFinished(
                userCancelled
                    ? "user cancelled authentication"
                    : $"installer failed with code {exitCode}"
            );
            return;
        }

        LogUpdateInfo("macOS installer completed successfully.");

        const string installedApp = "/Applications/UniGetUI.app";
        if (!Directory.Exists(installedApp))
        {
            string runningPath = Environment.ProcessPath ?? "(unknown)";
            LogUpdateWarn(
                $"Installer reported success but {installedApp} was not found. Running from: {runningPath}"
            );
            RaiseStatus(
                CoreTools.Translate("Update installed."),
                CoreTools.Translate("UniGetUI was updated successfully, but this running copy was not replaced. This usually means you are running a development build. Close this copy and start the newly-installed version to finish."),
                InfoBarSeverity.Warning,
                isClosable: true,
                actionButtonText: CoreTools.Translate("View log"),
                actionButtonAction: OpenUpdateLog);
            MarkAttemptFinished("installer succeeded but did not replace running copy");
            return;
        }

        LogUpdateInfo($"Relaunching {installedApp} and exiting current process.");

        // Detach a tiny shell that waits a moment, then opens a *new* instance of the
        // freshly-installed app. The brief sleep gives this process time to exit so
        // `open -na` doesn't race against our termination.
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/sh",
                ArgumentList = { "-c", $"sleep 1 && /usr/bin/open -na \"{installedApp}\"" },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            LogUpdateWarn("Could not schedule relaunch of new app instance:");
            LogUpdateWarn(ex);
        }

        MarkAttemptFinished("macOS installer succeeded; relaunching");

        // Match the Windows flow: the installer terminates the running copy. On macOS
        // we do that ourselves so the relaunch picks up the freshly-installed bundle.
        Environment.Exit(0);
    }

    [SupportedOSPlatform("linux")]
    private static void LaunchLinuxInstaller(string installerLocation)
    {
        LogUpdateInfo($"Applying Linux AppImage update from: {installerLocation}");

        // The AppImage runtime sets APPIMAGE to the on-disk path of the running
        // .AppImage file. Without it we have no reliable way to know which file
        // to replace (e.g., when running from `dotnet run` during development).
        string? runningApp = Environment.GetEnvironmentVariable("APPIMAGE");
        if (string.IsNullOrEmpty(runningApp) || !File.Exists(runningApp))
        {
            LogUpdateWarn(
                $"APPIMAGE env var is not set or points to a missing file (got '{runningApp}'). "
                + "UniGetUI does not appear to be running from an AppImage; the running copy "
                + "cannot be replaced automatically."
            );
            RaiseStatus(
                CoreTools.Translate("Update installed."),
                CoreTools.Translate("UniGetUI was updated successfully, but this running copy was not replaced. This usually means you are running a development build. Close this copy and start the newly-installed version to finish."),
                InfoBarSeverity.Warning,
                isClosable: true,
                actionButtonText: CoreTools.Translate("View log"),
                actionButtonAction: OpenUpdateLog);
            MarkAttemptFinished("not running from an AppImage; running copy not replaced");
            return;
        }

        try
        {
            // Replace the running AppImage on disk. Linux allows renaming over a
            // currently-executing file: the running process keeps its inode mapped,
            // and future launches resolve the path to the new file.
            File.Move(installerLocation, runningApp, overwrite: true);

            File.SetUnixFileMode(
                runningApp,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead | UnixFileMode.OtherExecute
            );
        }
        catch (Exception ex)
        {
            LogUpdateError("Failed to replace the running AppImage:");
            LogUpdateError(ex);
            RaiseStatus(
                CoreTools.Translate("The update could not be applied."),
                ex.Message,
                InfoBarSeverity.Error,
                isClosable: true,
                actionButtonText: CoreTools.Translate("View log"),
                actionButtonAction: OpenUpdateLog);
            MarkAttemptFinished($"AppImage replacement failed: {ex.Message}");
            return;
        }

        LogUpdateInfo($"Replaced {runningApp}; relaunching new AppImage and exiting current process.");

        RaiseStatus(
            CoreTools.Translate("UniGetUI is being updated..."),
            CoreTools.Translate("This may take a minute or two"),
            InfoBarSeverity.Informational,
            isClosable: false);

        // Detach a shell that waits a moment, then runs the new AppImage. The brief
        // sleep gives this process time to exit so the relaunched instance starts
        // cleanly without lingering shared resources.
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/sh",
                ArgumentList = { "-c", "sleep 1 && \"$1\" >/dev/null 2>&1 &", "sh", runningApp },
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            LogUpdateWarn("Could not schedule relaunch of new AppImage:");
            LogUpdateWarn(ex);
        }

        MarkAttemptFinished("Linux AppImage replaced; relaunching");
        Environment.Exit(0);
    }

    // ------------------------------------------------------------------ update check sources
    private static async Task<UpdateCandidate> GetUpdateCandidateAsync(UpdaterOverrides overrides)
    {
        return await CheckFromProductInfoAsync(overrides);
    }

    private static async Task<UpdateCandidate> CheckFromProductInfoAsync(UpdaterOverrides overrides)
    {
        LogUpdateDebug($"Checking updates via ProductInfo: {overrides.ProductInfoUrl}");

        if (!IsSourceUrlAllowed(overrides.ProductInfoUrl, overrides.AllowUnsafeUrls))
        {
            throw new InvalidOperationException(
                $"ProductInfo URL is not allowed: {overrides.ProductInfoUrl}"
            );
        }

        string json;
        using (HttpClient client = new(CreateHttpClientHandler(overrides)))
        {
            client.Timeout = TimeSpan.FromSeconds(600);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
            json = await client.GetStringAsync(overrides.ProductInfoUrl);
        }

        Dictionary<string, ProductInfoProduct>? root =
            JsonSerializer.Deserialize(
                json,
                typeof(Dictionary<string, ProductInfoProduct>),
                _jsonContext
            ) as Dictionary<string, ProductInfoProduct>;

        if (root is null || root.Count == 0)
        {
            throw new FormatException("productinfo.json is empty or invalid.");
        }

        if (!root.TryGetValue(overrides.ProductInfoProductKey, out ProductInfoProduct? product))
        {
            throw new KeyNotFoundException(
                $"Product key '{overrides.ProductInfoProductKey}' not found in productinfo.json"
            );
        }

        bool useBeta = Settings.Get(Settings.K.EnableUniGetUIBeta);
        ProductInfoChannel? channel = useBeta ? product.Beta : product.Current;
        if (channel is null)
        {
            throw new KeyNotFoundException(
                $"Channel '{(useBeta ? "Beta" : "Current")}' not found for product '{overrides.ProductInfoProductKey}'"
            );
        }

        ProductInfoFile installer = SelectInstallerFile(channel.Files);
        if (!IsSourceUrlAllowed(installer.Url, overrides.AllowUnsafeUrls))
        {
            throw new InvalidOperationException($"Installer URL is not allowed: {installer.Url}");
        }

        Version current = ParseVersionOrFallback(
            CoreData.VersionName,
            new Version(0, 0, 0, CoreData.BuildNumber)
        );
        Version available = ParseVersionOrFallback(channel.Version, new Version(0, 0, 0, 0));
        bool upgradable = available > current;

        LogUpdateDebug(
            $"ProductInfo check: current={current}, available={available}, upgradable={upgradable}"
        );

        return new UpdateCandidate(upgradable, channel.Version, installer.Hash, installer.Url, "ProductInfo");
    }

    // ------------------------------------------------------------------ validation helpers
    private static async Task<bool> CheckInstallerHashAsync(
        string path,
        string expectedHash,
        UpdaterOverrides overrides)
    {
        if (overrides.SkipHashValidation)
        {
            LogUpdateWarn("Registry override: skipping hash validation.");
            return true;
        }

        using FileStream fs = File.OpenRead(path);
        string actual = Convert
            .ToHexString(await SHA256.Create().ComputeHashAsync(fs))
            .ToLowerInvariant();

        if (actual == expectedHash.ToLowerInvariant())
        {
            LogUpdateDebug($"Hash match: {actual}");
            return true;
        }

        LogUpdateWarn($"Hash mismatch. Expected: {expectedHash}  Got: {actual}");
        return false;
    }

    private static bool CheckInstallerSignerThumbprint(string path, UpdaterOverrides overrides)
    {
        if (overrides.SkipSignerThumbprintCheck)
        {
            LogUpdateWarn("Registry override: skipping signer thumbprint validation.");
            return true;
        }

        if (OperatingSystem.IsMacOS())
        {
            return CheckMacInstallerSignature(path);
        }

        if (OperatingSystem.IsLinux())
        {
            // AppImage has no built-in signing format equivalent to Authenticode/.pkg.
            // Hash validation (verified separately, against the productinfo.json fetched
            // over HTTPS from a trusted host) provides the integrity guarantee. A future
            // extension could verify a detached GPG signature published alongside the
            // .AppImage in productinfo.
            LogUpdateWarn("Linux .AppImage signature validation is not implemented — relying on hash check.");
            return true;
        }

        if (!OperatingSystem.IsWindows())
        {
            LogUpdateWarn("Skipping installer signature validation on unsupported platform.");
            return true;
        }

        try
        {
#pragma warning disable SYSLIB0057
            X509Certificate signerCert = X509Certificate.CreateFromSignedFile(path);
#pragma warning restore SYSLIB0057
            using X509Certificate2 cert2 = new(signerCert);
            string thumbprint = NormalizeThumbprint(cert2.Thumbprint ?? string.Empty);

            if (string.IsNullOrWhiteSpace(thumbprint))
            {
                LogUpdateWarn($"Could not read signer thumbprint for '{path}'");
                return false;
            }

            if (DEVOLUTIONS_CERT_THUMBPRINTS.Contains(thumbprint, StringComparer.OrdinalIgnoreCase))
            {
                LogUpdateDebug($"Installer signer thumbprint is trusted: {thumbprint}");
                return true;
            }

            LogUpdateWarn($"Installer signer thumbprint is NOT trusted: {thumbprint}");
            return false;
        }
        catch (Exception ex)
        {
            LogUpdateWarn("Could not validate installer signer thumbprint.");
            LogUpdateWarn(ex);
            return false;
        }
    }

    private static bool CheckMacInstallerSignature(string path)
    {
        if (DEVOLUTIONS_MAC_DEVELOPER_IDS.Length == 0)
        {
            LogUpdateWarn(
                "No Devolutions macOS Developer Team IDs configured — skipping .pkg signature validation."
            );
            return true;
        }

        try
        {
            using Process p = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/sbin/pkgutil",
                    ArgumentList = { "--check-signature", path },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            p.Start();
            string stdout = p.StandardOutput.ReadToEnd();
            string stderr = p.StandardError.ReadToEnd();
            p.WaitForExit();

            if (p.ExitCode != 0)
            {
                LogUpdateWarn(
                    $"pkgutil --check-signature exited {p.ExitCode}; signature could not be verified. {stderr.Trim()}"
                );
                return false;
            }

            foreach (string teamId in DEVOLUTIONS_MAC_DEVELOPER_IDS)
            {
                if (stdout.Contains($"({teamId})", StringComparison.OrdinalIgnoreCase))
                {
                    LogUpdateDebug($"Installer is signed by trusted Developer Team ID {teamId}.");
                    return true;
                }
            }

            LogUpdateWarn("Installer signature does not match any trusted Devolutions Developer Team ID.");
            return false;
        }
        catch (Exception ex)
        {
            LogUpdateWarn("Could not validate installer signature via pkgutil.");
            LogUpdateWarn(ex);
            return false;
        }
    }

    // ------------------------------------------------------------------ download
    private static async Task DownloadInstallerAsync(
        string url,
        string destination,
        UpdaterOverrides overrides)
    {
        if (!IsSourceUrlAllowed(url, overrides.AllowUnsafeUrls))
        {
            throw new InvalidOperationException($"Download URL is not allowed: {url}");
        }

        LogUpdateDebug($"Downloading installer from {url}");
        using HttpClient client = new(CreateHttpClientHandler(overrides));
        client.Timeout = TimeSpan.FromSeconds(600);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);

        HttpResponseMessage response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();

        using FileStream fs = new(destination, FileMode.OpenOrCreate);
        await response.Content.CopyToAsync(fs);

        LogUpdateDebug("Installer download complete.");
    }

    // ------------------------------------------------------------------ HTTP client
    private static HttpClientHandler CreateHttpClientHandler(UpdaterOverrides overrides)
    {
        var handler = new HttpClientHandler();
        if (overrides.DisableTlsValidation)
        {
            LogUpdateWarn("Registry override: TLS certificate validation is disabled for updater requests.");
            handler.ServerCertificateCustomValidationCallback = static (_, _, _, _) => true;
        }
        return handler;
    }

    // ------------------------------------------------------------------ URL / arch helpers
    private static bool IsSourceUrlAllowed(string url, bool allowUnsafe)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (allowUnsafe)
        {
            LogUpdateWarn($"Registry override: allowing potentially unsafe URL {url}");
            return true;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return uri.Host.EndsWith("devolutions.net", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("release-assets.githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    private static ProductInfoFile SelectInstallerFile(List<ProductInfoFile> files)
    {
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };

        if (OperatingSystem.IsMacOS())
        {
            ProductInfoFile? mac =
                files.FirstOrDefault(f => f.Type.Equals("pkg", StringComparison.OrdinalIgnoreCase) && f.Arch.Equals(arch, StringComparison.OrdinalIgnoreCase))
                ?? files.FirstOrDefault(f => f.Type.Equals("pkg", StringComparison.OrdinalIgnoreCase) && f.Arch.Equals("universal", StringComparison.OrdinalIgnoreCase))
                ?? files.FirstOrDefault(f => f.Type.Equals("pkg", StringComparison.OrdinalIgnoreCase) && f.Arch.Equals("Any", StringComparison.OrdinalIgnoreCase));

            return mac ?? throw new PlatformArtifactMissingException(
                $"No compatible macOS installer (.pkg) found in productinfo for architecture '{arch}'"
            );
        }

        if (OperatingSystem.IsLinux())
        {
            ProductInfoFile? linux =
                files.FirstOrDefault(f => f.Type.Equals("AppImage", StringComparison.OrdinalIgnoreCase) && f.Arch.Equals(arch, StringComparison.OrdinalIgnoreCase))
                ?? files.FirstOrDefault(f => f.Type.Equals("AppImage", StringComparison.OrdinalIgnoreCase) && f.Arch.Equals("universal", StringComparison.OrdinalIgnoreCase))
                ?? files.FirstOrDefault(f => f.Type.Equals("AppImage", StringComparison.OrdinalIgnoreCase) && f.Arch.Equals("Any", StringComparison.OrdinalIgnoreCase));

            return linux ?? throw new PlatformArtifactMissingException(
                $"No compatible Linux installer (.AppImage) found in productinfo for architecture '{arch}'"
            );
        }

        ProductInfoFile? match =
            files.FirstOrDefault(f => f.Type.Equals("exe", StringComparison.OrdinalIgnoreCase) && f.Arch.Equals(arch, StringComparison.OrdinalIgnoreCase))
            ?? files.FirstOrDefault(f => f.Type.Equals("exe", StringComparison.OrdinalIgnoreCase) && f.Arch.Equals("Any", StringComparison.OrdinalIgnoreCase))
            ?? files.FirstOrDefault(f => f.Type.Equals("msi", StringComparison.OrdinalIgnoreCase) && f.Arch.Equals(arch, StringComparison.OrdinalIgnoreCase))
            ?? files.FirstOrDefault(f => f.Type.Equals("msi", StringComparison.OrdinalIgnoreCase) && f.Arch.Equals("Any", StringComparison.OrdinalIgnoreCase));

        return match ?? throw new KeyNotFoundException(
            $"No compatible installer found in productinfo for architecture '{arch}'"
        );
    }

    private static Version ParseVersionOrFallback(string raw, Version fallback)
    {
        string sanitized = raw.Trim().TrimStart('v', 'V');
        if (Version.TryParse(sanitized, out Version? parsed))
        {
            return CoreTools.NormalizeVersionForComparison(parsed);
        }

        LogUpdateWarn($"Could not parse version '{raw}', using fallback '{fallback}'");
        return fallback;
    }

    private static string NormalizeThumbprint(string thumbprint) =>
        new(thumbprint.ToLowerInvariant().Where(char.IsAsciiHexDigit).ToArray());

    // ------------------------------------------------------------------ registry
    private static UpdaterOverrides LoadUpdaterOverrides()
    {
        if (!OperatingSystem.IsWindows())
        {
            return new UpdaterOverrides(
                DEFAULT_PRODUCTINFO_URL,
                DEFAULT_PRODUCTINFO_KEY,
                false,
                false,
                false,
                false
            );
        }

#pragma warning disable CA1416
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(REGISTRY_PATH);

#if DEBUG
        if (key is not null)
        {
            LogUpdateInfo($"Updater registry overrides loaded from HKLM\\{REGISTRY_PATH}");
        }

        return new UpdaterOverrides(
            GetRegistryString(key, REG_PRODUCTINFO_URL) ?? DEFAULT_PRODUCTINFO_URL,
            GetRegistryString(key, REG_PRODUCTINFO_KEY) ?? DEFAULT_PRODUCTINFO_KEY,
            GetRegistryBool(key, REG_ALLOW_UNSAFE_URLS),
            GetRegistryBool(key, REG_SKIP_HASH_VALIDATION),
            GetRegistryBool(key, REG_SKIP_SIGNER_THUMBPRINT_CHECK),
            GetRegistryBool(key, REG_DISABLE_TLS_VALIDATION)
        );
#else
        LogIgnoredReleaseOverrides(key);
        string productInfoUrl = GetRegistryString(key, REG_PRODUCTINFO_URL) ?? DEFAULT_PRODUCTINFO_URL;

        return new UpdaterOverrides(
            productInfoUrl,
            DEFAULT_PRODUCTINFO_KEY,
            false,
            false,
            false,
            false
        );
#endif
#pragma warning restore CA1416
    }

#if !DEBUG
    private static void LogIgnoredReleaseOverrides(RegistryKey? key)
    {
#pragma warning disable CA1416
        if (key is null)
        {
            return;
        }

        foreach (string valueName in RELEASE_IGNORED_REGISTRY_VALUES)
        {
            if (key.GetValue(valueName) is not null)
            {
                LogUpdateWarn(
                    $"Release build is ignoring updater registry value HKLM\\{REGISTRY_PATH}\\{valueName}."
                );
            }
        }
#pragma warning restore CA1416
    }
#endif

    private static string? GetRegistryString(RegistryKey? key, string valueName)
    {
#pragma warning disable CA1416
        string? parsed = key?.GetValue(valueName)?.ToString();
#pragma warning restore CA1416
        return string.IsNullOrWhiteSpace(parsed) ? null : parsed.Trim();
    }

#if DEBUG
    private static bool GetRegistryBool(RegistryKey? key, string valueName)
    {
#pragma warning disable CA1416
        object? value = key?.GetValue(valueName);
#pragma warning restore CA1416
        if (value is null) return false;
        if (value is int i) return i != 0;
        if (value is long l) return l != 0;
        string s = value.ToString()?.Trim() ?? "";
        return s == "1"
            || s.Equals("true", StringComparison.OrdinalIgnoreCase)
            || s.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || s.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
#endif

    // ------------------------------------------------------------------ data types
    private sealed class PlatformArtifactMissingException(string message) : Exception(message);

    private sealed record UpdateCandidate(
        bool IsUpgradable,
        string VersionName,
        string InstallerHash,
        string InstallerDownloadUrl,
        string SourceName
    );

    private sealed record UpdaterOverrides(
        string ProductInfoUrl,
        string ProductInfoProductKey,
        bool AllowUnsafeUrls,
        bool SkipHashValidation,
        bool SkipSignerThumbprintCheck,
        bool DisableTlsValidation
    );

    private sealed class ProductInfoProduct
    {
        public ProductInfoChannel? Current { get; set; }
        public ProductInfoChannel? Beta { get; set; }
    }

    private sealed class ProductInfoChannel
    {
        public string Version { get; set; } = string.Empty;
        public List<ProductInfoFile> Files { get; set; } = [];
    }

    private sealed class ProductInfoFile
    {
        public string Arch { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Hash { get; set; } = string.Empty;
    }

    [JsonSourceGenerationOptions(AllowTrailingCommas = true)]
    [JsonSerializable(typeof(Dictionary<string, ProductInfoProduct>))]
    private sealed partial class AutoUpdaterJsonContext : JsonSerializerContext { }
}
