using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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

    public static async Task UpdateCheckLoop(Window window, InfoBar banner)
    {
        if (Settings.Get(Settings.K.DisableAutoUpdateWingetUI))
        {
            Logger.Warn("User has disabled updates");
            return;
        }

        bool IsFirstLaunch = true;
        Window = window;
        Banner = banner;

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
            Logger.Info(
                $"Updater source '{updateCandidate.SourceName}' returned version {updateCandidate.VersionName} (upgradable={updateCandidate.IsUpgradable})"
            );

            if (updateCandidate.IsUpgradable)
            {
                WasCheckingForUpdates = false;
                Logger.Info(
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
                    Logger.Info($"A cached valid installer was found, launching update process...");
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
                    Logger.Info("The downloaded installer is valid, launching update process...");
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
                    true
                );
                return false;
            }

            if (Verbose)
                ShowMessage_ThreadSafe(
                    CoreTools.Translate("Great! You are on the latest version."),
                    CoreTools.Translate("There are no new UniGetUI versions to be installed"),
                    InfoBarSeverity.Success,
                    true
                );
            return true;
        }
        catch (Exception e)
        {
            Logger.Error("An error occurred while checking for updates: ");
            Logger.Error(e);
            // We don't want an error popping if updates can't
            if (Verbose || !WasCheckingForUpdates)
                ShowMessage_ThreadSafe(
                    CoreTools.Translate("An error occurred when checking for updates: "),
                    e.Message,
                    InfoBarSeverity.Error,
                    true
                );
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
        Logger.Debug(
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
        Logger.Debug(
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
            Logger.Warn("Registry override enabled: skipping updater hash validation.");
            return true;
        }

        Logger.Debug($"Checking updater hash on location {installerLocation}");
        using (FileStream stream = File.OpenRead(installerLocation))
        {
            string hash = Convert
                .ToHexString(await SHA256.Create().ComputeHashAsync(stream))
                .ToLower();
            if (hash == expectedHash.ToLower())
            {
                Logger.Debug($"The hashes match ({hash})");
                return true;
            }
            Logger.Warn($"Hash mismatch.\nExpected: {expectedHash}\nGot:      {hash}");
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
            Logger.Warn(
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
                Logger.Warn(
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
                Logger.Debug($"Installer signer thumbprint is trusted: {signerThumbprint}");
                return true;
            }

            Logger.Warn($"Installer signer thumbprint is not trusted. Got: {signerThumbprint}");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not validate installer signer thumbprint");
            Logger.Warn(ex);
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

        Logger.Debug($"Downloading installer from {downloadUrl} to {installerLocation}");
        using (HttpClient client = new(CreateHttpClientHandler(updaterOverrides)))
        {
            client.Timeout = TimeSpan.FromSeconds(600);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
            HttpResponseMessage result = await client.GetAsync(downloadUrl);
            result.EnsureSuccessStatusCode();
            using FileStream fs = new(installerLocation, FileMode.OpenOrCreate);
            await result.Content.CopyToAsync(fs);
        }
        Logger.Debug("The download has finished successfully");
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
        Logger.Debug("Starting the process to launch the installer.");
        UpdateReadyToBeInstalled = true;
        ReleaseLockForAutoupdate_Window = false;
        ReleaseLockForAutoupdate_Notification = false;
        ReleaseLockForAutoupdate_UpdateBanner = false;

        // Check if the user has disabled updates
        if (!ManualCheck && Settings.Get(Settings.K.DisableAutoUpdateWingetUI))
        {
            Banner.IsOpen = false;
            Logger.Warn("User disabled updates!");
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
            Logger.Debug("AutoLaunch is enabled and the Window is hidden, launching installer...");
        }
        else
        {
            Logger.Debug(
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
            Logger.Debug("Autoupdater lock released, launching installer...");
        }

        if (!ManualCheck && Settings.Get(Settings.K.DisableAutoUpdateWingetUI))
        {
            Logger.Warn("User has disabled updates");
            return true;
        }

        await LaunchInstallerAndQuit(installerLocation);
        return true;
    }

    /// <summary>
    /// Launches the installer located on the installerLocation argument and quits UniGetUI
    /// </summary>
    private static async Task LaunchInstallerAndQuit(string installerLocation)
    {
        Logger.Debug("Launching the updater...");
        using Process p = new()
        {
            StartInfo = new()
            {
                FileName = installerLocation,
                Arguments =
                    "/SILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NoVCRedist /NoEdgeWebView /NoWinGet /NoChocolatey",
                UseShellExecute = true,
                CreateNoWindow = true,
            },
        };
        p.Start();
        ShowMessage_ThreadSafe(
            CoreTools.Translate("UniGetUI is being updated..."),
            CoreTools.Translate("This may take a minute or two"),
            InfoBarSeverity.Informational,
            false
        );
        await p.WaitForExitAsync();
        ShowMessage_ThreadSafe(
            CoreTools.Translate("Something went wrong while launching the updater."),
            CoreTools.Translate("Please try again later"),
            InfoBarSeverity.Error,
            true
        );
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
