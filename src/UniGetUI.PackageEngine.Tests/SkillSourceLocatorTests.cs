using Devolutions.AgentSkills;
using UniGetUI.PackageEngine.Managers.SkillsManager;

namespace UniGetUI.PackageEngine.Tests;

public sealed class SkillSourceLocatorTests
{
    [Theory]
    [InlineData("vercel-labs/agent-skills", "vercel-labs/agent-skills", "https://github.com/vercel-labs/agent-skills")]
    [InlineData("github:vercel-labs/agent-skills", "vercel-labs/agent-skills", "https://github.com/vercel-labs/agent-skills")]
    [InlineData("Anthropics/Skills", "anthropics/skills", "https://github.com/Anthropics/Skills")]
    [InlineData("https://github.com/Anthropics/Skills", "anthropics/skills", "https://github.com/Anthropics/Skills")]
    [InlineData("https://www.github.com/owner/repo/", "owner/repo", "https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/repo.git", "owner/repo", "https://github.com/owner/repo")]
    [InlineData("https://github.com/owner/.github", "owner/.github", "https://github.com/owner/.github")]
    public void GitHubRepositoriesAreNamedAfterTheirOwnerAndRepository(
        string input,
        string expectedName,
        string expectedInstallSource
    )
    {
        var source = SkillSourceLocator.Parse(input);

        Assert.NotNull(source);
        Assert.Equal(SkillSourceKind.Repository, source!.Kind);
        Assert.True(source.IsGitHub);
        Assert.Equal(expectedName, source.Name);
        Assert.Equal(expectedInstallSource, source.InstallSource);
        Assert.Equal("github.com", source.Url.Host);
    }

    [Theory]
    [InlineData("https://github.com/owner/repo/tree/main/skills/review")]
    [InlineData("https://www.github.com/owner/repo/tree/main/skills/review/?tab=readme#top")]
    public void GitHubTreeUrlKeepsTheFolderToInstallButTheRepositoryAsTheSource(string url)
    {
        var source = SkillSourceLocator.Parse(url)!;

        Assert.Equal("owner/repo", source.Name);
        Assert.True(source.IsGitHub);
        Assert.Equal("https://github.com/owner/repo/tree/main/skills/review", source.InstallSource);
    }

    [Fact]
    public void SshGitHubUrlKeepsTheSshAddressToClone()
    {
        var source = SkillSourceLocator.Parse("git@github.com:owner/repo.git")!;

        Assert.Equal(SkillSourceKind.Repository, source.Kind);
        Assert.Equal("owner/repo", source.Name);
        Assert.Equal("git@github.com:owner/repo.git", source.InstallSource);
    }

    [Theory]
    [InlineData("https://gitlab.com/group/sub/repo", "gitlab.com/group/sub/repo")]
    [InlineData("https://gitlab.com/group/repo/-/tree/main/skills", "gitlab.com/group/repo")]
    [InlineData("https://git.example.com/team/repo.git", "git.example.com/team/repo")]
    [InlineData("https://dev.azure.com/org/project/_git/repo", "dev.azure.com/org/project/_git/repo")]
    [InlineData("ssh://git@git.example.com:2222/team/repo.git", "git.example.com/team/repo")]
    public void OtherRepositoriesAreNamedAfterTheirHostAndPath(string input, string expectedName)
    {
        var source = SkillSourceLocator.Parse(input)!;

        Assert.Equal(SkillSourceKind.Repository, source.Kind);
        Assert.False(source.IsGitHub);
        Assert.Equal(expectedName, source.Name);
        Assert.Equal(input, source.InstallSource);
    }

    [Theory]
    [InlineData("https://skills.contoso.com", "skills.contoso.com")]
    [InlineData("https://www.contoso.com/team/skills", "contoso.com")]
    public void OtherWebsitesAreWellKnownIndexesNamedAfterTheirHost(string input, string expectedName)
    {
        var source = SkillSourceLocator.Parse(input)!;

        Assert.Equal(SkillSourceKind.Index, source.Kind);
        Assert.Equal(expectedName, source.Name);
        Assert.Equal(input, source.InstallSource);
    }

    [Theory]
    [InlineData("skills.sh")]
    [InlineData("https://skills.sh/")]
    public void SkillsShIsTheCatalog(string input)
    {
        Assert.Same(SkillSourceLocator.Catalog, SkillSourceLocator.Parse(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("./my-skills")]
    [InlineData("C:\\skills")]
    [InlineData("file:///C:/skills")]
    [InlineData("not a source")]
    [InlineData("https://github.com/owner")]
    public void LocalPathsAndUnknownTextAreNotSources(string input)
    {
        Assert.Null(SkillSourceLocator.Parse(input));
    }

    [Theory]
    // Not encrypted: whoever sits on the network could swap the skills
    [InlineData("http://skills.contoso.com")]
    [InlineData("http://git.example.com/team/repo.git")]
    // A user name or password would be kept in the settings and shown in the operation history
    [InlineData("https://user:secret@skills.contoso.com")]
    [InlineData("https://token@github.com/owner/repo")]
    [InlineData("https://user:pat@dev.azure.com/org/project/_git/repo")]
    // Read differently by other parsers
    [InlineData("https://skills.contoso.com\\@evil.example")]
    [InlineData("owner/repo name")]
    [InlineData("https://github.com/owner/\trepo")]
    // GitHub and GitLab pages that are not a repository or a folder of one
    [InlineData("https://github.com/owner/repo/releases/download/v1/skill.zip")]
    [InlineData("https://github.com/owner/repo/blob/main/SKILL.md")]
    [InlineData("https://gitlab.com/group/repo/-/archive/main/repo-main.zip")]
    // Not a GitHub owner or repository name
    [InlineData("owner/..")]
    [InlineData("https://github.com/own%20er/repo")]
    [InlineData("git@github.com:owner/re$po.git")]
    // Hosts, users and paths that git or ssh could take for options
    [InlineData("git@-oProxyCommand=calc:owner/repo")]
    [InlineData("ssh://-oProxyCommand=calc@git.example.com/team/repo")]
    [InlineData("ssh://git@git.example.com/-team/repo")]
    [InlineData("git@git.example.com:team/../repo")]
    public void UnsafeOrAmbiguousAddressesAreNotSources(string input)
    {
        Assert.Null(SkillSourceLocator.Parse(input));
    }

    [Theory]
    [InlineData("http://localhost:8080", "localhost")]
    [InlineData("http://127.0.0.1:5000/skills", "127.0.0.1")]
    public void AddressesOnThisComputerMayUsePlainHttp(string input, string expectedName)
    {
        var source = SkillSourceLocator.Parse(input)!;

        Assert.Equal(SkillSourceKind.Index, source.Kind);
        Assert.Equal(expectedName, source.Name);
    }

    [Theory]
    [InlineData("https://skills.contoso.com", "https://skills.contoso.com/team", true)]
    [InlineData("https://storage.contoso.com/team", "https://storage.contoso.com/team", true)]
    [InlineData("https://storage.contoso.com/team", "https://storage.contoso.com/team/v2", true)]
    [InlineData("https://storage.contoso.com/team", "https://storage.contoso.com/team-b", false)]
    [InlineData("https://storage.contoso.com/team", "https://storage.contoso.com/other", false)]
    [InlineData("https://skills.contoso.com:8443", "https://skills.contoso.com", false)]
    [InlineData("https://github.com/owner/repo/tree/main/approved", "owner/repo", true)]
    [InlineData("owner/repo", "owner/other", false)]
    public void AnAddedSourceCoversItsRepositoryOrTheWebsiteBelowItsAddress(
        string added,
        string other,
        bool covered
    )
    {
        Assert.Equal(covered, SkillSourceLocator.Parse(added)!.Covers(SkillSourceLocator.Parse(other)!));
    }

    [Fact]
    public void InstalledSkillsMapToTheSourceTheirLockEntryRecords()
    {
        var github = new InstalledSkillInfo
        {
            Name = "review",
            Description = "",
            Path = "",
            Scope = SkillScope.Global,
            Agents = [],
            Source = "Owner/Repo",
            SourceType = "github",
            SourceUrl = "https://github.com/Owner/Repo.git",
        };
        // Its URL is the skill file's, which the index may put on any host
        var wellKnown = github with
        {
            Source = "skills.contoso.com",
            SourceType = "well-known",
            SourceUrl = "https://cdn.contoso.net/review/SKILL.md",
        };
        var git = github with
        {
            Source = "team/repo",
            SourceType = "git",
            SourceUrl = "https://git.example.com/team/repo.git",
        };
        var local = github with { Source = "C:\\skills", SourceType = "local", SourceUrl = null };
        var untracked = github with { Source = null, SourceType = null, SourceUrl = null };

        Assert.Equal("owner/repo", SkillSourceLocator.ForInstalled(github)?.Name);
        Assert.Equal("skills.contoso.com", SkillSourceLocator.ForInstalled(wellKnown)?.Name);
        Assert.Equal("git.example.com/team/repo", SkillSourceLocator.ForInstalled(git)?.Name);
        Assert.Null(SkillSourceLocator.ForInstalled(local));
        Assert.Null(SkillSourceLocator.ForInstalled(untracked));
    }

    [Fact]
    public void SearchResultsComeFromTheirGitHubRepository()
    {
        Assert.Equal(
            "vercel-labs/agent-skills",
            SkillSourceLocator
                .ForSearchResult(new SkillSearchResult("deploy", "vercel-labs/agent-skills/deploy", "vercel-labs/agent-skills", 10))
                ?.Name
        );
        Assert.Equal(
            "owner/repo",
            SkillSourceLocator.ForSearchResult(new SkillSearchResult("skill", "owner/repo/skill", "", 1))?.Name
        );
        Assert.Null(
            SkillSourceLocator.ForSearchResult(new SkillSearchResult("skill", "x", "https://example.com/skills", 1))
        );
    }

    [Fact]
    public void ADiscoveredSkillAndTheSameSkillInstalledShareTheirSourceName()
    {
        var discovered = SkillSourceLocator.ForSearchResult(
            new SkillSearchResult("deploy", "vercel-labs/agent-skills/deploy", "vercel-labs/agent-skills", 10)
        );
        var installed = SkillSourceLocator.ForInstalled(
            FakeSkillsBackendSkill("deploy", "Vercel-Labs/agent-skills")
        );

        Assert.Equal(discovered?.Name, installed?.Name);
    }

    private static InstalledSkillInfo FakeSkillsBackendSkill(string name, string source) =>
        Infrastructure.Fakes.FakeSkillsBackend.TrackedSkill(name, source, "0123456789abcdef");
}
