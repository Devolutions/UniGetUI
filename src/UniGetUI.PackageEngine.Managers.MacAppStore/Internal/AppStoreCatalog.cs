using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;

namespace UniGetUI.PackageEngine.Managers.MacAppStoreManager.Internal;

/// <summary>
/// App Store catalog search and lookup against Apple's public iTunes Search API
/// (<c>itunes.apple.com/search</c> and <c>itunes.apple.com/lookup</c>), ported from MIT-licensed
/// <c>mas-cli/mas</c>. Apple's lookup API allows only about twenty requests a minute, so records are
/// cached and lookups batch up to 100 queued app IDs per request.
/// </summary>
internal sealed class AppStoreCatalog
{
    internal const int MaxIdsPerLookup = 100;
    internal const int MaxSearchResults = 50;
    private static readonly TimeSpan MissedRetryDelay = TimeSpan.FromHours(1);
    private static readonly TimeSpan FailedRetryDelay = TimeSpan.FromMinutes(2);

    private readonly ConcurrentDictionary<string, JsonObject> _records = new();
    private readonly ConcurrentDictionary<string, DateTime> _missed = new();
    private readonly ConcurrentDictionary<string, byte> _wanted = new();
    private readonly SemaphoreSlim _lookupLock = new(1, 1);
    private readonly Func<string> _storefront;
    private DateTime _failedUntil = DateTime.MinValue;

    public AppStoreCatalog(Func<string> storefront)
    {
        _storefront = storefront;
    }

    /// <summary>Downloads a lookup or search URL. Replaceable for tests.</summary>
    internal Func<Uri, string> Download { get; set; } = DownloadString;

    public void Clear()
    {
        _records.Clear();
        _missed.Clear();
        _failedUntil = DateTime.MinValue;
    }

    /// <summary>Keeps a catalog record already fetched from search or lookup.</summary>
    public void Remember(JsonObject? record)
    {
        if (record is not null && AppStoreRecord.IsCatalogRecord(record))
            _records[AppStoreRecord.Id(record)!] = record;
    }

    /// <summary>Notes apps a listing showed, so the next lookup fetches them alongside the one asked for.</summary>
    public void Want(IEnumerable<string> ids)
    {
        foreach (string id in ids)
            _wanted.TryAdd(id, 0);
    }

    /// <summary>The cached record, without ever asking Apple.</summary>
    public JsonObject? Peek(string id) => _records.GetValueOrDefault(id);

    public JsonObject? Get(string id)
    {
        if (_records.TryGetValue(id, out JsonObject? record))
            return record;
        if (RecentlyMissed(id) || DateTime.UtcNow < _failedUntil)
            return null;

        _lookupLock.Wait();
        try
        {
            if (_records.TryGetValue(id, out record))
                return record;
            if (RecentlyMissed(id) || DateTime.UtcNow < _failedUntil)
                return null;

            string[] batch =
            [
                id,
                .. _wanted.Keys
                    .Where(other => other != id && !_records.ContainsKey(other) && !RecentlyMissed(other))
                    .Take(MaxIdsPerLookup - 1),
            ];
            Lookup(batch);
            return _records.GetValueOrDefault(id);
        }
        finally
        {
            _lookupLock.Release();
        }
    }

    /// <summary>Ensures all requested IDs have been looked up in batches of up to 100.</summary>
    public IReadOnlyDictionary<string, JsonObject> GetMany(IEnumerable<string> ids)
    {
        string[] unique = ids.Where(id => id.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
        if (unique.Length == 0 || DateTime.UtcNow < _failedUntil)
            return SnapshotFor(unique);

        if (unique.All(id => _records.ContainsKey(id) || RecentlyMissed(id)))
            return SnapshotFor(unique);

        _lookupLock.Wait();
        try
        {
            if (DateTime.UtcNow >= _failedUntil)
            {
                string[] pending = unique
                    .Where(id => !_records.ContainsKey(id) && !RecentlyMissed(id))
                    .ToArray();
                for (int offset = 0; offset < pending.Length && DateTime.UtcNow >= _failedUntil; offset += MaxIdsPerLookup)
                {
                    string[] chunk = pending.Skip(offset).Take(MaxIdsPerLookup).ToArray();
                    Lookup(chunk);
                }
            }
        }
        finally
        {
            _lookupLock.Release();
        }

        return SnapshotFor(unique);
    }

    public IReadOnlyList<JsonObject> Search(string terms, bool includeAppleSiliconIosApps)
    {
        string storefront = _storefront();
        string desktopJson = Download(SearchUrl(terms, storefront, desktopOnly: true));
        IReadOnlyList<JsonObject> desktop = ParseLookup(desktopJson);
        List<JsonObject> merged = [];
        HashSet<string> seen = new(StringComparer.Ordinal);

        foreach (JsonObject app in desktop)
        {
            if (AppStoreRecord.Id(app) is not { } id
                || !AppStoreRecord.SupportsMac(app, includeAppleSiliconIosApps)
                || !seen.Add(id))
                continue;
            Remember(app);
            merged.Add(app);
        }

        if (includeAppleSiliconIosApps)
        {
            try
            {
                string compatibleJson = Download(SearchUrl(terms, storefront, desktopOnly: false));
                foreach (JsonObject app in ParseLookup(compatibleJson))
                {
                    if (AppStoreRecord.Id(app) is not { } id
                        || !AppStoreRecord.SupportsMac(app, isAppleSilicon: true)
                        || !seen.Add(id))
                        continue;
                    Remember(app);
                    merged.Add(app);
                }
            }
            catch (Exception ex)
            {
                Logger.Warn($"Could not query compatible Apple Silicon App Store results for '{terms}': {ex.Message}");
            }
        }

        return merged;
    }

    private Dictionary<string, JsonObject> SnapshotFor(IEnumerable<string> ids)
    {
        Dictionary<string, JsonObject> found = new(StringComparer.Ordinal);
        foreach (string id in ids)
            if (_records.TryGetValue(id, out JsonObject? record))
                found[id] = record;
        return found;
    }

    private void Lookup(IReadOnlyList<string> ids)
    {
        Uri url = LookupUrl(ids, _storefront());
        string json;
        try
        {
            json = Download(url);
        }
        catch (Exception ex)
        {
            _failedUntil = DateTime.UtcNow + FailedRetryDelay;
            Logger.Warn($"Could not look up {ids.Count} app(s) on the App Store: {ex.Message}");
            return;
        }

        foreach (JsonObject record in ParseLookup(json))
            Remember(record);
        foreach (string id in ids)
        {
            _wanted.TryRemove(id, out _);
            if (!_records.ContainsKey(id))
                _missed[id] = DateTime.UtcNow;
        }
    }

    private bool RecentlyMissed(string id) =>
        _missed.TryGetValue(id, out DateTime when) && DateTime.UtcNow - when < MissedRetryDelay;

    internal static Uri LookupUrl(IEnumerable<string> ids, string storefront) =>
        new($"https://itunes.apple.com/lookup?id={string.Join(',', ids)}&media=software&country={Uri.EscapeDataString(storefront.ToLowerInvariant())}");

    internal static Uri SearchUrl(string terms, string storefront, bool desktopOnly = true)
    {
        string entity = desktopOnly ? "&entity=desktopSoftware" : "";
        return new($"https://itunes.apple.com/search?term={Uri.EscapeDataString(terms)}&media=software{entity}&limit={MaxSearchResults}&country={Uri.EscapeDataString(storefront.ToLowerInvariant())}");
    }

    internal static IReadOnlyList<JsonObject> ParseLookup(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is JsonObject { } root && root["results"] is JsonArray results)
                return results.OfType<JsonObject>().ToArray();
        }
        catch (JsonException) { }
        return [];
    }

    private static string DownloadString(Uri url)
    {
        using HttpClient client = new(CoreTools.GenericHttpClientParameters) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
        // Called from synchronous helpers; never block a caller's synchronization context on it.
        return Task.Run(() => client.GetStringAsync(url)).GetAwaiter().GetResult();
    }
}
