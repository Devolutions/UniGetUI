using System.Collections.Concurrent;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.Managers.SkillsManager;

/// <summary>
/// Resolves source names for the Agent Skills manager. A name resolves to a source only when
/// skills may be installed from it (a configured source), which keeps bundle imports and installs within the
/// sources UniGetUI controls.
/// </summary>
internal sealed class SkillsSourceFactory(AgentSkills manager) : ISourceFactory
{
    private static readonly Uri PlaceholderUrl = new("https://agentskills.io/");

    private readonly ConcurrentDictionary<
        string,
        (SkillSourceLocator? Locator, IManagerSource Source)
    > _sources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The source object for a locator. Every locator with the same name gets the same object, so
    /// the packages of one source share it (the source filter compares sources by reference). The
    /// name of an added source gets that source as it was added, so a locator from a search result,
    /// a bundle or a lock file cannot bring an address of its own.
    /// </summary>
    public IManagerSource GetOrCreate(SkillSourceLocator locator) =>
        _sources
            .GetOrAdd(locator.Name, _ => Create(AgentSkills.GetConfiguredSource(locator.Name) ?? locator))
            .Source;

    private (SkillSourceLocator? Locator, IManagerSource Source) Create(SkillSourceLocator locator) =>
        (locator, new ManagerSource(manager, locator.Name, locator.Url));

    /// <summary>
    /// The locator of a source object: the one it was created with by this factory (none for
    /// placeholders and the local source), or else the one its URL describes, for sources a user
    /// is adding.
    /// </summary>
    public SkillSourceLocator? GetLocator(IManagerSource source)
    {
        if (_sources.TryGetValue(source.Name, out var entry) && ReferenceEquals(entry.Source, source))
            return entry.Locator;

        if (source.IsVirtualManager)
            return null;

        return SkillSourceLocator.Parse(source.Url.OriginalString) ?? SkillSourceLocator.Parse(source.Name);
    }

    public IManagerSource? GetSourceIfExists(string name)
    {
        var configured = AgentSkills
            .GetConfiguredSources()
            .FirstOrDefault(source => source.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (configured is not null)
            return GetOrCreate(configured);

        return SkillSourceLocator.Parse(name) is { } locator && AgentSkills.IsAllowedSource(locator)
            ? GetOrCreate(locator)
            : null;
    }

    public IManagerSource GetSourceOrDefault(string name)
    {
        if (GetSourceIfExists(name) is { } source)
            return source;

        if (SkillSourceLocator.Parse(name) is { } locator)
            return GetOrCreate(locator);

        return _sources
            .GetOrAdd(name, _ => (null, new ManagerSource(manager, name, PlaceholderUrl)))
            .Source;
    }

    public void AddSource(IManagerSource source)
    {
        _sources.TryAdd(
            source.Name,
            (SkillSourceLocator.Parse(source.Url.OriginalString), source)
        );
    }

    public IManagerSource[] GetAvailableSources() =>
        AgentSkills.GetConfiguredSources().Select(GetOrCreate).ToArray();

    /// <summary>
    /// Does nothing: the configured sources are read from the settings on every lookup, and the
    /// source objects must stay the same for the packages that already reference them.
    /// </summary>
    public void Reset() { }
}
