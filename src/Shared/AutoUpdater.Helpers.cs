using System.Runtime.InteropServices;
using Microsoft.Win32;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;

namespace UniGetUI.Shared;

internal static class AutoUpdaterHelpers
{
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

    internal static Version ParseVersionOrFallback(
        string? rawVersion,
        Version fallbackVersion,
        Action<string>? logWarning = null
    )
    {
        if (TryExtractNormalizedCoreVersion(rawVersion, out Version? normalized, out _))
        {
            return normalized!;
        }

        string message = $"Could not parse version '{rawVersion}', using fallback '{fallbackVersion}'";
        if (logWarning is not null)
        {
            logWarning(message);
        }
        else
        {
            Logger.Warn(message);
        }

        return fallbackVersion;
    }

    internal static bool TryParseUpdaterVersion(string? rawVersion, out SemanticVersion version)
    {
        version = SemanticVersion.Invalid(rawVersion ?? string.Empty);
        if (!TryExtractNormalizedCoreVersion(rawVersion, out Version? normalizedCore, out string? preRelease))
        {
            return false;
        }

        string canonical = preRelease is not null
            ? $"{normalizedCore}-{preRelease}"
            : normalizedCore!.ToString();

        return SemanticVersion.TryParse(canonical, SemVerLabels.CaseInsensitive, out version);
    }

    internal static SemanticVersion ParseUpdaterVersionOrFallback(
        string? rawVersion,
        Version fallbackVersion,
        Action<string>? logWarning = null
    )
    {
        if (TryParseUpdaterVersion(rawVersion, out SemanticVersion parsed))
        {
            return parsed;
        }

        string message = $"Could not parse version '{rawVersion}', using fallback '{fallbackVersion}'";
        if (logWarning is not null)
        {
            logWarning(message);
        }
        else
        {
            Logger.Warn(message);
        }

        return SemanticVersion.TryParse(
            fallbackVersion.ToString(),
            SemVerLabels.CaseInsensitive,
            out SemanticVersion fallback
        )
            ? fallback
            : SemanticVersion.Invalid(rawVersion ?? string.Empty);
    }

    internal static int CompareUpdaterVersions(
        string? currentRaw,
        string? availableRaw,
        Version? currentFallback = null,
        Action<string>? logWarning = null
    )
    {
        SemanticVersion current = ParseUpdaterVersionOrFallback(
            currentRaw,
            currentFallback ?? new Version(0, 0, 0, 0),
            logWarning
        );
        SemanticVersion available = ParseUpdaterVersionOrFallback(
            availableRaw,
            new Version(0, 0, 0, 0),
            logWarning
        );
        return current.CompareTo(available);
    }

    internal static bool IsUpdateAvailable(
        string? currentRaw,
        string? availableRaw,
        Version? currentFallback = null,
        Action<string>? logWarning = null
    ) => CompareUpdaterVersions(currentRaw, availableRaw, currentFallback, logWarning) < 0;

    internal static bool VersionsMatch(string? a, string? b)
    {
        if (
            TryParseUpdaterVersion(a, out SemanticVersion va)
            && TryParseUpdaterVersion(b, out SemanticVersion vb)
        )
        {
            return va.CompareTo(vb) == 0;
        }

        string sa = (a ?? string.Empty).Trim().TrimStart('v', 'V');
        string sb = (b ?? string.Empty).Trim().TrimStart('v', 'V');
        return string.Equals(sa, sb, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsPreReleaseVersion(string? rawVersion) =>
        TryParseUpdaterVersion(rawVersion, out SemanticVersion parsed) && parsed.IsPreRelease;

    internal static ProductInfoChannel? SelectChannel(
        ProductInfoChannel? current,
        ProductInfoChannel? beta,
        bool useBeta
    )
    {
        if (!useBeta)
        {
            return current;
        }

        if (beta is null)
        {
            return current;
        }

        if (current is null)
        {
            return beta;
        }

        // Prefer Beta only when its version is strictly newer than Current; once stable
        // catches up (e.g. 2026.3.2.0 > 2026.3.2-beta3 or 2026.3.2.0 == 2026.3.2.0),
        // prefer Current so beta users upgrade seamlessly to the final release.
        return IsUpdateAvailable(current.Version, beta.Version) ? beta : current;
    }

    internal static ProductInfoChannel? SelectChannel(ProductInfoEntry product, bool useBeta) =>
        SelectChannel(product.Current, product.Beta, useBeta);

    private static bool TryExtractNormalizedCoreVersion(
        string? rawVersion,
        out Version? normalizedCore,
        out string? preRelease
    )
    {
        normalizedCore = null;
        preRelease = null;

        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return false;
        }

        string sanitized = rawVersion.Trim().TrimStart('v', 'V');
        int plusIndex = sanitized.IndexOf('+');
        if (plusIndex >= 0)
        {
            sanitized = sanitized[..plusIndex];
        }

        int dashIndex = sanitized.IndexOf('-');
        string corePart;
        if (dashIndex >= 0)
        {
            corePart = sanitized[..dashIndex];
            preRelease = sanitized[(dashIndex + 1)..];
            if (string.IsNullOrWhiteSpace(preRelease))
            {
                return false;
            }
        }
        else
        {
            corePart = sanitized;
        }

        if (!Version.TryParse(corePart, out Version? parsed))
        {
            return false;
        }

        normalizedCore = CoreTools.NormalizeVersionForComparison(parsed);
        return true;
    }

    internal static bool IsDefaultDevVersion(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion))
        {
            return false;
        }

        string sanitized = rawVersion.Trim().TrimStart('v', 'V');
        if (!Version.TryParse(sanitized, out Version? parsed))
        {
            return false;
        }

        return parsed.Major == 1
            && parsed.Minor == 0
            && parsed.Build <= 0
            && parsed.Revision <= 0;
    }

    internal static string NormalizeThumbprint(string thumbprint)
    {
        char[] normalized = thumbprint.ToLowerInvariant().Where(char.IsAsciiHexDigit).ToArray();

        return new string(normalized);
    }

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

    internal sealed class ProductInfoEntry
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
}
