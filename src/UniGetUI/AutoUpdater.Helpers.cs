using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;

namespace UniGetUI;

public partial class AutoUpdater
{
    private const string REGISTRY_PATH = @"Software\Devolutions\UniGetUI";
    private const string DEFAULT_PRODUCTINFO_URL = "https://devolutions.net/productinfo.json";
    private const string DEFAULT_PRODUCTINFO_KEY = "Devolutions.UniGetUI";

    private const string REG_PRODUCTINFO_URL = "UpdaterProductInfoUrl";
    private const string REG_PRODUCTINFO_KEY = "UpdaterProductKey";
    private const string REG_ALLOW_UNSAFE_URLS = "UpdaterAllowUnsafeUrls";
    private const string REG_SKIP_HASH_VALIDATION = "UpdaterSkipHashValidation";
    private const string REG_SKIP_SIGNER_THUMBPRINT_CHECK = "UpdaterSkipSignerThumbprintCheck";
    private const string REG_DISABLE_TLS_VALIDATION = "UpdaterDisableTlsValidation";

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

    private static HttpClientHandler CreateHttpClientHandler(UpdaterOverrides updaterOverrides)
    {
        HttpClientHandler handler = CoreTools.GenericHttpClientParameters;
        if (updaterOverrides.DisableTlsValidation)
        {
            Logger.Warn(
                "Registry override enabled: TLS certificate validation is disabled for updater requests."
            );
            handler.ServerCertificateCustomValidationCallback = static (_, _, _, _) => true;
        }

        return handler;
    }

    internal static bool IsSourceUrlAllowed(string url, bool allowUnsafeUrls)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
        {
            return false;
        }

        if (allowUnsafeUrls)
        {
            Logger.Warn($"Registry override enabled: allowing potentially unsafe updater URL {url}");
            return true;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return uri.Host.Equals("devolutions.net", StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith(".devolutions.net", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals("objects.githubusercontent.com", StringComparison.OrdinalIgnoreCase)
            || uri.Host.Equals(
                "release-assets.githubusercontent.com",
                StringComparison.OrdinalIgnoreCase
            );
    }

    internal static ProductInfoFile SelectInstallerFile(List<ProductInfoFile> files)
    {
        string targetArch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            _ => "x64",
        };

        ProductInfoFile? match = files.FirstOrDefault(file =>
            file.Type.Equals("exe", StringComparison.OrdinalIgnoreCase)
            && file.Arch.Equals(targetArch, StringComparison.OrdinalIgnoreCase)
        );

        match ??= files.FirstOrDefault(file =>
            file.Type.Equals("exe", StringComparison.OrdinalIgnoreCase)
            && file.Arch.Equals("Any", StringComparison.OrdinalIgnoreCase)
        );

        match ??= files.FirstOrDefault(file =>
            file.Type.Equals("msi", StringComparison.OrdinalIgnoreCase)
            && file.Arch.Equals(targetArch, StringComparison.OrdinalIgnoreCase)
        );

        match ??= files.FirstOrDefault(file =>
            file.Type.Equals("msi", StringComparison.OrdinalIgnoreCase)
            && file.Arch.Equals("Any", StringComparison.OrdinalIgnoreCase)
        );

        if (match is null)
        {
            throw new KeyNotFoundException(
                $"No compatible installer file found in productinfo for architecture '{targetArch}'"
            );
        }

        return match;
    }

    internal static Version ParseVersionOrFallback(string rawVersion, Version fallbackVersion)
    {
        if (Version.TryParse(rawVersion, out Version? parsed))
        {
            return CoreTools.NormalizeVersionForComparison(parsed);
        }

        string sanitized = rawVersion.Trim().TrimStart('v', 'V');
        if (Version.TryParse(sanitized, out parsed))
        {
            return CoreTools.NormalizeVersionForComparison(parsed);
        }

        Logger.Warn($"Could not parse version '{rawVersion}', using fallback '{fallbackVersion}'");
        return fallbackVersion;
    }

    internal static string NormalizeThumbprint(string thumbprint)
    {
        char[] normalized = thumbprint.ToLowerInvariant().Where(char.IsAsciiHexDigit).ToArray();

        return new string(normalized);
    }

    private static UpdaterOverrides LoadUpdaterOverrides()
    {
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
        string productInfoUrl =
            GetRegistryString(key, REG_PRODUCTINFO_URL) ?? DEFAULT_PRODUCTINFO_URL;

        return new UpdaterOverrides(
            productInfoUrl,
            DEFAULT_PRODUCTINFO_KEY,
            false,
            false,
            false,
            false
        );
#endif
    }

#if !DEBUG
    private static void LogIgnoredReleaseOverrides(RegistryKey? key)
    {
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
    }
#endif

    internal static string? GetRegistryString(RegistryKey? key, string valueName)
    {
#pragma warning disable CA1416
        object? value = key?.GetValue(valueName);
#pragma warning restore CA1416
        if (value is null)
        {
            return null;
        }

        string? parsedValue = value.ToString();
        if (string.IsNullOrWhiteSpace(parsedValue))
        {
            return null;
        }

        return parsedValue.Trim();
    }

#if DEBUG
    internal static bool GetRegistryBool(RegistryKey? key, string valueName)
    {
#pragma warning disable CA1416
        object? value = key?.GetValue(valueName);
#pragma warning restore CA1416
        if (value is null)
        {
            return false;
        }

        if (value is int intValue)
        {
            return intValue != 0;
        }

        if (value is long longValue)
        {
            return longValue != 0;
        }

        string normalized = value.ToString()?.Trim() ?? "";
        return normalized.Equals("1", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("true", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("on", StringComparison.OrdinalIgnoreCase);
    }
#endif

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

    internal sealed class ProductInfoChannel
    {
        public string Version { get; set; } = string.Empty;
        public List<ProductInfoFile> Files { get; set; } = [];
    }

    internal sealed class ProductInfoFile
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
