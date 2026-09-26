using System.Collections.Concurrent;
using Devolutions.AgentSkills;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Manager.Classes;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.ManagerClasses.Manager;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.PackageEngine.Managers.SkillsManager;

/// <summary>
/// Agent skills: folders with a SKILL.md that coding agents such as Claude Code, GitHub Copilot,
/// Cursor and Codex load. UniGetUI decides which sources skills come from; the Devolutions.AgentSkills
/// library installs them in process, for the user, into every targeted agent's skills folder,
/// compatibly with the skills CLI and gh skill.
/// </summary>
public sealed class AgentSkills : PackageManager
{
    // The list settings API takes names, which come from Settings.K so that importing settings,
    // which only restores the names Settings.K knows, keeps them

    /// <summary>Repository and index sources the user added (list setting).</summary>
    internal static readonly string SourcesListKey = Settings.ResolveKey(Settings.K.SkillsSources);

    /// <summary>Agent ids to install skills for; empty for the detected and universal agents (list setting).</summary>
    internal static readonly string TargetAgentsListKey = Settings.ResolveKey(Settings.K.SkillsTargetAgents);

    /// <summary>Version shown for skills whose content hash is not known yet.</summary>
    internal const string LatestVersion = "latest";

    /// <summary>Version shown for skills no lock file tracks.</summary>
    internal const string UntrackedVersion = "local";

    private const int SearchLimit = 50;

    // Long enough to clone a repository the first time it is searched, within the 60-second
    // limit of listing tasks
    private static readonly TimeSpan ListingWait = TimeSpan.FromSeconds(40);

    private readonly ConcurrentDictionary<
        string,
        (SkillUpdate Update, SkillSourceLocator Source)
    > _pendingUpdates = new(StringComparer.OrdinalIgnoreCase);

    internal ISkillsBackend Backend { get; }
    internal SkillsSourceFactory SourceFactory { get; }
    internal SkillSourceListings Listings { get; }

    /// <summary>The source of skills no lock file tracks, such as ones copied by hand.</summary>
    internal IManagerSource LocalSource { get; }

    public AgentSkills()
        : this(new AgentSkillsBackend()) { }

    internal AgentSkills(ISkillsBackend backend)
    {
        Backend = backend;
        Listings = new SkillSourceListings(backend);

        Capabilities = new ManagerCapabilities
        {
            RunsInProcess = true,
            SupportsCustomSources = true,
            SupportsProxy = ProxySupport.No,
            SupportsProxyAuth = false,
            Sources = new SourceCapabilities { KnowsPackageCount = false, KnowsUpdateDate = false },
        };

        Properties = new ManagerProperties
        {
            Id = "skills",
            Name = "Skills",
            DisplayName = "Agent Skills",
            Description = CoreTools.Translate(
                "Skills for AI coding agents such as Claude Code, GitHub Copilot, Cursor and Codex.<br>Contains: <b>Agent skills (folders with a SKILL.md file)</b>"
            ),
            IconId = IconType.Skills,
            ColorIconId = "skills",
            ExecutableFriendlyName = "skills",
            InstallVerb = "add",
            UpdateVerb = "update",
            UninstallVerb = "remove",
        };

        // Sources refresh their display names from the manager's properties, so they are created
        // once the properties exist
        SourceFactory = new SkillsSourceFactory(this);
        IManagerSource catalog = SourceFactory.GetOrCreate(SkillSourceLocator.Catalog);
        LocalSource = new ManagerSource(
            this,
            CoreTools.Translate("Local"),
            new Uri("https://agentskills.io/"),
            isVirtualManager: true
        );

        var properties = Properties;
        properties.DefaultSource = catalog;
        properties.KnownSources =
        [
            catalog,
            SourceFactory.GetOrCreate(SkillSourceLocator.GitHub("anthropics", "skills")),
            SourceFactory.GetOrCreate(SkillSourceLocator.GitHub("vercel-labs", "agent-skills")),
            SourceFactory.GetOrCreate(SkillSourceLocator.GitHub("github", "awesome-copilot")),
        ];
        Properties = properties;

        DetailsHelper = new SkillsPkgDetailsHelper(this);
        OperationHelper = new SkillsPkgOperationHelper(this);
        SourcesHelper = new SkillsSourceHelper(this);
    }

    /// <summary>Whether the public skills.sh catalog is one of the sources.</summary>
    internal static bool CatalogEnabled => !Settings.Get(Settings.K.DisableSkillsPublicCatalog);

    /// <summary>The configured sources: the skills.sh catalog first when enabled, then the added ones.</summary>
    internal static IReadOnlyList<SkillSourceLocator> GetConfiguredSources()
    {
        List<SkillSourceLocator> sources = [];
        if (CatalogEnabled)
            sources.Add(SkillSourceLocator.Catalog);

        foreach (string entry in Settings.GetList<string>(SourcesListKey) ?? [])
        {
            if (
                SkillSourceLocator.Parse(entry) is { Kind: not SkillSourceKind.Catalog } source
                && !sources.Exists(s => s.Name.Equals(source.Name, StringComparison.OrdinalIgnoreCase))
            )
                sources.Add(source);
        }

        return sources;
    }

    /// <summary>The added repository or index with this name; null for any other name.</summary>
    internal static SkillSourceLocator? GetConfiguredSource(string name) =>
        GetConfiguredSources()
            .FirstOrDefault(source =>
                source.Kind is not SkillSourceKind.Catalog
                && source.Name.Equals(name, StringComparison.OrdinalIgnoreCase)
            );

    /// <summary>
    /// Whether skills may be installed or updated from the given source: from within an added
    /// repository or index, or from any GitHub repository while the skills.sh catalog is enabled.
    /// </summary>
    internal static bool IsAllowedSource(SkillSourceLocator source) =>
        (GetConfiguredSource(source.Name)?.Covers(source) ?? false)
        || (source.IsGitHub && CatalogEnabled);

    /// <summary>
    /// What a skill from the given package source is installed from: the added source of that name,
    /// as it was added, or the GitHub repository while the skills.sh catalog is enabled; null when
    /// skills may not be installed from it.
    /// </summary>
    internal SkillSourceLocator? GetInstallSource(IManagerSource source) =>
        SourceFactory.GetLocator(source) is { } locator
            ? GetConfiguredSource(locator.Name) ?? (locator.IsGitHub && CatalogEnabled ? locator : null)
            : null;

    /// <summary>
    /// The source the library updates a skill from: for a skill from an index, the index address the
    /// update names (the one the skill was installed from); for any other skill, the source its
    /// lock-file entry records.
    /// </summary>
    internal static SkillSourceLocator? GetUpdateSource(SkillUpdate update, InstalledSkillInfo? installed) =>
        installed is null or { SourceType: "well-known" }
            ? SkillSourceLocator.Parse(update.Source)
            : SkillSourceLocator.ForInstalled(installed);

    /// <summary>The agents to install skills for, or null for the detected and universal agents.</summary>
    internal static IReadOnlyList<string>? GetTargetAgents() =>
        Settings.GetList<string>(TargetAgentsListKey) is { Count: > 0 } agents ? agents : null;

    /// <summary>
    /// The ids of the agents skills are installed for. Empty, the default, installs for every
    /// detected agent and for the agents that read the shared .agents/skills folder.
    /// </summary>
    public static IReadOnlyList<string> TargetAgents
    {
        get => GetTargetAgents() ?? [];
        set => Settings.SetList(TargetAgentsListKey, value.Distinct().ToList());
    }

    /// <summary>The coding agents found on this computer, as (id, display name).</summary>
    public IReadOnlyList<(string Id, string DisplayName)> GetDetectedAgents()
    {
        try
        {
            return Backend
                .GetAgents()
                .Where(agent => agent.IsDetected)
                .Select(agent => (agent.Id, agent.DisplayName))
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not detect the coding agents on this computer");
            Logger.Warn(ex);
            return [];
        }
    }

    /// <summary>
    /// The update found for the skill by the last update check, removing it; null when there is
    /// none, or when its source was removed since.
    /// </summary>
    internal SkillUpdate? TakePendingUpdate(string skill) =>
        _pendingUpdates.TryRemove(skill, out var pending) && IsAllowedSource(pending.Source)
            ? pending.Update
            : null;

    /// <summary>The source object an installed skill came from.</summary>
    internal IManagerSource SourceFor(InstalledSkillInfo skill) =>
        SkillSourceLocator.ForInstalled(skill) is { } source
            ? SourceFactory.GetOrCreate(source)
            : LocalSource;

    /// <summary>A short content hash, which is the closest thing skills have to a version.</summary>
    internal static string ShortHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
            return LatestVersion;

        if (hash.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
            hash = hash[7..];
        return hash.Length > 7 ? hash[..7] : hash;
    }

    internal static string VersionOf(InstalledSkillInfo skill) =>
        skill.IsTracked ? ShortHash(skill.Hash) : UntrackedVersion;

    protected override IReadOnlyList<Package> FindPackages_UnSafe(string query)
    {
        INativeTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.FindPackages);
        Dictionary<string, Package> packages = new(StringComparer.OrdinalIgnoreCase);

        void Add(string skill, SkillSourceLocator source)
        {
            var package = new Package(
                CoreTools.FormatAsName(skill),
                skill,
                LatestVersion,
                SourceFactory.GetOrCreate(source),
                this
            );
            packages.TryAdd($"{source.Name}\\{skill}", package);
        }

        var sources = GetConfiguredSources();

        // Sources not listed yet start loading together, and all share one deadline
        DateTime deadline = DateTime.UtcNow + ListingWait;
        foreach (var source in sources.Where(source => source.Kind is not SkillSourceKind.Catalog))
            Listings.Get(source, TimeSpan.Zero);

        foreach (var source in sources)
        {
            try
            {
                if (source.Kind is SkillSourceKind.Catalog)
                {
                    var results = Backend.Search(query, SearchLimit, CancellationToken.None);
                    foreach (var result in results)
                    {
                        if (SkillSourceLocator.ForSearchResult(result) is { } origin)
                            Add(result.Name, origin);
                    }

                    logger.Log($"skills.sh returned {results.Count} skills for \"{query}\"");
                    continue;
                }

                TimeSpan remaining = deadline - DateTime.UtcNow;
                var skills = Listings.Get(source, remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero);
                if (skills is null)
                {
                    logger.Log($"The skills of {source.Name} are still loading");
                    continue;
                }

                foreach (var skill in skills.Where(skill => Matches(skill, query)))
                    Add(skill.Name, source);
            }
            catch (Exception ex)
            {
                logger.Error($"Could not search the source {source.Name}");
                logger.Error(ex);
            }
        }

        logger.Close(0);
        return [.. packages.Values];
    }

    private static bool Matches(AvailableSkill skill, string query) =>
        skill.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
        || skill.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
        || (skill.PluginName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false);

    protected override IReadOnlyList<Package> GetInstalledPackages_UnSafe()
    {
        INativeTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.ListInstalledPackages);
        var skills = Backend.GetInstalledSkills(CancellationToken.None);

        List<Package> packages = [];
        foreach (var skill in skills)
        {
            packages.Add(
                new Package(
                    CoreTools.FormatAsName(skill.Name),
                    skill.Name,
                    VersionOf(skill),
                    SourceFor(skill),
                    this
                )
            );
            logger.Log($"{skill.Name} from {skill.Source ?? UntrackedVersion}, for {string.Join(", ", skill.Agents)}");
        }

        logger.Close(0);
        return packages;
    }

    protected override IReadOnlyList<Package> GetAvailableUpdates_UnSafe()
    {
        INativeTaskLogger logger = TaskLogger.CreateNew(LoggableTaskType.ListUpdates);
        var installed = Backend
            .GetInstalledSkills(CancellationToken.None)
            .GroupBy(skill => skill.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var check = Backend.CheckForUpdates(null, new LoggerProgress(logger), CancellationToken.None);

        _pendingUpdates.Clear();
        List<Package> packages = [];
        foreach (var update in check.Updates)
        {
            var source = GetUpdateSource(update, installed.GetValueOrDefault(update.Name));
            if (source is null)
                continue;

            if (!IsAllowedSource(source))
            {
                logger.Log(
                    $"Not offering the update of {update.Name}: its source {source.Name} is not one of the Agent Skills sources"
                );
                continue;
            }

            _pendingUpdates[update.Name] = (update, source);
            packages.Add(
                new Package(
                    CoreTools.FormatAsName(update.Name),
                    update.Name,
                    ShortHash(update.CurrentHash),
                    ShortHash(update.LatestHash),
                    SourceFactory.GetOrCreate(source),
                    this
                )
            );
        }

        foreach (var skipped in check.Unchecked)
            logger.Log($"Could not check {skipped.Name} for updates: {skipped.Reason}");

        logger.Close(0);
        return packages;
    }

    /// <summary>
    /// Skill versions are content hashes, which have no order: two versions are either the same or
    /// cannot be compared.
    /// </summary>
    public override int? CompareVersions(string versionA, string versionB) =>
        versionA.Equals(versionB, StringComparison.OrdinalIgnoreCase) ? 0 : null;

    /// <summary>
    /// Agent Skills is enabled by default only when a coding agent is found, so people who do not
    /// use one do not get skills in their search results. Runs once, and never overrides the user.
    /// </summary>
    protected override void _performPreInitializationSteps()
    {
        if (Settings.Get(Settings.K.SkillsDefaultEnablementApplied))
            return;

        Settings.Set(Settings.K.SkillsDefaultEnablementApplied, true);
        if (Settings.DictionaryContainsKey<string, bool>(Settings.K.DisabledManagers, Name))
            return;

        bool agentFound = false;
        try
        {
            agentFound = Backend.GetAgents().Any(agent => agent.IsDetected);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not detect the coding agents on this computer");
            Logger.Warn(ex);
        }

        if (!agentFound)
        {
            Settings.SetDictionaryItem(Settings.K.DisabledManagers, Name, true);
            Logger.Info($"{DisplayName} is disabled by default because no coding agent was found");
        }
    }

    protected override void _loadManagerExecutableFile(
        out bool found,
        out string path,
        out string callArguments
    )
    {
        // Skills are installed in process; git is only needed to clone repository sources
        var (gitFound, gitPath) = CoreTools.Which(GitDependency.ExecutableName);
        found = true;
        path = gitFound ? gitPath : "";
        callArguments = "";
    }

    protected override void _loadManagerVersion(out string version) =>
        version = $"Devolutions.AgentSkills {Backend.Version}";

    protected override void _performExtraLoadingSteps()
    {
        var sources = GetConfiguredSources();

        // Only repository sources need git, so people who only use the catalog or indexes are not
        // asked to install it
        Dependencies = sources.Any(source => source.Kind is SkillSourceKind.Repository)
            ? [GitDependency.Create()]
            : [];

        foreach (var source in sources.Where(source => source.Kind is not SkillSourceKind.Catalog))
            _ = Listings.Refresh(source);
    }

    public override IReadOnlyList<string> FindCandidateExecutableFiles() => [];

    /// <summary>Forwards the library's progress lines to a task log.</summary>
    private sealed class LoggerProgress(INativeTaskLogger logger) : IProgress<string>
    {
        public void Report(string value) => logger.Log(value);
    }
}
