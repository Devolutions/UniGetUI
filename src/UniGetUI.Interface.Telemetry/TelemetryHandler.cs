using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using UniGetUI.Core.Data;
using UniGetUI.Core.Language;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Interface.Telemetry;

public enum TEL_InstallReferral
{
    DIRECT_SEARCH,
    FROM_BUNDLE,
    FROM_WEB_SHARE,
    ALREADY_INSTALLED,
}

public enum TEL_OP_RESULT
{
    SUCCESS,
    FAILED,
    CANCELED,
}

public static class TelemetryHandler
{
    private const string OpenSearchUrl = "https://telemetry2.devolutions.net:9200";
    private static string _openSearchUsername = "";
    private static string _openSearchPassword = "";
    private static bool _credentialsWarningLogged;

    public static void Configure(string username, string password)
    {
        _openSearchUsername = username;
        _openSearchPassword = password;
    }

    private static bool CredentialsConfigured()
    {
        if (!string.IsNullOrEmpty(_openSearchUsername)
            && !_openSearchUsername.EndsWith("_UNSET")
            && !string.IsNullOrEmpty(_openSearchPassword)
            && !_openSearchPassword.EndsWith("_UNSET"))
            return true;

        if (!_credentialsWarningLogged)
        {
            Logger.Warn("[Telemetry] OpenSearch credentials are not configured — telemetry is disabled for this build.");
            _credentialsWarningLogged = true;
        }

        return false;
    }

    // Index names — to be created on the OpenSearch server
    private const string IndexActivity = "unigetui_activity_events";
    private const string IndexPackage = "unigetui_package_events";
    private const string IndexBundle = "unigetui_bundle_events";

#if DEBUG
    private const string IndexPrefix = "dev-";
#else
    private const string IndexPrefix = "";
#endif

    private static readonly HttpClient _httpClient;

    static TelemetryHandler()
    {
        _httpClient = new HttpClient(CoreTools.GenericHttpClientParameters)
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
    }

    private static readonly Settings.K[] SettingsToSend =
    [
        Settings.K.DisableAutoUpdateWingetUI,
        Settings.K.EnableUniGetUIBeta,
        Settings.K.DisableSystemTray,
        Settings.K.DisableNotifications,
        Settings.K.DisableAutoCheckforUpdates,
        Settings.K.AutomaticallyUpdatePackages,
        Settings.K.AskToDeleteNewDesktopShortcuts,
        Settings.K.EnablePackageBackup_LOCAL,
        Settings.K.DoCacheAdminRights,
        Settings.K.DoCacheAdminRightsForBatches,
        Settings.K.ForceLegacyBundledWinGet,
        Settings.K.UseSystemChocolatey,
    ];

    // -------------------------------------------------------------------------

    public static async Task InitializeAsync()
    {
        try
        {
            if (Settings.Get(Settings.K.DisableTelemetry))
                return;

            await CoreTools.WaitForInternetConnection();

            string[] enabledManagers = PEInterface.Managers
                .Where(m => m.IsEnabled())
                .Select(m => m.Name)
                .ToArray();

            string[] foundManagers = PEInterface.Managers
                .Where(m => m.IsEnabled() && m.Status.Found)
                .Select(m => m.Name)
                .ToArray();

            int settingsMagicValue = 0;
            int mask = 0x1;
            foreach (var setting in SettingsToSend)
            {
                bool enabled = Settings.Get(
                    key: setting,
                    invert: Settings.ResolveKey(setting).StartsWith("Disable"));

                if (enabled)
                    settingsMagicValue |= mask;
                mask <<= 1;

                if (mask == 0x1)
                    throw new OverflowException();
            }
            foreach (var sp in new[] { "SP1", "SP2" })
            {
                bool enabled = sp switch
                {
                    "SP1" => File.Exists("ForceUniGetUIPortable"),
                    "SP2" => CoreData.WasDaemon,
                    _ => throw new NotImplementedException(),
                };

                if (enabled)
                    settingsMagicValue |= mask;
                mask <<= 1;

                if (mask == 0x1)
                    throw new OverflowException();
            }

            var ev = new UniGetUIActivityEvent
            {
                InstallID = GetRandomizedId(),
                Locale = LanguageEngine.SelectedLocale,
                EnabledManagers = enabledManagers,
                FoundManagers = foundManagers,
                ActiveSettings = settingsMagicValue,
                Application = BuildApplicationInfo(),
                Platform = BuildPlatformInfo(),
            };

            await PostToOpenSearchAsync(IndexActivity, ev, TelemetrySerializerContext.Trimming.UniGetUIActivityEvent);
        }
        catch (Exception ex)
        {
            Logger.Error("[Telemetry] Hard crash in InitializeAsync");
            Logger.Error(ex);
        }
    }

    // -------------------------------------------------------------------------

    public static void InstallPackage(
        IPackage package,
        TEL_OP_RESULT status,
        TEL_InstallReferral source
    ) => _ = TrackPackageEventAsync(package, "install", status, source.ToString());

    public static void UpdatePackage(IPackage package, TEL_OP_RESULT status) =>
        _ = TrackPackageEventAsync(package, "update", status);

    public static void DownloadPackage(
        IPackage package,
        TEL_OP_RESULT status,
        TEL_InstallReferral source
    ) => _ = TrackPackageEventAsync(package, "download", status, source.ToString());

    public static void UninstallPackage(IPackage package, TEL_OP_RESULT status) =>
        _ = TrackPackageEventAsync(package, "uninstall", status);

    public static void PackageDetails(IPackage package, string eventSource) =>
        _ = TrackPackageEventAsync(package, "details", eventSource: eventSource);

    public static void SharedPackage(IPackage package, string eventSource) =>
        _ = TrackPackageEventAsync(package, "share", eventSource: eventSource);

    private static async Task TrackPackageEventAsync(
        IPackage package,
        string operation,
        TEL_OP_RESULT? result = null,
        string? eventSource = null)
    {
        try
        {
            if (result is null && eventSource is null)
                throw new ArgumentException("result and eventSource cannot both be null");
            if (Settings.Get(Settings.K.DisableTelemetry))
                return;

            await CoreTools.WaitForInternetConnection();

            var ev = new UniGetUIPackageEvent
            {
                InstallID = GetRandomizedId(),
                Locale = LanguageEngine.SelectedLocale,
                Application = BuildApplicationInfo(),
                Platform = BuildPlatformInfo(),
                Operation = operation,
                PackageId = package.Id,
                ManagerName = package.Manager.Name,
                SourceName = package.Source.Name,
                OperationResult = result?.ToString(),
                EventSource = eventSource,
            };

            await PostToOpenSearchAsync(IndexPackage, ev, TelemetrySerializerContext.Trimming.UniGetUIPackageEvent);
        }
        catch (Exception ex)
        {
            Logger.Error($"[Telemetry] Hard crash in TrackPackageEventAsync ({operation})");
            Logger.Error(ex);
        }
    }

    // -------------------------------------------------------------------------

    public static void ImportBundle(BundleFormatType type) =>
        _ = TrackBundleEventAsync("import", type.ToString());

    public static void ExportBundle(BundleFormatType type) =>
        _ = TrackBundleEventAsync("export", type.ToString());

    public static void ExportBatch() =>
        _ = TrackBundleEventAsync("export", "PS1_SCRIPT");

    private static async Task TrackBundleEventAsync(string operation, string bundleType)
    {
        try
        {
            if (Settings.Get(Settings.K.DisableTelemetry))
                return;

            await CoreTools.WaitForInternetConnection();

            var ev = new UniGetUIBundleEvent
            {
                InstallID = GetRandomizedId(),
                Locale = LanguageEngine.SelectedLocale,
                Application = BuildApplicationInfo(),
                Platform = BuildPlatformInfo(),
                Operation = operation,
                BundleType = bundleType,
            };

            await PostToOpenSearchAsync(IndexBundle, ev, TelemetrySerializerContext.Trimming.UniGetUIBundleEvent);
        }
        catch (Exception ex)
        {
            Logger.Error($"[Telemetry] Hard crash in TrackBundleEventAsync ({operation})");
            Logger.Error(ex);
        }
    }

    // ─── OpenSearch HTTP ──────────────────────────────────────────────────────

    private static async Task PostToOpenSearchAsync<T>(string indexName, T eventData, JsonTypeInfo<T> typeInfo)
    {
        if (!CredentialsConfigured())
            return;

        try
        {
            string fullIndex = IndexPrefix + indexName;
            string json = JsonSerializer.Serialize(eventData, typeInfo);

            string credentials = Convert.ToBase64String(
                Encoding.UTF8.GetBytes($"{_openSearchUsername}:{_openSearchPassword}"));

            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{OpenSearchUrl}/{fullIndex}/_doc")
            {
                Content = content,
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);

            HttpResponseMessage response = await _httpClient.SendAsync(request);

            if (response.IsSuccessStatusCode)
                Logger.Debug($"[Telemetry] Sent to {fullIndex}");
            else
                Logger.Warn($"[Telemetry] {fullIndex} returned {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            Logger.Error($"[Telemetry] Hard crash posting to {indexName}");
            Logger.Error(ex);
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static string GetRandomizedId()
    {
        string id = Settings.GetValue(Settings.K.TelemetryClientToken);
        if (id.Length != 64)
        {
            id = CoreTools.RandomString(64);
            Settings.SetValue(Settings.K.TelemetryClientToken, id);
        }
        return id;
    }

    private static TelemetryApplicationInfo BuildApplicationInfo() =>
        new()
        {
            Name = "UniGetUI",
            Version = CoreData.VersionName,
            DataSource = "NotApplicable",
            Pricing = "Free",
            Language = LanguageEngine.SelectedLocale,
            ArchitectureType = RuntimeInformation.ProcessArchitecture.ToString(),
        };

    private static TelemetryPlatformInfo BuildPlatformInfo() =>
        new()
        {
            Name = GetPlatformName(),
            Version = Environment.OSVersion.VersionString,
            Architecture = RuntimeInformation.OSArchitecture.ToString(),
        };

    private static string GetPlatformName()
    {
        if (OperatingSystem.IsWindows()) return "Windows";
        if (OperatingSystem.IsMacOS()) return "Mac";
        return "Linux";
    }
}

// Source-generated JSON context — required for AOT/trimmed builds (WinUI).
// Reflection-based serialization is disabled in that configuration.
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UniGetUIActivityEvent))]
[JsonSerializable(typeof(UniGetUIPackageEvent))]
[JsonSerializable(typeof(UniGetUIBundleEvent))]
internal partial class TelemetrySerializerContext : JsonSerializerContext
{
    internal static readonly TelemetrySerializerContext Trimming =
        new(new JsonSerializerOptions(SerializationHelpers.DefaultOptions)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        });
}
