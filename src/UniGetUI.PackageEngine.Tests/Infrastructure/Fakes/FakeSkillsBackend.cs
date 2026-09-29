using Devolutions.AgentSkills;
using UniGetUI.PackageEngine.Managers.SkillsManager;

namespace UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

/// <summary>
/// Stands in for the bundled Agent Skills library in the manager tests, recording
/// what the manager asks of it.
/// </summary>
internal sealed class FakeSkillsBackend : ISkillsBackend
{
    public string Version => "0.0.0-test";

    public List<AgentInfo> Agents { get; } =
    [
        new("claude-code", "Claude Code", ".claude/skills", "/home/test/.claude/skills", false, true),
        new("cursor", "Cursor", ".agents/skills", "/home/test/.cursor/skills", true, false),
    ];

    public List<InstalledSkillInfo> Installed { get; } = [];

    public Dictionary<string, IReadOnlyList<AvailableSkill>> SourceSkills { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public SkillUpdateCheckResult UpdateCheck { get; set; } = new([], []);

    public SkillOperationStatus InstallStatus { get; set; } = SkillOperationStatus.Succeeded;

    public SkillOperationStatus RemoveStatus { get; set; } = SkillOperationStatus.Succeeded;

    public List<string> ListedSources { get; } = [];

    public List<(string Source, string Skill, IReadOnlyList<string>? Agents)> Installs { get; } = [];

    public List<IReadOnlyList<SkillUpdate>> AppliedUpdates { get; } = [];

    /// <summary>The skills each update check was limited to; null for a check of every skill.</summary>
    public List<IReadOnlyList<string>?> CheckedSkills { get; } = [];

    public List<string> Removed { get; } = [];

    public IReadOnlyList<AgentInfo> GetAgents() => Agents;

    public IReadOnlyList<InstalledSkillInfo> GetInstalledSkills(CancellationToken cancellationToken) =>
        Installed;

    public IReadOnlyList<AvailableSkill> GetAvailableSkills(
        string source,
        IProgress<string>? progress,
        CancellationToken cancellationToken
    )
    {
        ListedSources.Add(source);
        if (SourceFailures.TryGetValue(source, out var failure))
            throw failure;

        return SourceSkills.TryGetValue(source, out var skills)
            ? skills
            : throw new SkillsException("No valid skills found.");
    }

    /// <summary>Sources whose listing fails, and how.</summary>
    public Dictionary<string, SkillsException> SourceFailures { get; } = new(StringComparer.OrdinalIgnoreCase);

    public SkillInstallResult Install(
        string source,
        string skill,
        IReadOnlyList<string>? agents,
        IProgress<string>? progress,
        CancellationToken cancellationToken
    )
    {
        Installs.Add((source, skill, agents));
        progress?.Report($"Installing {skill}");
        return new SkillInstallResult(
            [
                new SkillInstallOutcome
                {
                    Name = skill,
                    Status = InstallStatus,
                    Error = InstallStatus is SkillOperationStatus.Succeeded ? null : "The install failed",
                    Agents = ["claude-code"],
                },
            ]
        );
    }

    public SkillUpdateCheckResult CheckForUpdates(
        IReadOnlyList<string>? skills,
        IProgress<string>? progress,
        CancellationToken cancellationToken
    )
    {
        CheckedSkills.Add(skills);
        return skills is null
            ? UpdateCheck
            : new SkillUpdateCheckResult(
                UpdateCheck.Updates.Where(update => skills.Contains(update.Name)).ToList(),
                UpdateCheck.Unchecked.Where(skill => skills.Contains(skill.Name)).ToList()
            );
    }

    public SkillUpdateResult Update(
        IReadOnlyList<SkillUpdate> updates,
        IProgress<string>? progress,
        CancellationToken cancellationToken
    )
    {
        AppliedUpdates.Add(updates);
        return new SkillUpdateResult(
            updates
                .Select(update => new SkillUpdateOutcome(update.Name, SkillScope.Global, SkillOperationStatus.Succeeded))
                .ToList()
        );
    }

    public SkillRemoveResult Remove(
        string skill,
        IProgress<string>? progress,
        CancellationToken cancellationToken
    )
    {
        Removed.Add(skill);
        return new SkillRemoveResult([new SkillRemoveOutcome(skill, RemoveStatus)]);
    }

    public NotionStatus NotionStatus { get; set; } = new(NotionCliState.SignedIn, "0.23.10", "Contoso", null);

    public int NotionSignIns { get; private set; }

    public NotionStatus GetNotionStatus(CancellationToken cancellationToken) => NotionStatus;

    public NotionSignIn BeginNotionSignIn(CancellationToken cancellationToken) =>
        new(new Uri("https://app.notion.com/workers/cli-login?verificationCode=K7Q-2MX"), "K7Q-2MX");

    public void CompleteNotionSignIn(CancellationToken cancellationToken)
    {
        NotionSignIns++;
        NotionStatus = new(NotionCliState.SignedIn, "0.23.10", "Contoso", null);
    }

    public void SignOutOfNotion(CancellationToken cancellationToken) =>
        NotionStatus = new(NotionCliState.SignedOut, "0.23.10", null, null);

    public static InstalledSkillInfo TrackedSkill(string name, string source, string hash) =>
        new()
        {
            Name = name,
            Description = $"The {name} skill",
            Path = $"/home/test/.agents/skills/{name}",
            Scope = SkillScope.Global,
            Agents = ["claude-code"],
            Source = source,
            SourceType = "github",
            SourceUrl = $"https://github.com/{source}.git",
            Hash = hash,
        };
}
