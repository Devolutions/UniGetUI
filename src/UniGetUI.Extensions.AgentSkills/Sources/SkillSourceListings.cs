using System.Collections.Concurrent;
using Devolutions.AgentSkills;

namespace UniGetUI.Extensions.AgentSkills;

/// <summary>Reuses source listings across queries without starting uncancellable background clones.</summary>
internal sealed class SkillSourceListings(ISkillsBackend backend)
{
    private sealed record Listing(DateTime FetchedAt, IReadOnlyList<AvailableSkill> Skills);
    private readonly ConcurrentDictionary<string, Listing> _listings = new(StringComparer.Ordinal);

    public IReadOnlyList<AvailableSkill> Get(SkillSourceLocator source, IProgress<string>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (source.Kind is not SkillSourceKind.Notion && _listings.TryGetValue(source.Name, out var cached)
            && DateTime.UtcNow - cached.FetchedAt < TimeSpan.FromHours(1)) return cached.Skills;
        var skills = backend.GetAvailableSkills(source.InstallSource, progress, token);
        token.ThrowIfCancellationRequested();
        Store(source, skills);
        return skills;
    }

    public void Store(SkillSourceLocator source, IReadOnlyList<AvailableSkill> skills) =>
        _listings[source.Name] = new(DateTime.UtcNow, skills);

    public void Forget(SkillSourceLocator source) => _listings.TryRemove(source.Name, out _);
}
