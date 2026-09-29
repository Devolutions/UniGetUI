using Skills;

namespace Devolutions.AgentSkills;

/// <summary>Settings for a <see cref="SkillsManager"/>.</summary>
public sealed class SkillsManagerOptions
{
    /// <summary>Project root for <see cref="SkillScope.Project"/> operations. Defaults to the current directory when the manager is created.</summary>
    public string? ProjectDirectory { get; init; }

    /// <summary>Home directory override for <see cref="SkillScope.Global"/> operations and agent detection (tests and sandboxes). Defaults to the user's home.</summary>
    public string? HomeDirectory { get; init; }

    /// <summary>
    /// GitHub token for API calls and clones (raises the anonymous rate limit of 60 requests per hour and reaches private repositories).
    /// Defaults to the <c>GITHUB_TOKEN</c> or <c>GH_TOKEN</c> environment variable.
    /// </summary>
    public string? GitHubToken { get; init; }

    /// <summary>
    /// The Notion CLI (<c>ntn</c>) that Notion sources and sign-in run, for hosts that install it where <c>PATH</c> does
    /// not reach yet. Defaults to <c>ntn</c> on <c>PATH</c>.
    /// </summary>
    public string? NotionCliPath { get; init; }

    /// <summary>Tests: answers Notion CLI calls instead of starting <c>ntn</c>.</summary>
    internal Func<IReadOnlyList<string>, ProcOutput>? NotionRunner { get; init; }

    /// <summary>Tests: fetches Notion skill archives instead of downloading them.</summary>
    internal Func<string, DownloadedSource>? NotionDownloader { get; init; }
}

/// <summary>
/// Installs, lists, updates and removes agent skills using the same install locations
/// and <c>~/.agents/.skill-lock.json</c> and <c>skills-lock.json</c> lock files as the CLI.
/// </summary>
/// <remarks>
/// Methods are synchronous and may block on the network, <c>git</c> and the file system; call them off the UI
/// thread, or use the <c>Async</c> variants, which run them on the thread pool. Cancellation stops between steps,
/// aborts HTTP requests and kills a running <c>git</c>. Instances are thread-safe. Cloning needs <c>git</c> on
/// <c>PATH</c>; direct downloads and supported external providers may not.
/// </remarks>
public sealed class SkillsManager
{
    private readonly SkillsManagerOptions _options;
    private readonly string? _home;
    private readonly Dictionary<string, string?>? _env;

    /// <summary>Creates a manager.</summary>
    public SkillsManager(SkillsManagerOptions? options = null)
    {
        _options = options ?? new SkillsManagerOptions();
        ProjectDirectory = NodePath.Resolve(_options.ProjectDirectory ?? Environment.CurrentDirectory);
        // path.resolve semantics, like the CLI: no 8.3 short-name expansion (C:\Users\RUNNER~1 stays as given).
        _home = _options.HomeDirectory is { } home ? NodePath.Resolve(home) : null;
        if (!string.IsNullOrEmpty(_options.GitHubToken)) _env = new Dictionary<string, string?> { ["GITHUB_TOKEN"] = _options.GitHubToken };
    }

    /// <summary>The library version.</summary>
    public static string Version => LibraryInfo.Version;

    /// <summary>Project root used for <see cref="SkillScope.Project"/> operations.</summary>
    public string ProjectDirectory { get; }

    private T Run<T>(Func<Action<string>, T> body, IProgress<string>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Action<string> log = progress == null ? static _ => { } : progress.Report;
        var context = new SysContext
        {
            Cwd = ProjectDirectory,
            Home = _home,
            Env = _env,
            Warn = log,
            Cancel = cancellationToken,
            NotionCli = _options.NotionCliPath,
            NotionRunner = _options.NotionRunner,
            NotionDownloader = _options.NotionDownloader,
        };
        using (Sys.Enter(context)) return body(log);
    }

    private static IReadOnlyList<SkillScope> Scopes(SkillScope? scope) => scope is { } s ? [s] : [SkillScope.Global, SkillScope.Project];

    // ─── Agents ───

    /// <summary>Every supported agent, with where it keeps skills and whether it is detected.</summary>
    public IReadOnlyList<AgentInfo> GetAgents() => Run(_ =>
        Skills.Agents.List.Select(a => new AgentInfo(a.Name, a.DisplayName, a.SkillsDir, a.GlobalSkillsDir,
            Skills.Agents.IsUniversalAgent(a.Name), Skills.Agents.DetectInstalled(a.Name))).ToList(), null, default);

    // ─── Installed skills ───

    /// <summary>Installed skills in one scope, or both when <paramref name="scope"/> is null.</summary>
    public IReadOnlyList<InstalledSkillInfo> GetInstalledSkills(SkillScope? scope = null, CancellationToken cancellationToken = default) =>
        Run(_ => Inventory.List(scope), null, cancellationToken);

    /// <inheritdoc cref="GetInstalledSkills"/>
    public Task<IReadOnlyList<InstalledSkillInfo>> GetInstalledSkillsAsync(SkillScope? scope = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => GetInstalledSkills(scope, cancellationToken), cancellationToken);

    // ─── Sources ───

    /// <summary>The skills a source offers, without installing anything (<c>skills add --list</c>).</summary>
    /// <param name="source">A source in any form <see cref="SkillInstallRequest.Source"/> accepts.</param>
    /// <param name="fullDepth">Search every subdirectory, even when the source root has a SKILL.md.</param>
    /// <param name="progress">Receives progress and warning lines.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="SkillsException">The source could not be fetched or has no skills.</exception>
    public IReadOnlyList<AvailableSkill> GetAvailableSkills(string source, bool fullDepth = false, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return Run(log => InstallEngine.ListAvailable(source, fullDepth, log), progress, cancellationToken);
    }

    /// <inheritdoc cref="GetAvailableSkills"/>
    public Task<IReadOnlyList<AvailableSkill>> GetAvailableSkillsAsync(string source, bool fullDepth = false, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => GetAvailableSkills(source, fullDepth, progress, cancellationToken), cancellationToken);

    // ─── Install ───

    /// <summary>Install skills from a source (<c>skills add -y</c>) and record them in the lock file.</summary>
    /// <param name="request">What to install.</param>
    /// <param name="progress">Receives progress and warning lines.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>One outcome per skill; per-agent failures are reported there rather than thrown.</returns>
    /// <exception cref="SkillsException">The source could not be fetched, has no skills, or has none of the requested skills.</exception>
    /// <exception cref="ArgumentException">An unknown agent id.</exception>
    public SkillInstallResult Install(SkillInstallRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Source);
        return Run(log => InstallEngine.Install(request, log), progress, cancellationToken);
    }

    /// <inheritdoc cref="Install"/>
    public Task<SkillInstallResult> InstallAsync(SkillInstallRequest request, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Install(request, progress, cancellationToken), cancellationToken);

    // ─── Remove ───

    /// <summary>
    /// Remove installed skills (<c>skills remove -y</c>). The canonical copy and lock entry are removed unless another
    /// detected agent still uses the skill.
    /// </summary>
    /// <param name="skillNames">Skill names; <c>*</c> removes every skill in the scope.</param>
    /// <param name="scope">Scope to remove from.</param>
    /// <param name="agents">Agent ids to remove from; null removes from every agent.</param>
    /// <param name="progress">Receives progress and warning lines.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <exception cref="ArgumentException">An unknown agent id.</exception>
    public SkillRemoveResult Remove(IEnumerable<string> skillNames, SkillScope scope = SkillScope.Global, IEnumerable<string>? agents = null,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(skillNames);
        var names = skillNames.ToList();
        var agentList = agents?.ToList();
        return Run(log => Inventory.Remove(names, scope, agentList, log), progress, cancellationToken);
    }

    /// <inheritdoc cref="Remove"/>
    public Task<SkillRemoveResult> RemoveAsync(IEnumerable<string> skillNames, SkillScope scope = SkillScope.Global, IEnumerable<string>? agents = null,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Remove(skillNames, scope, agents, progress, cancellationToken), cancellationToken);

    // ─── Updates ───

    /// <summary>
    /// Check tracked skills for changes in their source. Nothing is modified. Skills installed from a local path,
    /// untracked skills and sources that cannot be reached are reported in <see cref="SkillUpdateCheckResult.Unchecked"/>.
    /// </summary>
    /// <param name="scope">Scope to check, or both when null.</param>
    /// <param name="skillNames">Only check these skills (case-insensitive); null checks all.</param>
    /// <param name="progress">Receives progress and warning lines.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public SkillUpdateCheckResult CheckForUpdates(SkillScope? scope = null, IEnumerable<string>? skillNames = null,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var filter = skillNames?.ToList();
        return Run(log => UpdateEngine.Check(Scopes(scope), filter, log), progress, cancellationToken);
    }

    /// <inheritdoc cref="CheckForUpdates"/>
    public Task<SkillUpdateCheckResult> CheckForUpdatesAsync(SkillScope? scope = null, IEnumerable<string>? skillNames = null,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => CheckForUpdates(scope, skillNames, progress, cancellationToken), cancellationToken);

    /// <summary>Apply updates found by <see cref="CheckForUpdates"/> by reinstalling each skill from its source.</summary>
    /// <param name="updates">Updates returned by <see cref="CheckForUpdates"/>.</param>
    /// <param name="progress">Receives progress and warning lines.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public SkillUpdateResult Update(IEnumerable<SkillUpdate> updates, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);
        var list = updates.ToList();
        return Run(log => UpdateEngine.Update(list, log), progress, cancellationToken);
    }

    /// <inheritdoc cref="Update(IEnumerable{SkillUpdate}, IProgress{string}?, CancellationToken)"/>
    public Task<SkillUpdateResult> UpdateAsync(IEnumerable<SkillUpdate> updates, IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Update(updates, progress, cancellationToken), cancellationToken);

    /// <summary>Check the given skills (or all) for updates and apply the ones found (<c>skills update -y</c>).</summary>
    /// <param name="scope">Scope to update, or both when null.</param>
    /// <param name="skillNames">Only update these skills; null updates all.</param>
    /// <param name="progress">Receives progress and warning lines.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    public SkillUpdateResult Update(SkillScope? scope = null, IEnumerable<string>? skillNames = null,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var filter = skillNames?.ToList();
        return Run(log => UpdateEngine.Update(UpdateEngine.Check(Scopes(scope), filter, log).Updates, log), progress, cancellationToken);
    }

    /// <inheritdoc cref="Update(SkillScope?, IEnumerable{string}?, IProgress{string}?, CancellationToken)"/>
    public Task<SkillUpdateResult> UpdateAsync(SkillScope? scope = null, IEnumerable<string>? skillNames = null,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default) =>
        Task.Run(() => Update(scope, skillNames, progress, cancellationToken), cancellationToken);

    // ─── Notion ───

    /// <summary>
    /// Whether the Notion CLI (<c>ntn</c>), which Notion sources go through, is installed and signed in. The CLI keeps
    /// its token in the OS credential store; the library never sees it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the check.</param>
    public NotionStatus GetNotionStatus(CancellationToken cancellationToken = default) =>
        Run(_ => NotionCli.GetStatus(), null, cancellationToken);

    /// <inheritdoc cref="GetNotionStatus"/>
    public Task<NotionStatus> GetNotionStatusAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => GetNotionStatus(cancellationToken), cancellationToken);

    /// <summary>
    /// Start signing the Notion CLI in: returns the Notion page to open in the user's browser and the code it shows.
    /// Call <see cref="CompleteNotionSignIn"/> next, which waits for the user to approve.
    /// </summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="SkillsException">The Notion CLI is missing (<see cref="SkillsFailure.NotionCliMissing"/>) or failed.</exception>
    public NotionSignIn BeginNotionSignIn(CancellationToken cancellationToken = default) =>
        Run(_ => NotionCall(NotionCli.BeginSignIn), null, cancellationToken);

    /// <inheritdoc cref="BeginNotionSignIn"/>
    public Task<NotionSignIn> BeginNotionSignInAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => BeginNotionSignIn(cancellationToken), cancellationToken);

    /// <summary>
    /// Wait, up to 15 minutes, for the user to approve the sign-in started by <see cref="BeginNotionSignIn"/>.
    /// Cancelling stops waiting.
    /// </summary>
    /// <param name="cancellationToken">Stops waiting.</param>
    /// <exception cref="SkillsException">The sign-in was refused, expired or failed.</exception>
    public void CompleteNotionSignIn(CancellationToken cancellationToken = default) =>
        Run(_ => NotionCall(() => { NotionCli.CompleteSignIn(); return true; }), null, cancellationToken);

    /// <inheritdoc cref="CompleteNotionSignIn"/>
    public Task CompleteNotionSignInAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => CompleteNotionSignIn(cancellationToken), cancellationToken);

    /// <summary>Sign the Notion CLI out of its workspace, deleting the token it keeps.</summary>
    /// <param name="cancellationToken">Cancels the request.</param>
    /// <exception cref="SkillsException">The Notion CLI is missing or failed.</exception>
    public void SignOutOfNotion(CancellationToken cancellationToken = default) =>
        Run(_ => NotionCall(() => { NotionCli.SignOut(); return true; }), null, cancellationToken);

    private static T NotionCall<T>(Func<T> call)
    {
        try
        {
            return call();
        }
        catch (NotionCliException e)
        {
            throw e.ToSkillsException();
        }
    }
}
