using System.Globalization;
using System.Text.Json.Nodes;

namespace UniGetUI.PackageEngine.Managers.MacAppStoreManager.Internal;

/// <summary>
/// Reads an App Store record. mas 7 renames Apple's iTunes Search API fields (trackId becomes adamID,
/// artworkUrl512 becomes icon512URL, and so on), so every accessor takes both spellings.
/// </summary>
internal static class AppStoreRecord
{
    public static string? Id(JsonObject app)
    {
        string? id = Text(app, "adamID", "trackId");
        return id is not null && id.All(char.IsAsciiDigit) ? id : null;
    }

    public static string? Text(JsonObject app, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (app[key] is not JsonValue value)
                continue;
            string? text = value.GetValueKind() switch
            {
                System.Text.Json.JsonValueKind.String => value.GetValue<string>(),
                System.Text.Json.JsonValueKind.Number => value.ToJsonString(),
                _ => null,
            };
            if (!string.IsNullOrWhiteSpace(text))
                return text;
        }
        return null;
    }

    public static Uri? Url(JsonObject app, params string[] keys)
    {
        foreach (string key in keys)
            if (AsWebUrl(Text(app, key)) is { } url)
                return url;
        return null;
    }

    public static IReadOnlyList<string> Strings(JsonObject app, params string[] keys)
    {
        foreach (string key in keys)
        {
            if (app[key] is not JsonArray array)
                continue;
            List<string> values = [];
            foreach (JsonNode? item in array)
                if (item is JsonValue value && value.GetValueKind() == System.Text.Json.JsonValueKind.String
                    && value.GetValue<string>() is { Length: > 0 } text)
                    values.Add(text);
            if (values.Count > 0)
                return values;
        }
        return [];
    }

    public static decimal? Price(JsonObject app) =>
        decimal.TryParse(Text(app, "price"), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal price)
            ? price
            : null;

    public static long SizeInBytes(JsonObject app) =>
        long.TryParse(Text(app, "fileSizeBytes"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long size)
            ? size
            : 0;

    public static Uri? Icon(JsonObject app) =>
        Url(app, "icon512URL", "artworkUrl512", "icon100URL", "artworkUrl100", "icon60URL", "artworkUrl60");

    public static IReadOnlyList<Uri> Screenshots(JsonObject app)
    {
        List<Uri> screenshots = [];
        foreach (string url in Strings(app, "screenshotURLs", "macScreenshotUrls", "screenshotUrls", "ipadScreenshotUrls"))
            if (AsWebUrl(url) is { } uri)
                screenshots.Add(uri);
        return screenshots;
    }

    /// <summary>Whether this is a catalog record, rather than the bare installed-app data of Spotlight.</summary>
    public static bool IsCatalogRecord(JsonObject app) =>
        Id(app) is not null && (Icon(app) is not null || Text(app, "description") is not null);

    public static string? Kind(JsonObject app) => Text(app, "kind");

    /// <summary>
    /// Native Mac apps report <c>mac-software</c>; compatible iPhone/iPad apps offered on the Mac App
    /// Store for Apple Silicon report <c>software</c> and include <c>MacDesktop-MacDesktop</c> in
    /// <c>supportedDevices</c> (ported from <c>mas-cli/mas</c> <c>CatalogApp.swift</c>).
    /// </summary>
    public static bool SupportsMac(JsonObject app, bool isAppleSilicon)
    {
        string? kind = Kind(app);
        if (kind == "mac-software")
            return true;
        return isAppleSilicon
            && kind == "software"
            && Strings(app, "supportedDevices").Contains("MacDesktop-MacDesktop", StringComparer.Ordinal);
    }

    /// <summary>
    /// Minimum macOS version required by a <c>mac-software</c> release, or null when unrestricted
    /// (for <c>software</c> records, Apple's <c>minimumOsVersion</c> names an iOS version instead).
    /// </summary>
    public static Version? MinimumMacOSVersion(JsonObject app)
    {
        if (Kind(app) != "mac-software" || Text(app, "minimumOSVersion", "minimumOsVersion") is not { } raw)
            return null;

        string[] parts = raw.Trim().Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int major))
            return null;
        int minor = parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) ? m : 0;
        int patch = parts.Length >= 3 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int p) ? p : 0;
        return new Version(major, minor, patch);
    }

    public static bool IsCompatibleWithMacOS(JsonObject app, Version currentMacOS)
    {
        if (MinimumMacOSVersion(app) is not { } required)
            return true;
        Version normalizedCurrent = new(
            Math.Max(0, currentMacOS.Major),
            Math.Max(0, currentMacOS.Minor),
            Math.Max(0, currentMacOS.Build));
        return normalizedCurrent >= required;
    }

    private static Uri? AsWebUrl(string? text) =>
        Uri.TryCreate(text, UriKind.Absolute, out Uri? url) && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)
            ? url
            : null;
}
