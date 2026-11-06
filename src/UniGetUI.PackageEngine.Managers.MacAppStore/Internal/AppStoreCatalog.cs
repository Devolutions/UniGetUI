using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;

namespace UniGetUI.PackageEngine.Managers.MacAppStoreManager.Internal;

/// <summary>
/// App Store records (description, icon, screenshots, price) for the apps mas lists.
/// Apple's lookup API allows only about twenty requests a minute, so asking once per package would
/// throttle a page of icons. Records are cached, and a lookup also fetches every other app the last
/// listings showed that is not cached yet, so a page of installed apps costs one request.
/// </summary>
internal sealed class AppStoreCatalog
{
    internal const int MaxIdsPerLookup = 100;
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

    /// <summary>Downloads a lookup URL. Replaceable for tests.</summary>
    internal Func<Uri, string> Download { get; set; } = DownloadString;

    /// <summary>Keeps a record mas already printed (<c>mas search --json</c>), saving a lookup.</summary>
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
        new($"https://itunes.apple.com/lookup?id={string.Join(',', ids)}&country={Uri.EscapeDataString(storefront.ToLowerInvariant())}");

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
