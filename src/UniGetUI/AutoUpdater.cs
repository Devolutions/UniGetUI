using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;

namespace UniGetUI;

public partial class AutoUpdater
{
    private static readonly string[] DEVOLUTIONS_CERT_THUMBPRINTS =
    [
        "3f5202a9432d54293bdfe6f7e46adb0a6f8b3ba6",
        "8db5a43bb8afe4d2ffb92da9007d8997a4cc4e13",
        "50f753333811ff11f1920274afde3ffd4468b210",
    ];

    private static readonly AutoUpdaterJsonContext ProductInfoJsonContext = new(
        new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
    );

    public static Window Window = null!;
    public static InfoBar Banner = null!;

    //------------------------------------------------------------------------------------------------------------------
    public static bool ReleaseLockForAutoupdate_Notification;
    public static bool ReleaseLockForAutoupdate_Window;
    public static bool ReleaseLockForAutoupdate_UpdateBanner;
    public static bool UpdateReadyToBeInstalled { get; private set; }

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
                .AppendLine($"UI: WinUI")
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
    // whether the previous attempt completed cleanly or was killed mid-flow.
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
    /// Caller must have already assigned <see cref="Window"/> and
    /// <see cref="Banner"/> so the banner can render.
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

            ShowMessage_ThreadSafe(
                CoreTools.Translate("Your last update attempt did not complete."),
                CoreTools.Translate("UniGetUI could not confirm whether the update succeeded. Open the log to see what happened."),
                InfoBarSeverity.Warning,
                true,
                CreateViewLogButton()
            );
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

    private static Button CreateViewLogButton()
    {
        var btn = new Button { Content = CoreTools.Translate("View log") };
        btn.Click += (_, _) => OpenUpdateLog();
        return btn;
    }

    public static async Task UpdateCheckLoop(Window window, InfoBar banner)
    {
        Window = window;
        Banner = banner;

        // If the previous update attempt was killed mid-flow (typically by the
        // installer terminating us during file replacement), surface a banner
        // before either entering or short-circuiting the auto-update loop.
        CheckForOrphanedUpdateAttempt();

        if (Settings.Get(Settings.K.DisableAutoUpdateWingetUI))
        {
            Logger.Warn("User has disabled updates");
            return;
        }

        bool IsFirstLaunch = true;

        await CoreTools.WaitForInternetConnection();
        while (true)
        {
            // User could have disabled updates on runtime
            if (Settings.Get(Settings.K.DisableAutoUpdateWingetUI))
            {
                Logger.Warn("User has disabled updates");
                return;
            }
            bool updateSucceeded = await CheckAndInstallUpdates(
                window,
                banner,
                false,
                IsFirstLaunch
            );
            IsFirstLaunch = false;
            await Task.Delay(TimeSpan.FromMinutes(updateSucceeded ? 60 : 10));
        }
    }

    /// <summary>
    /// Performs the entire update process, and returns true/false whether the process finished successfully;
    /// </summary>
    public static async Task<bool> CheckAndInstallUpdates(
        Window window,
        InfoBar banner,
        bool Verbose,
        bool AutoLaunch = false,
        bool ManualCheck = false
    )
    {
        Window = window;
        Banner = banner;
        bool WasCheckingForUpdates = true;
        ResetUpdateLog(ManualCheck, AutoLaunch);
        UpdaterOverrides updaterOverrides = LoadUpdaterOverrides();

        try
        {
            if (Verbose)
                ShowMessage_ThreadSafe(
                    CoreTools.Translate("We are checking for updates."),
                    CoreTools.Translate("Please wait"),
                    InfoBarSeverity.Informational,
                    false
                );

            // Check for updates
            UpdateCandidate updateCandidate = await GetUpdateCandidate(updaterOverrides);
            LogUpdateInfo(
                $"Updater source '{updateCandidate.SourceName}' returned version {updateCandidate.VersionName} (upgradable={updateCandidate.IsUpgradable})"
            );

            if (updateCandidate.IsUpgradable)
            {
                WasCheckingForUpdates = false;
                RecordTargetVersion(updateCandidate.VersionName);
                LogUpdateInfo(
                    $"An update to UniGetUI version {updateCandidate.VersionName} is available"
                );
                string InstallerPath = Path.Join(
                    CoreData.UniGetUIDataDirectory,
                    "UniGetUI Updater.exe"
                );

                if (
                    File.Exists(InstallerPath)
                    && await CheckInstallerHash(
                        InstallerPath,
                        updateCandidate.InstallerHash,
                        updaterOverrides
                    )
                    && CheckInstallerSignerThumbprint(InstallerPath, updaterOverrides)
                )
                {
                    LogUpdateInfo($"A cached valid installer was found, launching update process...");
                    return await PrepairToLaunchInstaller(
                        InstallerPath,
                        updateCandidate.VersionName,
                        AutoLaunch,
                        ManualCheck
                    );
                }

                File.Delete(InstallerPath);

                ShowMessage_ThreadSafe(
                    CoreTools.Translate(
                        "UniGetUI version {0} is being downloaded.",
                        updateCandidate.VersionName.ToString(CultureInfo.InvariantCulture)
                    ),
                    CoreTools.Translate("This may take a minute or two"),
                    InfoBarSeverity.Informational,
                    false
                );

                // Download the installer
                await DownloadInstaller(
                    updateCandidate.InstallerDownloadUrl,
                    InstallerPath,
                    updaterOverrides
                );

                if (
                    await CheckInstallerHash(
                        InstallerPath,
                        updateCandidate.InstallerHash,
                        updaterOverrides
                    ) && CheckInstallerSignerThumbprint(InstallerPath, updaterOverrides)
                )
                {
                    LogUpdateInfo("The downloaded installer is valid, launching update process...");
                    return await PrepairToLaunchInstaller(
                        InstallerPath,
                        updateCandidate.VersionName,
                        AutoLaunch,
                        ManualCheck
                    );
                }

                ShowMessage_ThreadSafe(
                    CoreTools.Translate("The installer authenticity could not be verified."),
                    CoreTools.Translate("The update process has been aborted."),
                    InfoBarSeverity.Error,
                    true,
                    CreateViewLogButton()
                );
                MarkAttemptFinished("authenticity verification failed");
                return false;
            }

            if (Verbose)
                ShowMessage_ThreadSafe(
                    CoreTools.Translate("Great! You are on the latest version."),
                    CoreTools.Translate("There are no new UniGetUI versions to be installed"),
                    InfoBarSeverity.Success,
                    true
                );
            MarkAttemptFinished("no update available");
            return true;
        }
        catch (Exception e)
        {
            LogUpdateError("An error occurred while checking for updates: ");
            LogUpdateError(e);
            // We don't want an error popping if updates can't
            if (Verbose || !WasCheckingForUpdates)
                ShowMessage_ThreadSafe(
                    CoreTools.Translate("An error occurred when checking for updates: "),
                    e.Message,
                    InfoBarSeverity.Error,
                    true,
                    CreateViewLogButton()
                );
            MarkAttemptFinished($"exception: {e.Message}");
            return false;
        }
    }

    private static async Task<UpdateCandidate> GetUpdateCandidate(UpdaterOverrides updaterOverrides)
    {
        return await CheckForUpdatesFromProductInfo(updaterOverrides);
    }

    /// <summary>
    /// Default update source using Devolutions productinfo.json
    /// </summary>
    private static async Task<UpdateCandidate> CheckForUpdatesFromProductInfo(
        UpdaterOverrides updaterOverrides
    )
    {
        LogUpdateDebug(
            $"Begin check for updates on productinfo source {updaterOverrides.ProductInfoUrl}"
        );

        if (!IsSourceUrlAllowed(updaterOverrides.ProductInfoUrl, updaterOverrides.AllowUnsafeUrls))
        {
            throw new InvalidOperationException(
                $"Productinfo URL is not allowed: {updaterOverrides.ProductInfoUrl}"
            );
        }

        string productInfo;
        using (HttpClient client = new(CreateHttpClientHandler(updaterOverrides)))
        {
            client.Timeout = TimeSpan.FromSeconds(600);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
            productInfo = await client.GetStringAsync(updaterOverrides.ProductInfoUrl);
        }

        Dictionary<string, ProductInfoProduct>? productInfoRoot =
            JsonSerializer.Deserialize(
                productInfo,
                typeof(Dictionary<string, ProductInfoProduct>),
                ProductInfoJsonContext
            ) as Dictionary<string, ProductInfoProduct>;
        if (productInfoRoot is null || productInfoRoot.Count == 0)
        {
            throw new FormatException("productinfo.json content is empty or invalid");
        }

        if (
            !productInfoRoot.TryGetValue(
                updaterOverrides.ProductInfoProductKey,
                out ProductInfoProduct? product
            )
        )
        {
            throw new KeyNotFoundException(
                $"Product '{updaterOverrides.ProductInfoProductKey}' was not found in productinfo.json"
            );
        }

        ProductInfoChannel? channel = Settings.Get(Settings.K.EnableUniGetUIBeta)
            ? product.Beta
            : product.Current;
        if (channel is null)
        {
            string missingChannel = Settings.Get(Settings.K.EnableUniGetUIBeta)
                ? "Beta"
                : "Current";
            throw new KeyNotFoundException(
                $"Channel '{missingChannel}' was not found for product '{updaterOverrides.ProductInfoProductKey}'"
            );
        }

        ProductInfoFile installerFile = SelectInstallerFile(channel.Files);
        if (!IsSourceUrlAllowed(installerFile.Url, updaterOverrides.AllowUnsafeUrls))
        {
            throw new InvalidOperationException(
                $"Installer URL is not allowed: {installerFile.Url}"
            );
        }

        Version currentVersion = ParseVersionOrFallback(
            CoreData.VersionName,
            new Version(0, 0, 0, CoreData.BuildNumber)
        );
        Version availableVersion = ParseVersionOrFallback(channel.Version, new Version(0, 0, 0, 0));

        bool isUpgradable = availableVersion > currentVersion;
        LogUpdateDebug(
            $"Productinfo check result: current={currentVersion}, available={availableVersion}, upgradable={isUpgradable}"
        );

        return new UpdateCandidate(
            isUpgradable,
            channel.Version,
            installerFile.Hash,
            installerFile.Url,
            "ProductInfo"
        );
    }

    /// <summary>
    /// Checks whether the downloaded updater matches the hash.
    /// </summary>
    private static async Task<bool> CheckInstallerHash(
        string installerLocation,
        string expectedHash,
        UpdaterOverrides updaterOverrides
    )
    {
        if (updaterOverrides.SkipHashValidation)
        {
            LogUpdateWarn("Registry override enabled: skipping updater hash validation.");
            return true;
        }

        LogUpdateDebug($"Checking updater hash on location {installerLocation}");
        using (FileStream stream = File.OpenRead(installerLocation))
        {
            string hash = Convert
                .ToHexString(await SHA256.Create().ComputeHashAsync(stream))
                .ToLower();
            if (hash == expectedHash.ToLower())
            {
                LogUpdateDebug($"The hashes match ({hash})");
                return true;
            }
            LogUpdateWarn($"Hash mismatch.\nExpected: {expectedHash}\nGot:      {hash}");
            return false;
        }
    }

    private static bool CheckInstallerSignerThumbprint(
        string installerLocation,
        UpdaterOverrides updaterOverrides
    )
    {
        if (updaterOverrides.SkipSignerThumbprintCheck)
        {
            LogUpdateWarn(
                "Registry override enabled: skipping updater signer thumbprint validation."
            );
            return true;
        }

        try
        {
#pragma warning disable SYSLIB0057
            X509Certificate signerCertificate = X509Certificate.CreateFromSignedFile(
                installerLocation
            );
#pragma warning restore SYSLIB0057
            using X509Certificate2 cert = new(signerCertificate);

            string signerThumbprint = NormalizeThumbprint(cert.Thumbprint ?? string.Empty);
            if (string.IsNullOrWhiteSpace(signerThumbprint))
            {
                LogUpdateWarn(
                    $"Could not read signer thumbprint for installer '{installerLocation}'"
                );
                return false;
            }

            if (
                DEVOLUTIONS_CERT_THUMBPRINTS.Contains(
                    signerThumbprint,
                    StringComparer.OrdinalIgnoreCase
                )
            )
            {
                LogUpdateDebug($"Installer signer thumbprint is trusted: {signerThumbprint}");
                return true;
            }

            LogUpdateWarn($"Installer signer thumbprint is not trusted. Got: {signerThumbprint}");
            return false;
        }
        catch (Exception ex)
        {
            LogUpdateWarn("Could not validate installer signer thumbprint");
            LogUpdateWarn(ex);
            return false;
        }
    }

    /// <summary>
    /// Downloads the given installer to the given location
    /// </summary>
    private static async Task DownloadInstaller(
        string downloadUrl,
        string installerLocation,
        UpdaterOverrides updaterOverrides
    )
    {
        if (!IsSourceUrlAllowed(downloadUrl, updaterOverrides.AllowUnsafeUrls))
        {
            throw new InvalidOperationException($"Download URL is not allowed: {downloadUrl}");
        }

        LogUpdateDebug($"Downloading installer from {downloadUrl} to {installerLocation}");
        using (HttpClient client = new(CreateHttpClientHandler(updaterOverrides)))
        {
            client.Timeout = TimeSpan.FromSeconds(600);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
            HttpResponseMessage result = await client.GetAsync(downloadUrl);
            result.EnsureSuccessStatusCode();
            using FileStream fs = new(installerLocation, FileMode.OpenOrCreate);
            await result.Content.CopyToAsync(fs);
        }
        LogUpdateDebug("The download has finished successfully");
    }

    /// <summary>
    /// Waits for the window to be closed if it is open and launches the updater
    /// </summary>
    private static async Task<bool> PrepairToLaunchInstaller(
        string installerLocation,
        string NewVersion,
        bool AutoLaunch,
        bool ManualCheck
    )
    {
        LogUpdateDebug("Starting the process to launch the installer.");
        UpdateReadyToBeInstalled = true;
        ReleaseLockForAutoupdate_Window = false;
        ReleaseLockForAutoupdate_Notification = false;
        ReleaseLockForAutoupdate_UpdateBanner = false;

        // Check if the user has disabled updates
        if (!ManualCheck && Settings.Get(Settings.K.DisableAutoUpdateWingetUI))
        {
            // Banner is a UI element; always touch it from the UI thread.
            Window.DispatcherQueue.TryEnqueue(() => Banner.IsOpen = false);
            LogUpdateWarn("User disabled updates!");
            MarkAttemptFinished("aborted - auto-update disabled before launch");
            return true;
        }

        Window.DispatcherQueue.TryEnqueue(() =>
        {
            // Set the banner to Restart UniGetUI to update
            var UpdateNowButton = new Button { Content = CoreTools.Translate("Update now") };
            UpdateNowButton.Click += (_, _) => ReleaseLockForAutoupdate_UpdateBanner = true;
            ShowMessage_ThreadSafe(
                CoreTools.Translate("UniGetUI {0} is ready to be installed.", NewVersion),
                CoreTools.Translate("The update process will start after closing UniGetUI"),
                InfoBarSeverity.Success,
                true,
                UpdateNowButton
            );

            // Show a toast notification
            AppNotificationBuilder builder = new AppNotificationBuilder()
                .SetScenario(AppNotificationScenario.Default)
                .SetTag(CoreData.UniGetUICanBeUpdated.ToString())
                .AddText(
                    CoreTools.Translate("{0} can be updated to version {1}", "UniGetUI", NewVersion)
                )
                .SetAttributionText(
                    CoreTools.Translate(
                        "You have currently version {0} installed",
                        CoreData.VersionName
                    )
                )
                .AddArgument("action", NotificationArguments.Show)
                .AddButton(
                    new AppNotificationButton(CoreTools.Translate("Update now")).AddArgument(
                        "action",
                        NotificationArguments.ReleaseSelfUpdateLock
                    )
                );
            AppNotification notification = builder.BuildNotification();
            notification.ExpiresOnReboot = true;
            AppNotificationManager.Default.Show(notification);
        });

        if (AutoLaunch && !Window.Visible)
        {
            LogUpdateDebug("AutoLaunch is enabled and the Window is hidden, launching installer...");
        }
        else
        {
            LogUpdateDebug(
                "Waiting for mainWindow to be closed or for user to trigger the update from the notification..."
            );
            while (
                !(ReleaseLockForAutoupdate_Window && !ManualCheck)
                && !ReleaseLockForAutoupdate_Notification
                && !ReleaseLockForAutoupdate_UpdateBanner
            )
            {
                await Task.Delay(100);
            }
            LogUpdateDebug("Autoupdater lock released, launching installer...");
        }

        if (!ManualCheck && Settings.Get(Settings.K.DisableAutoUpdateWingetUI))
        {
            LogUpdateWarn("User has disabled updates");
            MarkAttemptFinished("aborted - auto-update disabled while waiting");
            return true;
        }

        await LaunchInstallerAndQuit(installerLocation);
        return true;
    }

    /// <summary>
    /// Launches the installer located on the installerLocation argument. The installer
    /// is expected to terminate UniGetUI before file replacement; if it returns control
    /// to us, we surface the exit code so the user has something concrete to act on.
    /// </summary>
    private static async Task LaunchInstallerAndQuit(string installerLocation)
    {
        LogUpdateInfo($"Launching installer: {installerLocation}");
        using Process p = new()
        {
            StartInfo = new()
            {
                FileName = installerLocation,
                Arguments =
                    "/SILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NoVCRedist /NoEdgeWebView /NoWinGet",
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
            ShowMessage_ThreadSafe(
                CoreTools.Translate("The updater could not be launched."),
                ex.Message,
                InfoBarSeverity.Error,
                true,
                CreateViewLogButton()
            );
            MarkAttemptFinished($"installer launch threw: {ex.Message}");
            return;
        }

        if (!started)
        {
            LogUpdateError("Failed to start installer process (Process.Start returned false).");
            ShowMessage_ThreadSafe(
                CoreTools.Translate("The updater could not be launched."),
                CoreTools.Translate("The operating system did not start the installer process."),
                InfoBarSeverity.Error,
                true,
                CreateViewLogButton()
            );
            MarkAttemptFinished("Process.Start returned false");
            return;
        }

        LogUpdateInfo($"Installer process started (PID {p.Id}). The installer is expected to terminate UniGetUI before file replacement.");

        ShowMessage_ThreadSafe(
            CoreTools.Translate("UniGetUI is being updated..."),
            CoreTools.Translate("This may take a minute or two"),
            InfoBarSeverity.Informational,
            false
        );

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

            ShowMessage_ThreadSafe(
                CoreTools.Translate("Update installed."),
                CoreTools.Translate("UniGetUI was updated successfully, but this running copy was not replaced. This usually means you are running a development build. Close this copy and start the newly-installed version to finish."),
                InfoBarSeverity.Warning,
                true,
                CreateViewLogButton()
            );
            MarkAttemptFinished("installer succeeded but did not replace running copy");
            return;
        }

        LogUpdateError($"Installer exited with code {exitCode} ({reason}) without restarting UniGetUI.");

        ShowMessage_ThreadSafe(
            CoreTools.Translate("The update could not be applied."),
            CoreTools.Translate("Installer exit code {0}: {1}", exitCode, reason),
            InfoBarSeverity.Error,
            true,
            CreateViewLogButton()
        );
        MarkAttemptFinished($"installer failed with code {exitCode}");
    }

    private static void ShowMessage_ThreadSafe(
        string Title,
        string Message,
        InfoBarSeverity MessageSeverity,
        bool BannerClosable,
        Button? ActionButton = null
    )
    {
        try
        {
            if (Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() is null)
            {
                Window.DispatcherQueue.TryEnqueue(() =>
                    ShowMessage_ThreadSafe(
                        Title,
                        Message,
                        MessageSeverity,
                        BannerClosable,
                        ActionButton
                    )
                );
                return;
            }

            Banner.Title = Title;
            Banner.Message = Message;
            Banner.Severity = MessageSeverity;
            Banner.IsClosable = BannerClosable;
            Banner.ActionButton = ActionButton;
            Banner.IsOpen = true;
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
        }
    }

}
