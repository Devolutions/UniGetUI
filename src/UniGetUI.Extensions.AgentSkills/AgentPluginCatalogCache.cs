using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using UniGetUI.Core.Logging;

namespace UniGetUI.Extensions.AgentSkills;

public sealed record AgentPluginCatalogCacheSnapshot(DateTimeOffset CachedAt, IReadOnlyList<AgentPluginItem> Plugins);

/// <summary>Persists marketplace discovery metadata without persisting installation state.</summary>
public sealed class AgentPluginCatalogCache(string? cacheDirectory, TimeProvider? timeProvider = null)
{
    private const int Version = 1;
    private const int MaxCacheBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan Lifetime = TimeSpan.FromDays(7);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<AgentPluginCatalogCacheSnapshot?> ReadAsync(string provider, AgentPluginMarketplace marketplace,
        CancellationToken token = default)
    {
        string? path = GetPath(provider, marketplace);
        if (path is null || !File.Exists(path)) return null;
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                16 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaxCacheBytes) throw new InvalidDataException("Plugin catalog cache exceeds the size limit.");
            var typeInfo = AgentPluginCatalogCacheJsonContext.Default.AgentPluginCatalogCacheEntry;
            var snapshot = await JsonSerializer.DeserializeAsync(stream, typeInfo, token);
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (snapshot is not { Version: Version, Plugins: not null }
                || snapshot.Provider != provider || snapshot.Marketplace != marketplace.Name
                || snapshot.SourceHash != GetSourceHash(marketplace.Source)
                || snapshot.CachedAt > now.AddMinutes(5) || now - snapshot.CachedAt >= Lifetime
                || snapshot.Plugins.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id)
                    || item.Description is null || item.Version is null
                    || !string.Equals(AgentPluginInput.PluginId(item.Id).Split('@')[1], marketplace.Name, StringComparison.Ordinal)))
                throw new InvalidDataException("Unsupported or expired plugin catalog cache.");

            var plugins = snapshot.Plugins
                .Select(item => new AgentPluginItem(item.Id, item.Description, item.Version, false, false)
                {
                    CanInstall = item.CanInstall,
                    CanManage = item.CanManage,
                    Notice = item.Notice,
                })
                .ToArray();
            return new(snapshot.CachedAt, plugins);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or ArgumentException)
        {
            Logger.Warn("Could not read the plugin marketplace cache; it will be refreshed when requested.");
            Logger.Warn(ex);
            return null;
        }
    }

    public async Task StoreAsync(string provider, AgentPluginMarketplace marketplace, IReadOnlyList<AgentPluginItem> plugins,
        CancellationToken token = default)
    {
        string? path = GetPath(provider, marketplace);
        if (path is null) return;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var entries = plugins.Select(item =>
            {
                if (!string.Equals(AgentPluginInput.PluginId(item.Id).Split('@')[1], marketplace.Name, StringComparison.Ordinal))
                    throw new ArgumentException("A plugin does not belong to the cached marketplace.", nameof(plugins));
                return new AgentPluginCatalogCacheItem(item.Id, item.Description, item.Version,
                    item.CanInstall, item.CanManage, item.Notice);
            }).ToArray();
            var typeInfo = AgentPluginCatalogCacheJsonContext.Default.AgentPluginCatalogCacheEntry;
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(new AgentPluginCatalogCacheEntry
            {
                Version = Version,
                Provider = provider,
                Marketplace = marketplace.Name,
                SourceHash = GetSourceHash(marketplace.Source),
                CachedAt = _timeProvider.GetUtcNow(),
                Plugins = entries,
            }, typeInfo);
            if (data.Length > MaxCacheBytes)
            {
                Logger.Warn($"Plugin marketplace catalog is too large to cache: {marketplace.Name}");
                return;
            }
            token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(cacheDirectory!);
            await File.WriteAllBytesAsync(temporary, data, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn("Could not save the plugin marketplace cache.");
            Logger.Warn(ex);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.Warn(ex); }
        }
    }

    public void Forget(string provider, AgentPluginMarketplace marketplace)
    {
        if (GetPath(provider, marketplace) is not { } path) return;
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.Warn(ex); }
    }

    private string? GetPath(string provider, AgentPluginMarketplace marketplace)
    {
        if (cacheDirectory is null) return null;
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(marketplace.Name)
            || string.IsNullOrWhiteSpace(marketplace.Source))
            throw new ArgumentException("Plugin cache identity must be complete.");
        string identity = string.Join('\0', provider, marketplace.Name, marketplace.Source);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return Path.Combine(cacheDirectory, hash + ".json");
    }

    private static string GetSourceHash(string source) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source)));
}

internal sealed class AgentPluginCatalogCacheEntry
{
    public int Version { get; set; }
    public string? Provider { get; set; }
    public string? Marketplace { get; set; }
    public string? SourceHash { get; set; }
    public DateTimeOffset CachedAt { get; set; }
    public AgentPluginCatalogCacheItem[]? Plugins { get; set; }
}

internal sealed record AgentPluginCatalogCacheItem(string Id, string Description, string Version,
    bool CanInstall, bool CanManage, string? Notice);

[JsonSerializable(typeof(AgentPluginCatalogCacheEntry))]
internal sealed partial class AgentPluginCatalogCacheJsonContext : JsonSerializerContext;
