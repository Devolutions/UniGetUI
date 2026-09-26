using Devolutions.AgentSkills;
using UniGetUI.Core.SecureSettings;
using AgentSkillsClient = Devolutions.AgentSkills.SkillsManager;

namespace UniGetUI.PackageEngine.Managers.SkillsManager;

/// <summary>
/// <see cref="ISkillsBackend"/> over the Devolutions.AgentSkills library, which installs skills in
/// process, with the same lock files and folders as the skills CLI (npx skills) and gh skill.
/// </summary>
/// <param name="homeDirectory">Home directory override, for tests; the user's home when null</param>
internal sealed class AgentSkillsBackend(string? homeDirectory = null) : ISkillsBackend
{
    public string Version => AgentSkillsClient.Version;

    // A client takes its options when it is created, and creating one is cheap, so each call gets
    // one built with the current GitHub sign-in.
    private AgentSkillsClient CreateClient(bool useGitHubToken = false) => new(CreateOptions(useGitHubToken));

    /// <summary>
    /// The library's options. Its own telemetry, which reports installs to skills.sh, stays off:
    /// skill operations are reported through UniGetUI's telemetry, to Devolutions only, like those of
    /// every other manager.
    /// </summary>
    internal SkillsManagerOptions CreateOptions(bool useGitHubToken) =>
        new()
        {
            HomeDirectory = homeDirectory,
            GitHubToken = useGitHubToken ? GetGitHubToken() : null,
            EnableTelemetry = false,
        };

    /// <summary>
    /// The token of the GitHub account signed in to UniGetUI (for cloud backups), which lifts
    /// GitHub's anonymous rate limit of 60 API requests per hour. It is never sent to a GitHub
    /// Enterprise host selected with GH_HOST. Without it, the library falls back to the
    /// GITHUB_TOKEN and GH_TOKEN environment variables.
    /// </summary>
    private static string? GetGitHubToken()
    {
        string? host = Environment.GetEnvironmentVariable("GH_HOST")?.Trim();
        if (!string.IsNullOrEmpty(host) && !host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return null;

        return SecureGHTokenManager.GetToken();
    }

    public IReadOnlyList<AgentInfo> GetAgents() => CreateClient().GetAgents();

    public IReadOnlyList<InstalledSkillInfo> GetInstalledSkills(CancellationToken cancellationToken) =>
        CreateClient().GetInstalledSkills(SkillScope.Global, cancellationToken);

    public IReadOnlyList<SkillSearchResult> Search(
        string query,
        int limit,
        CancellationToken cancellationToken
    ) => CreateClient().Search(query, limit: limit, cancellationToken: cancellationToken);

    public IReadOnlyList<AvailableSkill> GetAvailableSkills(
        string source,
        IProgress<string>? progress,
        CancellationToken cancellationToken
    ) => CreateClient(useGitHubToken: true).GetAvailableSkills(source, false, progress, cancellationToken);

    public SkillInstallResult Install(
        string source,
        string skill,
        IReadOnlyList<string>? agents,
        IProgress<string>? progress,
        CancellationToken cancellationToken
    ) =>
        CreateClient(useGitHubToken: true)
            .Install(
                new SkillInstallRequest
                {
                    Source = source,
                    Skills = [skill],
                    Agents = agents,
                    Scope = SkillScope.Global,
                },
                progress,
                cancellationToken
            );

    public SkillUpdateCheckResult CheckForUpdates(
        IReadOnlyList<string>? skills,
        IProgress<string>? progress,
        CancellationToken cancellationToken
    ) =>
        CreateClient(useGitHubToken: true)
            .CheckForUpdates(SkillScope.Global, skills, progress, cancellationToken);

    public SkillUpdateResult Update(
        IReadOnlyList<SkillUpdate> updates,
        IProgress<string>? progress,
        CancellationToken cancellationToken
    ) => CreateClient(useGitHubToken: true).Update(updates, progress, cancellationToken);

    public SkillRemoveResult Remove(
        string skill,
        IProgress<string>? progress,
        CancellationToken cancellationToken
    ) => CreateClient().Remove([skill], SkillScope.Global, null, progress, cancellationToken);
}
