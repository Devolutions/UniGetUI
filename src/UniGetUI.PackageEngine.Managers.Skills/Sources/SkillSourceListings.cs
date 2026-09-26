using System.Collections.Concurrent;
using Devolutions.AgentSkills;
using UniGetUI.Core.Logging;

namespace UniGetUI.PackageEngine.Managers.SkillsManager;

/// <summary>
/// Keeps the skills each repository and index source offers, so a search does not clone a
/// repository or download an index on every query. A stale listing is still used while it is
/// refreshed in the background.
/// </summary>
internal sealed class SkillSourceListings(ISkillsBackend backend)
{
    private static readonly TimeSpan Freshness = TimeSpan.FromHours(1);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(10);

    private sealed record Listing(DateTime FetchedAt, IReadOnlyList<AvailableSkill> Skills, bool Failed);

    private readonly ConcurrentDictionary<string, Listing> _listings = new(StringComparer.OrdinalIgnoreCase);

    // Lazy, because a concurrent dictionary may call its value factories more than once, and each
    // started load would clone the repository again
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<AvailableSkill>>>> _refreshes = new(
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>
    /// The skills the source offers. When nothing is known about the source yet, waits up to
    /// <paramref name="wait"/> for them and returns null if they do not arrive in time.
    /// </summary>
    public IReadOnlyList<AvailableSkill>? Get(SkillSourceLocator source, TimeSpan wait)
    {
        if (_listings.TryGetValue(source.Name, out var listing))
        {
            TimeSpan age = DateTime.UtcNow - listing.FetchedAt;
            if (age >= (listing.Failed ? RetryAfterFailure : Freshness))
                _ = Refresh(source);

            return listing.Skills;
        }

        var refresh = Refresh(source);
        return refresh.Wait(wait) ? refresh.Result : null;
    }

    /// <summary>Loads the skills the source offers again; concurrent calls share one load.</summary>
    public Task<IReadOnlyList<AvailableSkill>> Refresh(SkillSourceLocator source) =>
        _refreshes
            .AddOrUpdate(
                source.Name,
                _ => NewLoad(source),
                (_, running) => running.Value.IsCompleted ? NewLoad(source) : running
            )
            .Value;

    private Lazy<Task<IReadOnlyList<AvailableSkill>>> NewLoad(SkillSourceLocator source) =>
        new(() => Task.Run(() => Load(source)));

    /// <summary>Records a listing obtained elsewhere, such as when the source was added.</summary>
    public void Store(SkillSourceLocator source, IReadOnlyList<AvailableSkill> skills) =>
        _listings[source.Name] = new Listing(DateTime.UtcNow, skills, Failed: false);

    public void Forget(SkillSourceLocator source) => _listings.TryRemove(source.Name, out _);

    /// <summary>A skill the source is known to offer, without loading anything.</summary>
    public AvailableSkill? Find(SkillSourceLocator source, string skill) =>
        _listings.TryGetValue(source.Name, out var listing)
            ? listing.Skills.FirstOrDefault(s => s.Name.Equals(skill, StringComparison.OrdinalIgnoreCase))
            : null;

    private IReadOnlyList<AvailableSkill> Load(SkillSourceLocator source)
    {
        try
        {
            var skills = backend.GetAvailableSkills(source.InstallSource, null, CancellationToken.None);
            Store(source, skills);
            return skills;
        }
        catch (Exception ex)
        {
            // Remembered as an empty listing, retried after a while
            Logger.Warn($"Could not list the skills of the source {source.Name}: {ex.Message}");
            _listings[source.Name] = new Listing(DateTime.UtcNow, [], Failed: true);
            return [];
        }
    }
}
