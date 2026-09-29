namespace Devolutions.AgentSkills;

/// <summary>Where skills are installed.</summary>
public enum SkillScope
{
    /// <summary>User level: the home directory (<c>~/.agents/skills</c> and each agent's global directory).</summary>
    Global,

    /// <summary>Project level: the project directory (<c>./.agents/skills</c> and each agent's project directory).</summary>
    Project,
}

/// <summary>How a skill reaches agents that do not read the shared <c>.agents/skills</c> directory.</summary>
public enum SkillInstallMode
{
    /// <summary>One canonical copy in <c>.agents/skills</c>, linked into each agent directory (junctions on Windows). Falls back to copying when linking fails.</summary>
    Symlink,

    /// <summary>An independent copy in every agent directory.</summary>
    Copy,
}

/// <summary>Outcome of one skill in an install, update or remove operation.</summary>
public enum SkillOperationStatus
{
    /// <summary>The operation completed.</summary>
    Succeeded,

    /// <summary>The operation failed; see the outcome's error.</summary>
    Failed,

    /// <summary>The requested skill was not found (in the source, or among installed skills).</summary>
    NotFound,
}

/// <summary>A coding agent the library can install skills for.</summary>
/// <param name="Id">Stable identifier, as used by <c>skills add --agent</c> (for example <c>claude-code</c>).</param>
/// <param name="DisplayName">Human-readable name (for example <c>Claude Code</c>).</param>
/// <param name="ProjectSkillsDirectory">Skills directory relative to a project root (for example <c>.claude/skills</c>).</param>
/// <param name="GlobalSkillsDirectory">Absolute user-level skills directory, or null when the agent has no global skills.</param>
/// <param name="IsUniversal">True when the agent reads the shared <c>.agents/skills</c> directory directly.</param>
/// <param name="IsDetected">True when the agent appears to be installed on this machine (or in the project).</param>
public sealed record AgentInfo(
    string Id,
    string DisplayName,
    string ProjectSkillsDirectory,
    string? GlobalSkillsDirectory,
    bool IsUniversal,
    bool IsDetected);

/// <summary>An installed skill, merged with what the lock file knows about its origin.</summary>
public sealed record InstalledSkillInfo
{
    /// <summary>Skill name from the SKILL.md frontmatter.</summary>
    public required string Name { get; init; }

    /// <summary>Skill description from the SKILL.md frontmatter.</summary>
    public required string Description { get; init; }

    /// <summary>Directory holding the skill's SKILL.md.</summary>
    public required string Path { get; init; }

    /// <summary>Scope the skill is installed in.</summary>
    public required SkillScope Scope { get; init; }

    /// <summary>Ids of the agents the skill is installed for (see <see cref="AgentInfo.Id"/>).</summary>
    public required IReadOnlyList<string> Agents { get; init; }

    /// <summary>Source recorded at install time (for example <c>owner/repo</c>), or null for untracked skills.</summary>
    public string? Source { get; init; }

    /// <summary>Source kind: <c>github</c>, <c>gitlab</c>, <c>git</c>, <c>local</c>, <c>well-known</c>, <c>download</c> or <c>notion</c>; null for untracked skills.</summary>
    public string? SourceType { get; init; }

    /// <summary>Full URL of the source, when recorded.</summary>
    public string? SourceUrl { get; init; }

    /// <summary>Base URL of the well-known index, when recorded separately from the skill file URL.</summary>
    public string? SourceBaseUrl { get; init; }

    /// <summary>Git ref (branch, tag or commit) the skill was installed from, if any.</summary>
    public string? Ref { get; init; }

    /// <summary>Repository-relative path of the SKILL.md, when recorded.</summary>
    public string? SkillPath { get; init; }

    /// <summary>
    /// Content hash recorded at install time: the Git tree SHA (global GitHub installs), a SHA-256
    /// folder hash, the well-known digest, Notion's version id, or null. Skills have no version
    /// numbers; this is the closest equivalent.
    /// </summary>
    public string? Hash { get; init; }

    /// <summary>Plugin the skill belongs to, when the source groups skills into plugins.</summary>
    public string? PluginName { get; init; }

    /// <summary>
    /// The ref the skill is pinned to (<c>skills add --pin</c>), or null. Pinned skills are not checked for updates.
    /// </summary>
    public string? PinnedRef { get; init; }

    /// <summary>First install time (global lock only).</summary>
    public DateTimeOffset? InstalledAt { get; init; }

    /// <summary>Last install or update time (global lock only).</summary>
    public DateTimeOffset? UpdatedAt { get; init; }

    /// <summary>True when a lock file tracks this skill, so it can be checked for updates.</summary>
    public bool IsTracked => Source != null;
}

/// <summary>A skill offered by a source (see <see cref="SkillsManager.GetAvailableSkills"/>).</summary>
/// <param name="Name">Skill name, as passed in <see cref="SkillInstallRequest.Skills"/>.</param>
/// <param name="Description">Skill description.</param>
/// <param name="PluginName">Plugin the skill belongs to, if the source groups skills.</param>
public sealed record AvailableSkill(string Name, string Description, string? PluginName);

/// <summary>What to install.</summary>
public sealed class SkillInstallRequest
{
    /// <summary>
    /// Where to install from, in any form <c>skills add</c> accepts: <c>owner/repo</c>, <c>owner/repo@skill</c>,
    /// a GitHub or GitLab URL (optionally with a tree path or <c>#ref</c>), a Git URL, a local path,
    /// a website publishing <c>/.well-known/skills</c>, or a direct SKILL.md / archive URL. Also the address
    /// of a Notion skills database or skill page, read through the Notion CLI (see <see cref="SkillsManager.GetNotionStatus"/>).
    /// </summary>
    public required string Source { get; init; }

    /// <summary>Skill names to install; null, empty or <c>*</c> installs every skill in the source.</summary>
    public IReadOnlyList<string>? Skills { get; init; }

    /// <summary>
    /// Agent ids to install for; <c>*</c> means every agent. When null, installs for the detected agents plus the
    /// universal agents (those reading <c>.agents/skills</c>), skipping agents without a directory for the scope.
    /// </summary>
    public IReadOnlyList<string>? Agents { get; init; }

    /// <summary>Install scope. Defaults to <see cref="SkillScope.Global"/>.</summary>
    public SkillScope Scope { get; init; } = SkillScope.Global;

    /// <summary>
    /// Install mode. Defaults to <see cref="SkillInstallMode.Symlink"/>; when every target shares one directory,
    /// the skill is copied there directly.
    /// </summary>
    public SkillInstallMode Mode { get; init; } = SkillInstallMode.Symlink;

    /// <summary>Search every subdirectory for skills, even when the source root has a SKILL.md.</summary>
    public bool FullDepth { get; init; }

    /// <summary>Eve subagents to install into (<c>root</c> for the root agent). Adds the <c>eve</c> agent when set. Project scope only.</summary>
    public IReadOnlyList<string>? EveSubagents { get; init; }
}

/// <summary>Result of <see cref="SkillsManager.Install"/>.</summary>
/// <param name="Skills">One outcome per selected (or requested but missing) skill.</param>
public sealed record SkillInstallResult(IReadOnlyList<SkillInstallOutcome> Skills)
{
    /// <summary>True when every skill installed.</summary>
    public bool Succeeded => Skills.Count > 0 && Skills.All(s => s.Status == SkillOperationStatus.Succeeded);
}

/// <summary>Outcome of installing one skill.</summary>
public sealed record SkillInstallOutcome
{
    /// <summary>Skill name.</summary>
    public required string Name { get; init; }

    /// <summary>Whether the skill installed for every target agent.</summary>
    public required SkillOperationStatus Status { get; init; }

    /// <summary>First error, when <see cref="Status"/> is not <see cref="SkillOperationStatus.Succeeded"/>.</summary>
    public string? Error { get; init; }

    /// <summary>Canonical install directory (or the first agent directory for copies).</summary>
    public string? Path { get; init; }

    /// <summary>Install scope.</summary>
    public SkillScope Scope { get; init; }

    /// <summary>Ids of the agents the skill was installed or linked for.</summary>
    public IReadOnlyList<string> Agents { get; init; } = [];

    /// <summary>Ids of the agents skipped because their project directory does not exist.</summary>
    public IReadOnlyList<string> SkippedAgents { get; init; } = [];

    /// <summary>Install mode actually used.</summary>
    public SkillInstallMode Mode { get; init; }

    /// <summary>Content hash of the installed skill, when computed.</summary>
    public string? Hash { get; init; }
}

/// <summary>Result of <see cref="SkillsManager.Remove"/>.</summary>
/// <param name="Skills">One outcome per requested skill.</param>
public sealed record SkillRemoveResult(IReadOnlyList<SkillRemoveOutcome> Skills)
{
    /// <summary>True when every requested skill was removed.</summary>
    public bool Succeeded => Skills.Count > 0 && Skills.All(s => s.Status == SkillOperationStatus.Succeeded);
}

/// <summary>Outcome of removing one skill.</summary>
/// <param name="Name">Skill name (as requested, or as installed when resolved).</param>
/// <param name="Status">Outcome.</param>
/// <param name="Error">Error message on failure.</param>
public sealed record SkillRemoveOutcome(string Name, SkillOperationStatus Status, string? Error = null);

/// <summary>An installed skill whose source has changed.</summary>
public sealed record SkillUpdate
{
    /// <summary>Skill name (lock-file key).</summary>
    public required string Name { get; init; }

    /// <summary>Scope of the installed skill.</summary>
    public required SkillScope Scope { get; init; }

    /// <summary>Source the skill will be reinstalled from.</summary>
    public required string Source { get; init; }

    /// <summary>Hash recorded at install time (see <see cref="InstalledSkillInfo.Hash"/>).</summary>
    public string? CurrentHash { get; init; }

    /// <summary>Hash of the skill in its source now, when known.</summary>
    public string? LatestHash { get; init; }

    /// <summary>True when the skill moved to another path in its repository.</summary>
    public bool Relocated { get; init; }

    internal string InstallSource { get; init; } = "";

    internal bool FullDepth { get; init; }

    internal bool PinGitHubHost { get; init; }

    internal IReadOnlyList<string>? EveSubagents { get; init; }
}

/// <summary>An installed skill that could not be checked for updates.</summary>
/// <param name="Name">Skill name.</param>
/// <param name="Scope">Scope of the installed skill.</param>
/// <param name="Reason">Why it was not checked (for example <c>Local path</c>, <c>Deleted upstream</c>, <c>Failed to check source</c>).</param>
/// <param name="Source">Source to reinstall from manually, when known.</param>
public sealed record UncheckedSkill(string Name, SkillScope Scope, string Reason, string? Source);

/// <summary>Result of <see cref="SkillsManager.CheckForUpdates"/>.</summary>
/// <param name="Updates">Skills with a newer version in their source.</param>
/// <param name="Unchecked">Skills that could not be checked.</param>
public sealed record SkillUpdateCheckResult(IReadOnlyList<SkillUpdate> Updates, IReadOnlyList<UncheckedSkill> Unchecked);

/// <summary>Result of <c>SkillsManager.Update</c>.</summary>
/// <param name="Skills">One outcome per update.</param>
public sealed record SkillUpdateResult(IReadOnlyList<SkillUpdateOutcome> Skills)
{
    /// <summary>True when every update succeeded.</summary>
    public bool Succeeded => Skills.All(s => s.Status == SkillOperationStatus.Succeeded);
}

/// <summary>Outcome of updating one skill.</summary>
/// <param name="Name">Skill name.</param>
/// <param name="Scope">Scope of the skill.</param>
/// <param name="Status">Outcome.</param>
/// <param name="Error">Error message on failure.</param>
public sealed record SkillUpdateOutcome(string Name, SkillScope Scope, SkillOperationStatus Status, string? Error = null);

/// <summary>Whether the Notion CLI, which Notion sources go through, can be used (see <see cref="SkillsManager.GetNotionStatus"/>).</summary>
public enum NotionCliState
{
    /// <summary>The Notion CLI (<c>ntn</c>) was not found.</summary>
    NotInstalled,

    /// <summary>The Notion CLI is installed but not signed in to a workspace.</summary>
    SignedOut,

    /// <summary>The Notion CLI is signed in to a workspace.</summary>
    SignedIn,

    /// <summary>The Notion CLI is installed, but whether it is signed in could not be checked; see <see cref="NotionStatus.Message"/>.</summary>
    Unknown,
}

/// <summary>The state of the Notion CLI.</summary>
/// <param name="State">Whether it is installed and signed in.</param>
/// <param name="CliVersion">Its version (for example <c>0.23.10</c>), when installed.</param>
/// <param name="WorkspaceName">The workspace it is signed in to, when signed in.</param>
/// <param name="Message">Why the state is <see cref="NotionCliState.Unknown"/>; null otherwise.</param>
public sealed record NotionStatus(NotionCliState State, string? CliVersion, string? WorkspaceName, string? Message);

/// <summary>A Notion sign-in waiting for the user to approve it in a browser (see <see cref="SkillsManager.BeginNotionSignIn"/>).</summary>
/// <param name="Url">The Notion page to open, where the user approves the sign-in.</param>
/// <param name="VerificationCode">The code that page shows; the user checks that the two match.</param>
public sealed record NotionSignIn(Uri Url, string VerificationCode);

/// <summary>Failures a host can act on, as opposed to <see cref="Other"/> ones that it can only report.</summary>
public enum SkillsFailure
{
    /// <summary>Any other failure.</summary>
    Other,

    /// <summary>A Notion source needs the Notion CLI (<c>ntn</c>), which was not found.</summary>
    NotionCliMissing,

    /// <summary>A Notion source needs the Notion CLI to be signed in.</summary>
    NotionSignedOut,

    /// <summary>The Notion page or database does not exist, or is not shared with the workspace the Notion CLI is signed in to.</summary>
    NotionNotFound,
}

/// <summary>A source could not be resolved or installed from (clone, download, parse or discovery failure).</summary>
public sealed class SkillsException : Exception
{
    /// <summary>Creates the exception.</summary>
    public SkillsException(string message) : base(message)
    {
    }

    /// <summary>Creates the exception with its cause.</summary>
    public SkillsException(string message, Exception innerException) : base(message, innerException)
    {
    }

    /// <summary>Creates the exception for a failure the host can act on.</summary>
    public SkillsException(string message, SkillsFailure failure, Exception? innerException = null) : base(message, innerException)
    {
        Failure = failure;
    }

    /// <summary>What went wrong, when the host can act on it (for example by offering to sign in to Notion).</summary>
    public SkillsFailure Failure { get; }
}
