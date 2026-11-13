using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace UniGetUI.PackageEngine.Managers.MacAppStoreManager.Internal;

/// <summary>One app as mas reports it: installed, outdated or found by a search.</summary>
/// <param name="Id">The App Store ADAM ID, which every mas command accepts.</param>
/// <param name="NewVersion">The version an update installs; only set by <c>mas outdated</c>.</param>
/// <param name="Path">Where the app is installed; only reported by <c>mas list --json</c>.</param>
/// <param name="Catalog">The App Store record, when mas printed one (<c>mas search --json</c>).</param>
internal sealed record MasApp(
    string Id,
    string Name,
    string Version,
    string? NewVersion = null,
    string? Path = null,
    JsonObject? Catalog = null,
    string? BundleId = null
);

/// <summary>
/// Reads what mas prints. mas 7 writes one JSON object per line when asked to (<c>--json</c>);
/// every version writes a padded table otherwise, which is read for older releases.
/// </summary>
internal static partial class MasOutput
{
    // "937984704  Amphetamine  (5.3.2)", optionally followed by a price on search. The name may
    // itself hold parentheses, so the version is the last parenthesised group of the line.
    [GeneratedRegex(@"^\s*(\d+)\s+(.+?)\s+\(([^()]*)\)(?:\s+[^()]*)?\s*$")]
    private static partial Regex AppLine();

    // "6450432292  Universal Print  (1.0    -> 1.1.0)"
    [GeneratedRegex(@"^\s*(\d+)\s+(.+?)\s+\(([^()]*?)\s*->\s*([^()]*?)\)\s*$")]
    private static partial Regex OutdatedLine();

    // "store ▁▁ CA" or "region ▁ CA", as "mas config" prints without --json.
    [GeneratedRegex(@"^\s*(store|region)\b[^A-Za-z]*([A-Za-z]{2})\s*$")]
    private static partial Regex ConfigLine();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Number();

    /// <summary>The first number of <c>mas version</c>, or 0 when it cannot be read.</summary>
    public static int ParseMajorVersion(string version)
    {
        Match match = Number().Match(version);
        return match.Success && int.TryParse(match.Value, CultureInfo.InvariantCulture, out int major) ? major : 0;
    }

    /// <summary>Reads <c>mas list</c>, <c>mas search</c> or <c>mas outdated</c>, as JSON lines or as a table.</summary>
    public static IReadOnlyList<MasApp> ParseApps(IEnumerable<string> lines, bool outdated)
    {
        List<MasApp> apps = [];
        HashSet<string> seen = [];
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            MasApp? app = line.StartsWith('{') ? ParseJsonLine(line, outdated) : ParseTableLine(line, outdated);
            if (app is not null && seen.Add(app.Id))
                apps.Add(app);
        }
        return apps;
    }

    private static MasApp? ParseJsonLine(string line, bool outdated)
    {
        JsonObject? json;
        try
        {
            json = JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
        if (json is null)
            return null;

        string? id = AppStoreRecord.Id(json);
        string? name = AppStoreRecord.Text(json, "name", "trackName");
        if (id is null || string.IsNullOrWhiteSpace(name))
            return null;

        string version = AppStoreRecord.Text(json, "version") ?? "";
        string? newVersion = AppStoreRecord.Text(json, "newVersion");
        if (outdated && string.IsNullOrEmpty(newVersion))
            return null;

        return new MasApp(id, name.Trim(), version, outdated ? newVersion : null,
            AppStoreRecord.Text(json, "path"), json, AppStoreRecord.Text(json, "bundleID", "bundleId"));
    }

    private static MasApp? ParseTableLine(string line, bool outdated)
    {
        if (outdated)
        {
            Match update = OutdatedLine().Match(line);
            return update.Success
                ? new MasApp(update.Groups[1].Value, update.Groups[2].Value.Trim(),
                    update.Groups[3].Value.Trim(), update.Groups[4].Value.Trim())
                : null;
        }

        Match app = AppLine().Match(line);
        return app.Success
            ? new MasApp(app.Groups[1].Value, app.Groups[2].Value.Trim(), app.Groups[3].Value.Trim())
            : null;
    }

    /// <summary>
    /// The App Store storefront from <c>mas config</c> (JSON or text), else the system region.
    /// The storefront wins: it is the country whose catalog the signed-in account buys from.
    /// </summary>
    public static string? ParseStorefront(IEnumerable<string> lines)
    {
        string[] all = lines.ToArray();
        string text = string.Join('\n', all).Trim();
        if (text.Length == 0)
            return null;

        if (text.StartsWith('{'))
        {
            try
            {
                if (JsonNode.Parse(text) is JsonObject json)
                    return AppStoreRegion.FromStoreFrontHeader(AppStoreRecord.Text(json, "store"))
                        ?? AppStoreRegion.FromStoreFrontHeader(AppStoreRecord.Text(json, "region"));
            }
            catch (JsonException) { }
        }

        if (AppStoreRegion.FromStoreFrontHeader(text) is { } direct)
            return direct;

        string? store = null, region = null;
        foreach (string line in all)
        {
            Match match = ConfigLine().Match(line);
            if (!match.Success)
                continue;
            if (match.Groups[1].Value == "store")
                store ??= AppStoreRegion.FromStoreFrontHeader(match.Groups[2].Value);
            else
                region ??= AppStoreRegion.FromStoreFrontHeader(match.Groups[2].Value);
        }
        return store ?? region;
    }
}
