using Devolutions.AgentSkills;

namespace UniGetUI.PackageEngine.Managers.SkillsManager;

/// <summary>
/// The part of the Devolutions.AgentSkills library the manager uses, always in the user (global)
/// scope. Tests replace it with a fake.
/// </summary>
internal interface ISkillsBackend
{
    /// <summary>The library version.</summary>
    string Version { get; }

    /// <summary>Every agent the library can install skills for, and whether it is detected.</summary>
    IReadOnlyList<AgentInfo> GetAgents();

    IReadOnlyList<InstalledSkillInfo> GetInstalledSkills(CancellationToken cancellationToken);

    /// <summary>Searches the skills.sh catalog.</summary>
    IReadOnlyList<SkillSearchResult> Search(string query, int limit, CancellationToken cancellationToken);

    /// <summary>The skills a repository or a well-known index offers.</summary>
    IReadOnlyList<AvailableSkill> GetAvailableSkills(
        string source,
        IProgress<string>? progress,
        CancellationToken cancellationToken
    );

    /// <param name="agents">Agent ids to install for, or null for the detected and universal agents</param>
    SkillInstallResult Install(
        string source,
        string skill,
        IReadOnlyList<string>? agents,
        IProgress<string>? progress,
        CancellationToken cancellationToken
    );

    /// <param name="skills">The skills to check, or null for every installed skill</param>
    SkillUpdateCheckResult CheckForUpdates(
        IReadOnlyList<string>? skills,
        IProgress<string>? progress,
        CancellationToken cancellationToken
    );

    /// <summary>Applies updates found by <see cref="CheckForUpdates"/>.</summary>
    SkillUpdateResult Update(
        IReadOnlyList<SkillUpdate> updates,
        IProgress<string>? progress,
        CancellationToken cancellationToken
    );

    SkillRemoveResult Remove(string skill, IProgress<string>? progress, CancellationToken cancellationToken);
}
