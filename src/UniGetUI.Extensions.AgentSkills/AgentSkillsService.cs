using System.Collections.Concurrent;
using Devolutions.AgentSkills;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;

namespace UniGetUI.Extensions.AgentSkills;

public enum SkillAction { Install, Update, Remove }

public sealed record SourceAddResult(SkillSourceLocator Source, bool Added);

public sealed record SkillItem(string Id, string Description, SkillSourceLocator? Source,
    string Version = "latest", string? NewVersion = null, IReadOnlyList<string>? Agents = null,
    bool IsInstalled = false, bool HasUpdate = false)
{
    public string Name => CoreTools.FormatAsName(Id);
    public string SourceName => Source?.Name ?? CoreTools.Translate("Local");
    public string Provenance => $"{SourceName} · {Version}"
        + (Agents is { Count: > 0 } ? $" · {string.Join(", ", Agents)}" : "");
    public string DiscoverStatus => HasUpdate ? CoreTools.Translate("Update available")
        : IsInstalled ? CoreTools.Translate("Installed") : "";
    public bool HasDiscoverStatus => IsInstalled || HasUpdate;
    public bool CanInstallFromDiscover => !IsInstalled && !HasUpdate;
}

/// <summary>
/// Optional Agent Skills extension. Its domain objects and operations are independent of the
/// package engine; the bundled library owns the agent folders and tracking files.
/// </summary>
public sealed class AgentSkillsService
{
    internal static readonly string SourcesListKey = Settings.ResolveKey(Settings.K.SkillsSources);
    internal static readonly string TargetAgentsListKey = Settings.ResolveKey(Settings.K.SkillsTargetAgents);
    internal const string LatestVersion = "latest";
    internal const string UntrackedVersion = "local";
    private static readonly SemaphoreSlim OperationGate = new(1, 1);
    private readonly ISkillsBackend _backend;
    private readonly ISkillsShClient _registry;
    private readonly SkillSourceListings _listings;
    private static readonly object RegistryGate = new();
    private readonly ConcurrentDictionary<(string Source, string Skill), SkillSourceLocator> _registrySkills = new();
    private readonly ConcurrentDictionary<string, (SkillUpdate Update, SkillSourceLocator Source)> _pendingUpdates =
        new(StringComparer.OrdinalIgnoreCase);
    private static int _registryGeneration;
    private int _admittedGeneration = -1;

    public AgentSkillsService() : this(new AgentSkillsBackend()) { }

    internal AgentSkillsService(ISkillsBackend backend, ISkillsShClient? registry = null)
    {
        _backend = backend;
        _registry = registry ?? new SkillsShClient();
        _listings = new(backend);
    }

    public static IReadOnlyList<SkillSourceLocator> GetConfiguredSources() =>
        (Settings.GetList<string>(SourcesListKey) ?? [])
        .Select(SkillSourceLocator.Parse).OfType<SkillSourceLocator>()
        .DistinctBy(source => source.Name, StringComparer.Ordinal).ToArray();

    internal static SkillSourceLocator? GetConfiguredSource(string name) =>
        GetConfiguredSources().FirstOrDefault(source => source.Name.Equals(name, StringComparison.Ordinal));

    internal static SkillSourceLocator? FindConfiguredSource(SkillSourceLocator source) =>
        GetConfiguredSources().Where(configured => configured.Covers(source))
        .OrderByDescending(configured => configured.Url.AbsolutePath.Length).FirstOrDefault();

    internal static bool IsAllowedSource(SkillSourceLocator source) => FindConfiguredSource(source) is not null;

    internal SkillSourceLocator? GetInstallSource(SkillItem skill)
    {
        if (skill.Source is not { } source) return null;
        if (FindConfiguredSource(source) is { } configured) return configured;
        lock (RegistryGate)
            return SkillsShDiscoveryEnabled && _admittedGeneration == _registryGeneration
                && _registrySkills.TryGetValue((source.Name, skill.Id), out var discovered) ? discovered : null;
    }

    public bool SkillsShDiscoveryEnabled
    {
        get => Settings.Get(Settings.K.SkillsShDiscoveryEnabled);
        set
        {
            lock (RegistryGate)
            {
                bool previous = SkillsShDiscoveryEnabled;
                Settings.Set(Settings.K.SkillsShDiscoveryEnabled, value);
                if (File.Exists(Path.Combine(CoreData.UniGetUIUserConfigurationDirectory,
                    Settings.ResolveKey(Settings.K.SkillsShDiscoveryEnabled))) != value)
                {
                    Settings.Set(Settings.K.SkillsShDiscoveryEnabled, previous);
                    throw new IOException(CoreTools.Translate("The skills.sh discovery preference could not be saved. Check your settings folder permissions and try again."));
                }
                ClearRegistryResults();
            }
        }
    }

    internal void ClearRegistryResults()
    {
        lock (RegistryGate)
        {
            _registryGeneration++;
            _registrySkills.Clear();
        }
    }

    internal static SkillSourceLocator? GetUpdateSource(SkillUpdate update, InstalledSkillInfo? installed) =>
        installed is null or { SourceType: "well-known" }
            ? SkillSourceLocator.Parse(update.Source) : SkillSourceLocator.ForInstalled(installed);

    internal static IReadOnlyList<string>? GetTargetAgents() =>
        Settings.GetList<string>(TargetAgentsListKey) is { Count: > 0 } agents ? agents : null;

    public static IReadOnlyList<string> TargetAgents
    {
        get => GetTargetAgents() ?? [];
        set => Settings.SetList(TargetAgentsListKey, value.Distinct().ToList());
    }

    public IReadOnlyList<AgentInfo> GetSupportedAgents() => _backend.GetAgents();
    public Task<SkillPreview> PreviewAsync(SkillItem skill, CancellationToken token = default)
    {
        var source = GetInstallSource(skill) ?? throw NotAllowed(skill.SourceName);
        return Task.Run(() => _backend.PreviewSkill(source.InstallSource, skill.Id, token), token);
    }

    public bool HasNotionSources => GetConfiguredSources().Any(source => source.Kind is SkillSourceKind.Notion);
    public Task<NotionStatus> GetNotionStatusAsync() =>
        Task.Run(() => _backend.GetNotionStatus(CancellationToken.None));
    public Task<NotionSignIn> BeginNotionSignInAsync(CancellationToken token) =>
        Task.Run(() => _backend.BeginNotionSignIn(token), token);
    public async Task CompleteNotionSignInAsync(CancellationToken token)
    {
        await Task.Run(() => _backend.CompleteNotionSignIn(token), token);
        RefreshNotionSources();
    }
    public async Task SignOutOfNotionAsync()
    {
        await Task.Run(() => _backend.SignOutOfNotion(CancellationToken.None));
        RefreshNotionSources();
    }
    public void RefreshNotionSources()
    {
        NotionCliTool.Forget();
        foreach (var source in GetConfiguredSources().Where(source => source.Kind is SkillSourceKind.Notion))
            _listings.Forget(source);
    }

    internal static string ShortHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return LatestVersion;
        if (hash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) hash = hash[7..];
        return hash.Length > 7 ? hash[..7] : hash;
    }

    /// <summary>
    /// Finds skills in the configured sources and, when its discovery is on, on skills.sh. With
    /// <paramref name="filterSources"/> off, every skill of the configured sources is returned and
    /// only the skills.sh search uses the query. <paramref name="refreshSources"/> fetches the sources
    /// again instead of reusing listings from the last hour.
    /// </summary>
    public IReadOnlyList<SkillItem> Search(string query, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default, bool filterSources = true, bool refreshSources = false)
    {
        Dictionary<(string, string), SkillItem> found = [];
        int generation;
        Task<IReadOnlyList<SkillsShResult>>? registrySearch;
        lock (RegistryGate)
        {
            generation = _registryGeneration;
            registrySearch = SkillsShDiscoveryEnabled && query.Trim().Length >= 2
                ? Task.Run(() => _registry.Search(query), cancellationToken) : null;
        }
        foreach (var source in GetConfiguredSources())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var skills = _listings.Get(source, progress, cancellationToken, refreshSources);
                foreach (var skill in skills.Where(skill => !filterSources
                    || skill.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || skill.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                    || (skill.PluginName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)))
                    found.TryAdd((source.Name, skill.Name), new(skill.Name, skill.Description, source));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                progress?.Report(CoreTools.Translate("Could not search {0}: {1}", source.Name, ex.Message));
                Logger.Warn(ex);
            }
        }
        if (registrySearch is not null)
        {
            try
            {
                var discovered = registrySearch.WaitAsync(cancellationToken).GetAwaiter().GetResult();
                lock (RegistryGate)
                {
                    if (SkillsShDiscoveryEnabled && generation == _registryGeneration)
                    {
                        if (_admittedGeneration != generation) _registrySkills.Clear();
                        _admittedGeneration = generation;
                        foreach (var skill in discovered)
                        {
                            _registrySkills[(skill.Source.Name, skill.Name)] = skill.Source;
                            found.TryAdd((skill.Source.Name, skill.Name), new(skill.Name,
                                CoreTools.Translate("Discovered on skills.sh. Installs from the repository or website shown as its source. Review the skill before installing it."),
                                skill.Source));
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(CoreTools.Translate("Could not search skills.sh: {0}", ex.Message));
            }
        }
        return [.. found.Values];
    }

    public IReadOnlyList<SkillItem> ListInstalled(CancellationToken token = default) =>
        _backend.GetInstalledSkills(token).Select(skill => new SkillItem(skill.Name, skill.Description,
            SkillSourceLocator.ForInstalled(skill), skill.IsTracked ? ShortHash(skill.Hash) : UntrackedVersion,
            Agents: skill.Agents)).ToArray();

    public IReadOnlyList<SkillItem> CheckUpdates(IProgress<string>? progress = null, CancellationToken token = default)
    {
        var installed = _backend.GetInstalledSkills(token);
        string[] eligible = installed.Where(skill =>
            SkillSourceLocator.ForInstalled(skill) is { } source && IsAllowedSource(source))
            .Select(skill => skill.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        _pendingUpdates.Clear();
        if (eligible.Length == 0) return [];
        var check = _backend.CheckForUpdates(eligible, progress, token);
        List<SkillItem> updates = [];
        foreach (var update in check.Updates)
        {
            var skill = installed.FirstOrDefault(skill => skill.Name.Equals(update.Name, StringComparison.OrdinalIgnoreCase));
            var source = GetUpdateSource(update, skill);
            if (source is null || !IsAllowedSource(source)) continue;
            _pendingUpdates[update.Name] = (update, source);
            updates.Add(new(update.Name, skill?.Description ?? "", source, ShortHash(update.CurrentHash),
                ShortHash(update.LatestHash), skill?.Agents));
        }
        foreach (var skipped in check.Unchecked) progress?.Report($"{skipped.Name}: {skipped.Reason}");
        return updates;
    }

    public async Task PerformAsync(SkillItem skill, SkillAction action, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default, IReadOnlyList<string>? targetAgents = null)
    {
        await OperationGate.WaitAsync(cancellationToken);
        try
        {
            await Task.Run(() =>
            {
                switch (action)
                {
                    case SkillAction.Install:
                        var source = GetInstallSource(skill) ?? throw NotAllowed(skill.SourceName);
                        var install = _backend.Install(source.InstallSource, skill.Id, targetAgents ?? GetTargetAgents(), progress, cancellationToken);
                        var installedOutcome = install.Skills.FirstOrDefault(s => s.Name.Equals(skill.Id, StringComparison.OrdinalIgnoreCase));
                        RequireSuccess(installedOutcome?.Status, installedOutcome?.Error, skill.Id);
                        RememberInstalledSource(source);
                        break;
                    case SkillAction.Update:
                        SkillUpdate? update = null;
                        if (_pendingUpdates.TryRemove(skill.Id, out var pending) && IsAllowedSource(pending.Source))
                            update = pending.Update;
                        if (update is null)
                        {
                            var installed = _backend.GetInstalledSkills(cancellationToken)
                                .FirstOrDefault(s => s.Name.Equals(skill.Id, StringComparison.OrdinalIgnoreCase));
                            if (installed is null || SkillSourceLocator.ForInstalled(installed) is not { } original
                                || !IsAllowedSource(original)) throw NotAllowed(skill.SourceName);
                            var check = _backend.CheckForUpdates([skill.Id], progress, cancellationToken);
                            if (check.Unchecked.FirstOrDefault(s => s.Name.Equals(skill.Id, StringComparison.OrdinalIgnoreCase)) is { } skipped)
                                throw new SkillsException(skipped.Reason);
                            update = check.Updates.FirstOrDefault(s => s.Name.Equals(skill.Id, StringComparison.OrdinalIgnoreCase));
                            if (update is null) return;
                            if (GetUpdateSource(update, installed) is not { } actual || !IsAllowedSource(actual))
                                throw NotAllowed(skill.SourceName);
                        }
                        var result = _backend.Update([update], progress, cancellationToken);
                        var updatedOutcome = result.Skills.FirstOrDefault(s => s.Name.Equals(skill.Id, StringComparison.OrdinalIgnoreCase));
                        RequireSuccess(updatedOutcome?.Status, updatedOutcome?.Error, skill.Id);
                        break;
                    case SkillAction.Remove:
                        var removed = _backend.Remove(skill.Id, progress, cancellationToken).Skills.FirstOrDefault();
                        if (removed?.Status != SkillOperationStatus.NotFound) RequireSuccess(removed?.Status, removed?.Error, skill.Id);
                        break;
                    default: throw new ArgumentOutOfRangeException(nameof(action));
                }
            }, cancellationToken);
        }
        finally { OperationGate.Release(); }
    }

    private static void RequireSuccess(SkillOperationStatus? status, string? error, string skill)
    {
        if (status is not SkillOperationStatus.Succeeded)
            throw new SkillsException(error ?? CoreTools.Translate("{0} could not be changed", skill));
    }

    private static SkillsException NotAllowed(string source) => new(CoreTools.Translate(
        "{0} is not one of your Agent Skills sources. Add this source first, then try again.", source));

    private static void RememberInstalledSource(SkillSourceLocator source)
    {
        if (FindConfiguredSource(source) is not null) return;
        Settings.AddToList(SourcesListKey, source.InstallSource);
        if (FindConfiguredSource(source) is null)
            throw new SkillsException(CoreTools.Translate("The skill was installed, but its source {0} could not be saved. Add it in Agent Skills settings to receive updates.", source.Name));
    }

    /// <summary>
    /// Adds a source, reading its skills first so a source that offers none is refused. A source that
    /// is already added is not read again; the result tells which source the address resolved to and
    /// whether it was new.
    /// </summary>
    public async Task<SourceAddResult> AddSourceAsync(string address, IProgress<string>? progress = null, CancellationToken token = default)
    {
        var source = SkillSourceLocator.ParseUserInput(address) ?? throw new ArgumentException(CoreTools.Translate(
            "Enter a GitHub repository (owner/repo), a git repository URL, an HTTPS skills index, or a Notion database."));
        if (FindConfiguredSource(source) is { } existing) return new(existing, false);
        try
        {
            var skills = await Task.Run(() => _backend.GetAvailableSkills(source.InstallSource, progress, token), token);
            if (skills.Count == 0) throw new SkillsException(CoreTools.Translate("This source does not offer any skills."));
            _listings.Store(source, skills);
        }
        catch (SkillsException ex) when (source.Kind is SkillSourceKind.Notion
            && ex.Failure is SkillsFailure.NotionCliMissing or SkillsFailure.NotionSignedOut)
        {
            progress?.Report(CoreTools.Translate("Sign in to Notion in the Agent Skills settings to see the skills of this source."));
        }
        RememberInstalledSource(source);
        return new(FindConfiguredSource(source) ?? source, true);
    }

    /// <summary>The skills one configured source offers.</summary>
    public IReadOnlyList<SkillItem> ListSource(SkillSourceLocator source, IProgress<string>? progress = null,
        CancellationToken token = default) =>
        _listings.Get(source, progress, token).Select(skill => new SkillItem(skill.Name, skill.Description, source)).ToArray();

    public void RemoveSource(SkillSourceLocator source)
    {
        Settings.SetList(SourcesListKey, (Settings.GetList<string>(SourcesListKey) ?? [])
            .Where(entry => !(SkillSourceLocator.Parse(entry)?.Name ?? entry).Equals(source.Name, StringComparison.Ordinal)).ToList());
        ClearRegistryResults();
        _listings.Forget(source);
    }
}
