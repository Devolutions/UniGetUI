using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Win32;
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
    public static void TriggerInstall() => _installRequested = true;
    public static async Task UpdateCheckLoopAsync()
    {
        if (Settings.Get(Settings.K.DisableAutoUpdateWingetUI))
        {
            Logger.Warn("Auto-updater: disabled by user setting, skipping.");
            return;
        }

        await CoreTools.WaitForInternetConnection();

        bool isFirstLaunch = true;
        while (true)
        {
            if (Settings.Get(Settings.K.DisableAutoUpdateWingetUI))
            {
                Logger.Warn("Auto-updater: disabled by user setting, stopping loop.");
                return;
            }

            bool success = await CheckAndInstallUpdatesAsync(autoLaunch: isFirstLaunch);
            isFirstLaunch = false;

            await Task.Delay(TimeSpan.FromMinutes(success ? 60 : 10));
        }
    }

    // ------------------------------------------------------------------ core logic
    internal static async Task<bool> CheckAndInstallUpdatesAsync(bool autoLaunch = false)
    {
        UpdaterOverrides overrides = LoadUpdaterOverrides();

        try
        {
            UpdateCandidate candidate = await GetUpdateCandidateAsync(overrides);
            Logger.Info(
                $"Auto-updater source '{candidate.SourceName}' returned version {candidate.VersionName} (upgradable={candidate.IsUpgradable})"
            );

            if (!candidate.IsUpgradable)
            {
                return true;
            }

            Logger.Info($"Update to UniGetUI {candidate.VersionName} is available.");

            string installerPath = Path.Join(CoreData.UniGetUIDataDirectory, "UniGetUI Updater.exe");

            // Try cached installer first
            if (
                File.Exists(installerPath)
                && await CheckInstallerHashAsync(installerPath, candidate.InstallerHash, overrides)
                && CheckInstallerSignerThumbprint(installerPath, overrides)
            )
            {
                Logger.Info("Cached valid installer found, preparing to launch...");
                return await PrepareAndLaunchAsync(installerPath, candidate.VersionName, autoLaunch);
            }

            // Delete invalid/outdated cached copy
            try { File.Delete(installerPath); } catch { }

            Logger.Info("Downloading installer...");
            await DownloadInstallerAsync(candidate.InstallerDownloadUrl, installerPath, overrides);

            if (
                await CheckInstallerHashAsync(installerPath, candidate.InstallerHash, overrides)
                && CheckInstallerSignerThumbprint(installerPath, overrides)
            )
            {
                Logger.Info("Downloaded installer is valid, preparing to launch...");
                return await PrepareAndLaunchAsync(installerPath, candidate.VersionName, autoLaunch);
            }

            Logger.Error("Installer authenticity could not be verified. Aborting update.");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error("An error occurred while checking for updates:");
            Logger.Error(ex);
            return false;
        }
    }

    // ------------------------------------------------------------------ update flow
    private static async Task<bool> PrepareAndLaunchAsync(
        string installerPath,
        string versionName,
        bool autoLaunch)
    {
        _pendingInstallerPath = installerPath;
        _installRequested = false;
        ReleaseLockForAutoupdate_Notification = false;

        // Notify UI (update banner + toast)
        Dispatcher.UIThread.Post(() => UpdateAvailable?.Invoke(versionName));
        WindowsAppNotificationBridge.ShowSelfUpdateAvailableNotification(versionName);

        if (autoLaunch)
        {
            // On first launch in background we wait for user interaction
        }

        // Wait until user requests install, clicks the toast, or the window is being closed
        while (!_installRequested && !ReleaseLockForAutoupdate_Window && !ReleaseLockForAutoupdate_Notification)
        {
            if (Settings.Get(Settings.K.DisableAutoUpdateWingetUI))
            {
                Logger.Warn("Auto-updater: disabled while waiting for user \u2014 aborting.");
                return true;
            }
            await Task.Delay(500);
        }

        Logger.Info("Installing update \u2014 launching installer and quitting.");
        await LaunchInstallerAndQuitAsync(installerPath);
        return true;
    }

    private static async Task LaunchInstallerAndQuitAsync(string installerLocation)
    {
        Logger.Info($"Launching installer: {installerLocation}");
        using Process p = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = installerLocation,
                Arguments = "/SILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NoVCRedist /NoEdgeWebView /NoWinGet /NoChocolatey",
                UseShellExecute = true,
                CreateNoWindow = true,
            },
        };

        bool started = p.Start();
        if (!started)
        {
            Logger.Error("Failed to start installer process.");
            return;
        }

        // Quit the app on the UI thread
        Dispatcher.UIThread.Post(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime lifetime)
            {
                if (lifetime.MainWindow is MainWindow mw)
                {
                    mw.QuitApplication();
                }
                else
                {
                    lifetime.Shutdown();
                }
            }
        });

        await p.WaitForExitAsync();
    }

    // ------------------------------------------------------------------ update check sources
    private static async Task<UpdateCandidate> GetUpdateCandidateAsync(UpdaterOverrides overrides)
    {
        return await CheckFromProductInfoAsync(overrides);
    }

    private static async Task<UpdateCandidate> CheckFromProductInfoAsync(UpdaterOverrides overrides)
    {
        Logger.Debug($"Checking updates via ProductInfo: {overrides.ProductInfoUrl}");

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

        Logger.Debug(
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
            Logger.Warn("Registry override: skipping hash validation.");
            return true;
        }

        using FileStream fs = File.OpenRead(path);
        string actual = Convert
            .ToHexString(await SHA256.Create().ComputeHashAsync(fs))
            .ToLowerInvariant();

        if (actual == expectedHash.ToLowerInvariant())
        {
            Logger.Debug($"Hash match: {actual}");
            return true;
        }

        Logger.Warn($"Hash mismatch. Expected: {expectedHash}  Got: {actual}");
        return false;
    }

    private static bool CheckInstallerSignerThumbprint(string path, UpdaterOverrides overrides)
    {
        if (overrides.SkipSignerThumbprintCheck)
        {
            Logger.Warn("Registry override: skipping signer thumbprint validation.");
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
                Logger.Warn($"Could not read signer thumbprint for '{path}'");
                return false;
            }

            if (DEVOLUTIONS_CERT_THUMBPRINTS.Contains(thumbprint, StringComparer.OrdinalIgnoreCase))
            {
                Logger.Debug($"Installer signer thumbprint is trusted: {thumbprint}");
                return true;
            }

            Logger.Warn($"Installer signer thumbprint is NOT trusted: {thumbprint}");
            return false;
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not validate installer signer thumbprint.");
            Logger.Warn(ex);
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

        Logger.Debug($"Downloading installer from {url}");
        using HttpClient client = new(CreateHttpClientHandler(overrides));
        client.Timeout = TimeSpan.FromSeconds(600);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);

        HttpResponseMessage response = await client.GetAsync(url);
        response.EnsureSuccessStatusCode();

        using FileStream fs = new(destination, FileMode.OpenOrCreate);
        await response.Content.CopyToAsync(fs);

        Logger.Debug("Installer download complete.");
    }

    // ------------------------------------------------------------------ HTTP client
    private static HttpClientHandler CreateHttpClientHandler(UpdaterOverrides overrides)
    {
        var handler = new HttpClientHandler();
        if (overrides.DisableTlsValidation)
        {
            Logger.Warn("Registry override: TLS certificate validation is disabled for updater requests.");
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
            Logger.Warn($"Registry override: allowing potentially unsafe URL {url}");
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

        Logger.Warn($"Could not parse version '{raw}', using fallback '{fallback}'");
        return fallback;
    }

    private static string NormalizeThumbprint(string thumbprint) =>
        new(thumbprint.ToLowerInvariant().Where(char.IsAsciiHexDigit).ToArray());

    // ------------------------------------------------------------------ registry
    private static UpdaterOverrides LoadUpdaterOverrides()
    {
#pragma warning disable CA1416
        using RegistryKey? key = Registry.LocalMachine.OpenSubKey(REGISTRY_PATH);

#if DEBUG
        if (key is not null)
        {
            Logger.Info($"Updater registry overrides loaded from HKLM\\{REGISTRY_PATH}");
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
                Logger.Warn(
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
