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
    private const string INSTALLER_VALIDATION_FAILURE_MARKER_SUFFIX = ".validation-failed";

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
            _updateLogBuilder.AppendLine($"[{DateTime.Now:HH:mm:ss}] [{severity}] {Logger.Redact(message)}");
            FlushUpdateLogToDiskNoLock();
        }
    }

    // Tmp + rename so a kill mid-flush (installer terminates us during file replacement) can't leave a 0-byte file.
    private static void FlushUpdateLogToDiskNoLock()
    {
        if (_updateLogBuilder is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_updateLogPath)!);
            string tempPath = _updateLogPath + ".tmp";
            File.WriteAllText(tempPath, _updateLogBuilder.ToString());
            File.Move(tempPath, _updateLogPath, overwrite: true);
        }
        catch { }
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

            if (targetVer is null)
            {
                Logger.Info("Update log has no recorded target version; skipping orphan-attempt banner.");
                return;
            }

            if (VersionsMatch(targetVer, currentVer))
            {
                Logger.Info($"Previous update attempt killed mid-flow but install succeeded (running version {currentVer} matches target {targetVer}). Marking as finished.");
                try
                {
                    File.AppendAllText(
                        _updateLogPath,
                        $"{Environment.NewLine}{AttemptFinishedMarker} installer succeeded (detected on next launch — running version is {currentVer}) at {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}");
                }
                catch { /* swallow */ }
                return;
            }

            Logger.Warn($"Detected interrupted update attempt. Running={currentVer}, Target={targetVer}");

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

            await CheckAndInstallUpdatesAsync(autoLaunch: isFirstLaunch);
            isFirstLaunch = false;

            await Task.Delay(UpdaterDownloadEngine.DefaultAutomaticUpdateCheckInterval);
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
            else
                // macOS and Linux both ship as self-contained .tar.gz archives.
                installerName = "UniGetUI Updater.tar.gz";
            string installerPath = Path.Join(CoreData.UniGetUIDataDirectory, installerName);
            UpdaterDownloadIdentity installerIdentity = new(
                candidate.VersionName,
                candidate.InstallerHash,
                candidate.InstallerDownloadUrl
            );

            // Try cached installer first
            if (
                File.Exists(installerPath)
                && await CheckInstallerHashAsync(installerPath, candidate.InstallerHash, overrides)
                && CheckInstallerSignerThumbprint(installerPath, overrides)
            )
            {
                ClearInstallerValidationFailure(GetInstallerValidationFailureMarkerPath(installerPath));
                UpdaterDownloadEngine.ClearFailureState(
                    UpdaterDownloadEngine.GetFailureStatePath(installerPath),
                    message => LogUpdateWarn(message)
                );
                LogUpdateInfo("Cached valid installer found, preparing to launch...");
                return await PrepareAndLaunchAsync(installerPath, candidate.VersionName, autoLaunch, manualCheck);
            }

            string installerValidationFailureMarkerPath =
                GetInstallerValidationFailureMarkerPath(installerPath);
            string installerDownloadFailureStatePath =
                UpdaterDownloadEngine.GetFailureStatePath(installerPath);
            if (File.Exists(installerPath))
            {
                LogUpdateWarn(
                    "Cached installer is invalid; it will be kept until a replacement is validated."
                );
            }

            if (
                !manualCheck
                && UpdaterDownloadEngine.IsFailureBackoffActive(
                    installerDownloadFailureStatePath,
                    installerIdentity,
                    DateTime.UtcNow,
                    out TimeSpan remainingBackoff,
                    out UpdaterDownloadFailureState? failureState,
                    message => LogUpdateWarn(message)
                )
            )
            {
                LogUpdateWarn(
                    $"Skipping installer download for version {candidate.VersionName}; previous {failureState?.FailureClass ?? "download"} failure is backed off for {remainingBackoff:g}."
                );
                MarkAttemptFinished("installer download skipped by failure backoff");
                return true;
            }

            RaiseStatus(
                CoreTools.Translate(
                    "UniGetUI version {0} is being downloaded.",
                    candidate.VersionName.ToString(CultureInfo.InvariantCulture)),
                CoreTools.Translate("This may take a minute or two"),
                InfoBarSeverity.Informational,
                isClosable: false);

            LogUpdateInfo("Downloading installer...");
            string downloadedInstallerPath;
            try
            {
                downloadedInstallerPath = await DownloadInstallerAsync(
                    candidate.InstallerDownloadUrl,
                    installerPath,
                    overrides,
                    installerIdentity
                );
            }
            catch (Exception ex)
            {
                UpdaterDownloadEngine.RecordFailure(
                    installerDownloadFailureStatePath,
                    installerIdentity,
                    "download",
                    DateTime.UtcNow,
                    message => LogUpdateWarn(message)
                );
                LogUpdateWarn($"Installer download failed: {ex.Message}");
                throw;
            }

            if (
                await CheckInstallerHashAsync(downloadedInstallerPath, candidate.InstallerHash, overrides)
                && CheckInstallerSignerThumbprint(downloadedInstallerPath, overrides)
            )
            {
                UpdaterDownloadEngine.PromotePartialDownload(
                    installerPath,
                    message => LogUpdateWarn(message)
                );
                ClearInstallerValidationFailure(installerValidationFailureMarkerPath);
                UpdaterDownloadEngine.ClearFailureState(
                    installerDownloadFailureStatePath,
                    message => LogUpdateWarn(message)
                );
                LogUpdateInfo("Downloaded installer is valid, preparing to launch...");
                return await PrepareAndLaunchAsync(installerPath, candidate.VersionName, autoLaunch, manualCheck);
            }

            UpdaterDownloadEngine.RecordFailure(
                installerDownloadFailureStatePath,
                installerIdentity,
                "validation",
                DateTime.UtcNow,
                message => LogUpdateWarn(message)
            );
            UpdaterDownloadEngine.DeletePartialDownload(
                installerPath,
                message => LogUpdateWarn(message)
            );

            LogUpdateError("Installer authenticity could not be verified. Aborting update.");
            RaiseStatus(
                CoreTools.Translate("The installer authenticity could not be verified."),
                CoreTools.Translate("The update process has been aborted."),
                InfoBarSeverity.Error,
                isClosable: true,
                actionButtonText: CoreTools.Translate("View log"),
                actionButtonAction: OpenUpdateLog);
            MarkAttemptFinished("authenticity verification failed");
            return !manualCheck;
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
    internal static string GetInstallerValidationFailureMarkerPath(string installerPath)
    {
        return installerPath + INSTALLER_VALIDATION_FAILURE_MARKER_SUFFIX;
    }

    private static void ClearInstallerValidationFailure(string markerPath)
    {
        DeleteFileIfExists(markerPath, "installer validation failure marker");
    }

    private static void DeleteFileIfExists(string path, string description)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            LogUpdateWarn($"Could not delete {description} at '{path}': {ex.Message}");
        }
    }

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
            await LaunchLinuxInstallerAsync(installerLocation);
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

    [SupportedOSPlatform("macos")]
    private static async Task LaunchMacInstallerAsync(string installerLocation)
    {
        LogUpdateInfo($"Applying macOS update from archive: {installerLocation}");

        RaiseStatus(
            CoreTools.Translate("UniGetUI is being updated..."),
            CoreTools.Translate("This may take a minute or two"),
            InfoBarSeverity.Informational,
            isClosable: false);

        string stagingDir;
        try
        {
            stagingDir = await Task.Run(() => ExtractTarGz(installerLocation));
        }
        catch (Exception ex)
        {
            LogUpdateError("Failed to extract the macOS update archive:");
            LogUpdateError(ex);
            RaiseStatus(
                CoreTools.Translate("The update could not be applied."),
                ex.Message,
                InfoBarSeverity.Error,
                isClosable: true,
                actionButtonText: CoreTools.Translate("View log"),
                actionButtonAction: OpenUpdateLog);
            MarkAttemptFinished($"archive extraction failed: {ex.Message}");
            return;
        }

        // Locate the UniGetUI.app bundle inside the extracted archive.
        string topLevelApp = Path.Join(stagingDir, "UniGetUI.app");
        string? newApp = Directory.Exists(topLevelApp)
            ? topLevelApp
            : Directory.EnumerateDirectories(stagingDir, "UniGetUI.app", SearchOption.AllDirectories).FirstOrDefault();

        if (newApp is null || !Directory.Exists(newApp))
        {
            LogUpdateError($"Could not find UniGetUI.app inside the extracted archive at {stagingDir}.");
            RaiseStatus(
                CoreTools.Translate("The update could not be applied."),
                CoreTools.Translate("The update package was malformed."),
                InfoBarSeverity.Error,
                isClosable: true,
                actionButtonText: CoreTools.Translate("View log"),
                actionButtonAction: OpenUpdateLog);
            MarkAttemptFinished("UniGetUI.app not found in archive");
            return;
        }

        // Verify the embedded app is signed by Devolutions before trusting it.
        if (!VerifyMacAppSignature(newApp))
        {
            LogUpdateError("The extracted app failed signature validation. Aborting update.");
            RaiseStatus(
                CoreTools.Translate("The installer authenticity could not be verified."),
                CoreTools.Translate("The update process has been aborted."),
                InfoBarSeverity.Error,
                isClosable: true,
                actionButtonText: CoreTools.Translate("View log"),
                actionButtonAction: OpenUpdateLog);
            MarkAttemptFinished("extracted app signature invalid");
            return;
        }

        // Replace the *running* bundle wherever it lives, falling back to /Applications.
        string target = ResolveRunningMacAppBundle() ?? "/Applications/UniGetUI.app";

        if (!Directory.Exists(target))
        {
            LogUpdateWarn(
                $"No installed .app bundle found at {target} (running from {Environment.ProcessPath ?? "unknown"}); "
                + "the running copy will not be replaced."
            );
            ReportRunningCopyNotReplaced("no installed bundle to replace");
            return;
        }

        // Guard against clobbering a development build (a published .app sitting in a
        // build/publish output tree).
        if (LooksLikeDevBuild(string.Empty, target))
        {
            LogUpdateWarn($"Resolved bundle '{target}' looks like a development build; the running copy will not be replaced.");
            ReportRunningCopyNotReplaced("development build detected; running copy not replaced");
            return;
        }

        LogUpdateInfo($"Replacing {target} with the freshly-extracted bundle and relaunching.");

        // The swap is handed to a detached helper that waits for THIS process to exit
        // before touching the bundle, so the running app never has its files yanked out
        // from under it. On success the helper relaunches the new bundle; on any failure
        // it rolls back and relaunches whatever remains. We deliberately do NOT write the
        // "attempt finished" marker here — exactly like the Windows installer path, the
        // relaunched copy confirms success via CheckForOrphanedUpdateAttempt() by
        // comparing its own version against the recorded target version.
        //
        // Arguments are passed positionally ($1=pid, $2=target, $3=new app) so no path
        // is ever interpolated into the script text.
        const string swap = """
            pid="$1"; target="$2"; newapp="$3"
            i=0
            while kill -0 "$pid" 2>/dev/null && [ "$i" -lt 150 ]; do sleep 0.2; i=$((i+1)); done
            rm -rf "$target.old"
            if mv "$target" "$target.old"; then
              if mv "$newapp" "$target"; then
                xattr -dr com.apple.quarantine "$target" 2>/dev/null
                rm -rf "$target.old"
              else
                rm -rf "$target"
                mv "$target.old" "$target"
              fi
            fi
            /usr/bin/open -na "$target"
            """;
        if (!TrySpawnSwapHelper(swap, Environment.ProcessId.ToString(CultureInfo.InvariantCulture), target, newApp))
        {
            // We could not even launch the helper, so nothing was changed. Report and bail
            // without exiting so the user keeps a working copy.
            RaiseStatus(
                CoreTools.Translate("The update could not be applied."),
                CoreTools.Translate("The updater could not be launched."),
                InfoBarSeverity.Error,
                isClosable: true,
                actionButtonText: CoreTools.Translate("View log"),
                actionButtonAction: OpenUpdateLog);
            MarkAttemptFinished("could not spawn swap helper");
            return;
        }

        // Match the Windows flow: terminate the running copy so the helper can replace
        // the bundle and relaunch the freshly-installed version.
        Environment.Exit(0);
    }

    [SupportedOSPlatform("linux")]
    private static async Task LaunchLinuxInstallerAsync(string installerLocation)
    {
        LogUpdateInfo($"Applying Linux update from archive: {installerLocation}");

        RaiseStatus(
            CoreTools.Translate("UniGetUI is being updated..."),
            CoreTools.Translate("This may take a minute or two"),
            InfoBarSeverity.Informational,
            isClosable: false);

        // The directory holding the running executable is the install location to replace.
        string? exePath = Environment.ProcessPath;
        string? installDir = Path.GetDirectoryName(exePath);
        if (string.IsNullOrEmpty(exePath) || string.IsNullOrEmpty(installDir))
        {
            LogUpdateWarn(
                $"Could not resolve the running install directory (ProcessPath='{exePath}'); "
                + "the running copy will not be replaced."
            );
            ReportRunningCopyNotReplaced("could not resolve install directory; running copy not replaced");
            return;
        }

        string exeName = Path.GetFileName(exePath);

        // Guard against clobbering a development build: when running through the `dotnet`
        // host or from a build/publish output tree, do not swap the directory in place.
        if (LooksLikeDevBuild(exeName, installDir))
        {
            LogUpdateWarn($"Running from what looks like a development build ('{exePath}'); the running copy will not be replaced.");
            ReportRunningCopyNotReplaced("development build detected; running copy not replaced");
            return;
        }

        string stagingDir;
        try
        {
            stagingDir = await Task.Run(() => ExtractTarGz(installerLocation));
        }
        catch (Exception ex)
        {
            LogUpdateError("Failed to extract the Linux update archive:");
            LogUpdateError(ex);
            RaiseStatus(
                CoreTools.Translate("The update could not be applied."),
                ex.Message,
                InfoBarSeverity.Error,
                isClosable: true,
                actionButtonText: CoreTools.Translate("View log"),
                actionButtonAction: OpenUpdateLog);
            MarkAttemptFinished($"archive extraction failed: {ex.Message}");
            return;
        }

        // The new install tree is either the single top-level directory inside the
        // archive, or the staging directory itself if the executable sits at the root.
        string newRoot = ResolveExtractedLinuxRoot(stagingDir);

        // Confirm the archive actually contains the executable we expect before we
        // commit to swapping the install directory.
        if (!File.Exists(Path.Join(newRoot, exeName)))
        {
            LogUpdateError($"Expected executable '{exeName}' was not found in the extracted archive at {newRoot}.");
            RaiseStatus(
                CoreTools.Translate("The update could not be applied."),
                CoreTools.Translate("The update package was malformed."),
                InfoBarSeverity.Error,
                isClosable: true,
                actionButtonText: CoreTools.Translate("View log"),
                actionButtonAction: OpenUpdateLog);
            MarkAttemptFinished("executable not found in archive");
            return;
        }

        LogUpdateInfo($"Replacing install directory {installDir} with the freshly-extracted tree and relaunching.");

        // Hand the swap to a detached helper that waits for THIS process to exit before
        // renaming the install directory (renaming, not overwriting, avoids "text file
        // busy" on the running executable and keeps the running process's mapped files
        // intact until it is gone). On failure the helper rolls back. As with Windows,
        // success/failure is confirmed by the relaunched copy via
        // CheckForOrphanedUpdateAttempt() — so no "attempt finished" marker is written here.
        //
        // Positional args: $1=pid, $2=install dir, $3=new tree, $4=executable name.
        const string swap = """
            pid="$1"; dir="$2"; newroot="$3"; exe="$4"
            i=0
            while kill -0 "$pid" 2>/dev/null && [ "$i" -lt 150 ]; do sleep 0.2; i=$((i+1)); done
            rm -rf "$dir.old"
            if mv "$dir" "$dir.old"; then
              if mv "$newroot" "$dir"; then
                chmod +x "$dir/$exe" 2>/dev/null
                rm -rf "$dir.old"
              else
                rm -rf "$dir"
                mv "$dir.old" "$dir"
              fi
            fi
            "$dir/$exe" >/dev/null 2>&1 &
            """;
        if (!TrySpawnSwapHelper(swap, Environment.ProcessId.ToString(CultureInfo.InvariantCulture), installDir, newRoot, exeName))
        {
            RaiseStatus(
                CoreTools.Translate("The update could not be applied."),
                CoreTools.Translate("The updater could not be launched."),
                InfoBarSeverity.Error,
                isClosable: true,
                actionButtonText: CoreTools.Translate("View log"),
                actionButtonAction: OpenUpdateLog);
            MarkAttemptFinished("could not spawn swap helper");
            return;
        }

        Environment.Exit(0);
    }

    // Shows the friendly "updated, but this running copy was not replaced" notice used
    // for development builds and unresolved install locations, and finishes the attempt.
    private static void ReportRunningCopyNotReplaced(string outcome)
    {
        RaiseStatus(
            CoreTools.Translate("Update installed."),
            CoreTools.Translate("UniGetUI was updated successfully, but this running copy was not replaced. This usually means you are running a development build. Close this copy and start the newly-installed version to finish."),
            InfoBarSeverity.Warning,
            isClosable: true,
            actionButtonText: CoreTools.Translate("View log"),
            actionButtonAction: OpenUpdateLog);
        MarkAttemptFinished(outcome);
    }

    // True when the running process looks like a development build rather than an
    // installed copy that should replace itself in place.
    private static bool LooksLikeDevBuild(string exeName, string installDir)
    {
        if (exeName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            return true;

        char sep = Path.DirectorySeparatorChar;
        return installDir.Contains($"{sep}bin{sep}Debug{sep}", StringComparison.OrdinalIgnoreCase)
            || installDir.Contains($"{sep}bin{sep}Release{sep}", StringComparison.OrdinalIgnoreCase)
            || installDir.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase);
    }

    // Launches the detached /bin/sh helper that performs the file swap after this
    // process exits. Returns false if the helper process could not be started.
    private static bool TrySpawnSwapHelper(string script, params string[] positionalArgs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(script);
            psi.ArgumentList.Add("sh"); // becomes $0 inside the script
            foreach (string arg in positionalArgs)
            {
                psi.ArgumentList.Add(arg);
            }

            Process? p = Process.Start(psi);
            return p is not null;
        }
        catch (Exception ex)
        {
            LogUpdateError("Could not spawn the update swap helper:");
            LogUpdateError(ex);
            return false;
        }
    }

    // ------------------------------------------------------------------ archive helpers
    // Extracts a .tar.gz into a fresh per-attempt staging directory and returns its path.
    private static string ExtractTarGz(string tarballPath)
    {
        string stagingRoot = Path.Join(CoreData.UniGetUIDataDirectory, "update-staging");
        Directory.CreateDirectory(stagingRoot);
        PruneStaleStagingDirs(stagingRoot);

        // A unique subdirectory per attempt: update checks can run concurrently (the
        // background loop and a manual check), so a single shared directory would let two
        // attempts delete/overwrite each other's contents mid-extraction.
        string stagingDir = Path.Join(stagingRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stagingDir);

        string tar = ResolveTarExecutable();
        LogUpdateInfo($"Extracting {tarballPath} -> {stagingDir} (using '{tar}')");

        // Use the system `tar` rather than System.Formats.Tar: the published archives use
        // PAX extended headers that the managed TarReader rejects ("extended header
        // contains invalid records"), whereas bsdtar (macOS) and GNU tar (Linux) extract
        // them cleanly while restoring Unix permissions and symlinks.
        using Process p = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = tar,
                ArgumentList = { "-xzf", tarballPath, "-C", stagingDir },
                UseShellExecute = false,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        p.Start();
        string stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"tar exited with code {p.ExitCode} while extracting the update archive. {stderr.Trim()}");
        }
        return stagingDir;
    }

    // Resolves the `tar` executable. Prefers the standard FHS locations, then defers to a
    // bare name so the OS resolves it from PATH (covers Nix and other non-FHS layouts;
    // Process searches PATH for an unrooted file name when UseShellExecute is false).
    private static string ResolveTarExecutable()
    {
        foreach (string candidate in new[] { "/usr/bin/tar", "/bin/tar" })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return "tar";
    }

    // Best-effort removal of staging directories left behind by previous runs. Only
    // touches directories old enough that no in-flight attempt could still be using them,
    // so it never races a concurrent extraction.
    private static void PruneStaleStagingDirs(string stagingRoot)
    {
        try
        {
            foreach (string dir in Directory.EnumerateDirectories(stagingRoot))
            {
                try
                {
                    if (DateTime.Now - Directory.GetLastWriteTime(dir) > TimeSpan.FromHours(1))
                    {
                        Directory.Delete(dir, recursive: true);
                    }
                }
                catch { /* leftover will be retried next time */ }
            }
        }
        catch (Exception ex)
        {
            LogUpdateWarn($"Could not prune stale staging directories: {ex.Message}");
        }
    }

    // If the archive wraps everything in a single top-level directory, return it;
    // otherwise the staging directory itself is the install root.
    private static string ResolveExtractedLinuxRoot(string stagingDir)
    {
        string[] entries = Directory.GetFileSystemEntries(stagingDir);
        if (entries.Length == 1 && Directory.Exists(entries[0]))
        {
            return entries[0];
        }
        return stagingDir;
    }

    // Walks up from the running executable to the enclosing ".app" bundle directory.
    [SupportedOSPlatform("macos")]
    private static string? ResolveRunningMacAppBundle()
    {
        // Environment.ProcessPath is typically /Applications/UniGetUI.app/Contents/MacOS/UniGetUI.
        string? dir = Path.GetDirectoryName(Environment.ProcessPath);
        while (!string.IsNullOrEmpty(dir))
        {
            if (dir.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
            {
                return dir;
            }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
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
            // The downloaded artifact is a .tar.gz archive, which carries no signature of
            // its own. Integrity is guaranteed by the SHA256 hash (verified separately
            // against productinfo.json fetched over HTTPS from a trusted host). The
            // Developer ID of the embedded UniGetUI.app is verified after extraction, in
            // VerifyMacAppSignature(), before the running bundle is replaced.
            LogUpdateDebug("macOS .tar.gz integrity is enforced via hash; the app signature is verified post-extraction.");
            return true;
        }

        if (OperatingSystem.IsLinux())
        {
            // .tar.gz has no built-in signing format equivalent to Authenticode/.pkg.
            // Hash validation (verified separately, against the productinfo.json fetched
            // over HTTPS from a trusted host) provides the integrity guarantee. A future
            // extension could verify a detached GPG signature published alongside the
            // archive in productinfo.
            LogUpdateWarn("Linux .tar.gz signature validation is not implemented — relying on hash check.");
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

    [SupportedOSPlatform("macos")]
    private static bool VerifyMacAppSignature(string appBundlePath)
    {
        if (DEVOLUTIONS_MAC_DEVELOPER_IDS.Length == 0)
        {
            LogUpdateWarn(
                "No Devolutions macOS Developer Team IDs configured — skipping .app signature validation."
            );
            return true;
        }

        // IMPORTANT: `codesign --verify` (with or without --deep/--strict) cannot be used
        // as a gate here. The published self-contained .NET bundle contains nested managed
        // assemblies that are not individually code-signed, so --verify reports "code
        // object is not signed at all" even for a perfectly legitimate, Developer-ID-signed
        // bundle (and `spctl` likewise rejects it). Integrity and authenticity are already
        // guaranteed by the SHA256 hash, which is checked against productinfo.json fetched
        // over HTTPS from a trusted host. Here we additionally read the bundle's Team
        // Identifier as a best-effort signer check: a *mismatch* is treated as tampering
        // and blocks the update; an absent/unreadable signature only warns and defers to
        // the verified download hash.
        try
        {
            using Process info = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/codesign",
                    ArgumentList = { "-dv", "--verbose=4", appBundlePath },
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            info.Start();
            // codesign prints the signing metadata to stderr, but read both streams
            // concurrently so a full pipe on one stream can never deadlock the updater.
            Task<string> stderrTask = info.StandardError.ReadToEndAsync();
            Task<string> stdoutTask = info.StandardOutput.ReadToEndAsync();
            info.WaitForExit();
            string metadata = stderrTask.GetAwaiter().GetResult() + stdoutTask.GetAwaiter().GetResult();

            if (info.ExitCode != 0)
            {
                LogUpdateWarn("Could not read the extracted app's code signature; relying on the verified download hash.");
                return true;
            }

            string? teamId = null;
            foreach (string rawLine in metadata.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("TeamIdentifier=", StringComparison.OrdinalIgnoreCase))
                {
                    teamId = line["TeamIdentifier=".Length..].Trim();
                    break;
                }
            }

            if (string.IsNullOrEmpty(teamId) || teamId.Equals("not set", StringComparison.OrdinalIgnoreCase))
            {
                LogUpdateWarn("Extracted app has no Team Identifier; relying on the verified download hash.");
                return true;
            }

            if (DEVOLUTIONS_MAC_DEVELOPER_IDS.Contains(teamId, StringComparer.OrdinalIgnoreCase))
            {
                LogUpdateDebug($"Extracted app is signed by trusted Developer Team ID {teamId}.");
                return true;
            }

            LogUpdateWarn($"Extracted app is signed by an untrusted Team Identifier '{teamId}'. Aborting update.");
            return false;
        }
        catch (Exception ex)
        {
            LogUpdateWarn("Could not validate the extracted app signature via codesign; relying on the verified download hash.");
            LogUpdateWarn(ex);
            return true;
        }
    }

    // ------------------------------------------------------------------ download
    private static async Task<string> DownloadInstallerAsync(
        string url,
        string destination,
        UpdaterOverrides overrides,
        UpdaterDownloadIdentity identity)
    {
        if (!IsSourceUrlAllowed(url, overrides.AllowUnsafeUrls))
        {
            throw new InvalidOperationException($"Download URL is not allowed: {url}");
        }

        LogUpdateDebug($"Downloading installer from {url}");
        using HttpClient client = new(CreateHttpClientHandler(overrides));
        client.Timeout = TimeSpan.FromSeconds(600);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
        using CancellationTokenSource downloadTimeout = new(TimeSpan.FromSeconds(600));

        UpdaterDownloadResult result = await UpdaterDownloadEngine.DownloadInstallerPartAsync(
            client,
            identity,
            destination,
            message => LogUpdateDebug(message),
            downloadTimeout.Token
        );

        LogUpdateDebug("Installer download complete.");
        return result.PartialPath;
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

        // Note: macOS and Linux both publish "tar.gz" artifacts, so the Type field
        // alone is ambiguous between the two. Disambiguate on the platform token that
        // is embedded in the download URL (e.g. "...macos-arm64..." vs "...linux-x64...").
        if (OperatingSystem.IsMacOS())
        {
            ProductInfoFile? mac =
                files.FirstOrDefault(f => IsTarGzFor(f, "macos", arch))
                ?? files.FirstOrDefault(f => IsTarGzFor(f, "macos", "universal"))
                ?? files.FirstOrDefault(f => IsTarGzFor(f, "macos", "Any"));

            return mac ?? throw new PlatformArtifactMissingException(
                $"No compatible macOS package (.tar.gz) found in productinfo for architecture '{arch}'"
            );
        }

        if (OperatingSystem.IsLinux())
        {
            ProductInfoFile? linux =
                files.FirstOrDefault(f => IsTarGzFor(f, "linux", arch))
                ?? files.FirstOrDefault(f => IsTarGzFor(f, "linux", "universal"))
                ?? files.FirstOrDefault(f => IsTarGzFor(f, "linux", "Any"));

            return linux ?? throw new PlatformArtifactMissingException(
                $"No compatible Linux package (.tar.gz) found in productinfo for architecture '{arch}'"
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

    // Matches a .tar.gz artifact for a given OS (identified by a token in the URL,
    // since macOS and Linux share the same "tar.gz" Type) and processor architecture.
    private static bool IsTarGzFor(ProductInfoFile f, string osToken, string arch) =>
        f.Type.Equals("tar.gz", StringComparison.OrdinalIgnoreCase)
        && f.Arch.Equals(arch, StringComparison.OrdinalIgnoreCase)
        && f.Url.Contains(osToken, StringComparison.OrdinalIgnoreCase);

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

    // Normalize trailing zero components so "2026.1.11" and "2026.1.11.0" compare equal.
    private static bool VersionsMatch(string a, string b)
    {
        string sa = a.Trim().TrimStart('v', 'V');
        string sb = b.Trim().TrimStart('v', 'V');

        if (Version.TryParse(sa, out Version? va) && Version.TryParse(sb, out Version? vb))
        {
            return CoreTools.NormalizeVersionForComparison(va)
                .Equals(CoreTools.NormalizeVersionForComparison(vb));
        }

        return string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase);
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
