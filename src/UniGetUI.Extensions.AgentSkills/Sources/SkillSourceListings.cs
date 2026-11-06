using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Devolutions.AgentSkills;
using UniGetUI.Core.Logging;

namespace UniGetUI.Extensions.AgentSkills;

/// <summary>Browsing reuses metadata; only an explicit refresh fetches an already known source.</summary>
internal sealed class SkillSourceListings(ISkillsBackend backend, string? cacheDirectory = null)
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<AvailableSkill>> _listings = new(StringComparer.Ordinal);
    private const int MaxCacheBytes = 8 * 1024 * 1024;

    public IReadOnlyList<AvailableSkill> Get(SkillSourceLocator source, IProgress<string>? progress, CancellationToken token,
        bool refresh = false)
    {
        token.ThrowIfCancellationRequested();
        if (!refresh && source.Kind is not SkillSourceKind.Notion)
        {
            if (_listings.TryGetValue(source.InstallSource, out var cached)) return cached;
            if (Read(source) is { } saved)
            {
                _listings[source.InstallSource] = saved;
                return saved;
            }
        }
        var skills = backend.GetAvailableSkills(source.InstallSource, progress, token);
        token.ThrowIfCancellationRequested();
        Store(source, skills);
        return skills;
    }

    public void Store(SkillSourceLocator source, IReadOnlyList<AvailableSkill> skills)
    {
        _listings[source.InstallSource] = skills;
        if (CachePath(source) is not { } path) return;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var typeInfo = SkillListingJsonContext.Default.SkillListingSnapshot;
            var data = JsonSerializer.SerializeToUtf8Bytes(new SkillListingSnapshot
            {
                Version = 1,
                Source = source.InstallSource,
                Skills = skills.ToArray(),
            }, typeInfo);
            if (data.Length > MaxCacheBytes)
            {
                Logger.Warn($"Skill listing is too large to cache: {source.Name}");
                return;
            }
            Directory.CreateDirectory(cacheDirectory!);
            File.WriteAllBytes(temporary, data);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn("Could not save the skill source listing cache.");
            Logger.Warn(ex);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.Warn(ex); }
        }
    }

    public void Forget(SkillSourceLocator source)
    {
        _listings.TryRemove(source.InstallSource, out _);
        if (CachePath(source) is not { } path) return;
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Logger.Warn(ex); }
    }

    private string? CachePath(SkillSourceLocator source) => cacheDirectory is null || source.Kind == SkillSourceKind.Notion
        ? null : Path.Combine(cacheDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.InstallSource))) + ".json");

    private IReadOnlyList<AvailableSkill>? Read(SkillSourceLocator source)
    {
        if (CachePath(source) is not { } path || !File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxCacheBytes) throw new InvalidDataException("Skill listing cache exceeds the size limit.");
            var typeInfo = SkillListingJsonContext.Default.SkillListingSnapshot;
            var snapshot = JsonSerializer.Deserialize(stream, typeInfo);
            if (snapshot is not { Version: 1, Skills: not null } || snapshot.Source != source.InstallSource
                || snapshot.Skills.Any(skill => skill is null || string.IsNullOrWhiteSpace(skill.Name) || skill.Description is null))
                throw new InvalidDataException("Unsupported skill listing cache.");
            return snapshot.Skills;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            Logger.Warn("Could not read the skill source listing cache; fetching the source again.");
            Logger.Warn(ex);
            return null;
        }
    }
}

internal sealed class SkillListingSnapshot
{
    public int Version { get; set; }
    public string? Source { get; set; }
    public AvailableSkill[]? Skills { get; set; }
}

[JsonSerializable(typeof(SkillListingSnapshot))]
internal sealed partial class SkillListingJsonContext : JsonSerializerContext;
