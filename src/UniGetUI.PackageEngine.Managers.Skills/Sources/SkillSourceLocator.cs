using System.Text.RegularExpressions;
using Devolutions.AgentSkills;

namespace UniGetUI.PackageEngine.Managers.SkillsManager;

internal enum SkillSourceKind
{
    /// <summary>The skills.sh catalog, searched through its API.</summary>
    Catalog,

    /// <summary>A website that publishes a well-known skills index (RFC 8615).</summary>
    Index,

    /// <summary>A git repository: GitHub, GitLab, Azure DevOps or any git URL.</summary>
    Repository,
}

/// <summary>
/// Where skills come from, parsed from what a user typed, a search result or a lock-file entry.
/// The library parses sources again with rules of its own, so it is handed the address as parsed
/// here, and anything the two could read differently is refused.
/// </summary>
/// <param name="Kind">The kind of source</param>
/// <param name="Name">
/// The source name UniGetUI shows and compares: owner/repo for a GitHub repository, host and path
/// for other repositories, the host for an index, and skills.sh for the catalog. It matches what the
/// skills lock file records, so a skill found in a source and the same skill once installed get the
/// same source.
/// </param>
/// <param name="InstallSource">What the library installs from</param>
/// <param name="Url">A browsable address for the source</param>
internal sealed partial record SkillSourceLocator(
    SkillSourceKind Kind,
    string Name,
    string InstallSource,
    Uri Url
)
{
    public static readonly SkillSourceLocator Catalog = new(
        SkillSourceKind.Catalog,
        "skills.sh",
        "https://skills.sh",
        new Uri("https://skills.sh")
    );

    [GeneratedRegex(@"^(?:github:)?([A-Za-z0-9][A-Za-z0-9-]*)/([A-Za-z0-9._-]+)$")]
    private static partial Regex GitHubShorthand();

    // Hosts, user names and paths are limited to characters that git and ssh cannot take for options
    [GeneratedRegex(
        @"^(?:git@([A-Za-z0-9][A-Za-z0-9.-]*):|ssh://(?:[A-Za-z0-9_][A-Za-z0-9._-]*@)?([A-Za-z0-9][A-Za-z0-9.-]*)(?::\d+)?/)([A-Za-z0-9_~][A-Za-z0-9._~/-]*)$"
    )]
    private static partial Regex SshGitUrl();

    public bool IsGitHub =>
        Kind is SkillSourceKind.Repository
        && Url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>The owner of a GitHub repository; null for any other source.</summary>
    public string? GitHubOwner => IsGitHub ? Name.Split('/')[0] : null;

    /// <summary>
    /// A GitHub repository. It is installed from its github.com address rather than as owner/repo,
    /// which the library would resolve against a GitHub Enterprise host selected with GH_HOST.
    /// </summary>
    public static SkillSourceLocator GitHub(string owner, string repository)
    {
        repository = StripDotGit(repository);
        var url = new Uri($"https://github.com/{owner}/{repository}");
        return new(SkillSourceKind.Repository, $"{owner}/{repository}".ToLowerInvariant(), url.AbsoluteUri, url);
    }

    /// <summary>
    /// Parses a source as the skills CLI accepts it: owner/repo, a GitHub, GitLab, Azure DevOps or
    /// git URL, or the address of a website that publishes a well-known skills index. Returns null
    /// for anything else, including local paths, which UniGetUI does not offer as sources, web
    /// addresses that are not encrypted or that carry a user name or password, and GitHub addresses
    /// other than a repository or a folder of one.
    /// </summary>
    public static SkillSourceLocator? Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        input = input.Trim();
        if (input.Any(c => c == '\\' || char.IsWhiteSpace(c) || char.IsControl(c)))
            return null;

        if (input.Equals("skills.sh", StringComparison.OrdinalIgnoreCase))
            return Catalog;

        var shorthand = GitHubShorthand().Match(input);
        if (shorthand.Success)
            return TryGitHub(shorthand.Groups[1].Value, shorthand.Groups[2].Value);

        var ssh = SshGitUrl().Match(input);
        if (ssh.Success)
            return ParseSsh(input, ssh);

        if (
            !Uri.TryCreate(input, UriKind.Absolute, out Uri? uri)
            || !IsEncryptedOrLocal(uri)
            || uri.UserInfo.Length > 0
        )
            return null;

        string host = uri.Host.ToLowerInvariant();
        string[] segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (host is "skills.sh" or "www.skills.sh")
            return Catalog;

        if (host is "github.com" or "www.github.com")
            return ParseGitHubUrl(uri, segments);

        bool isRepository =
            host == "gitlab.com"
            || uri.AbsolutePath.TrimEnd('/').EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            || segments.Contains("_git");
        if (isRepository)
        {
            // GitLab tree URLs append /-/tree/<ref>/<path> to the repository path; its other /-/
            // pages, such as archive downloads, are not sources
            int dash = Array.IndexOf(segments, "-");
            if (dash >= 0 && (dash + 1 == segments.Length || segments[dash + 1] != "tree"))
                return null;

            string[] repositoryPath = dash >= 0 ? segments[..dash] : segments;
            if (repositoryPath.Length < 2)
                return null;

            repositoryPath[^1] = StripDotGit(repositoryPath[^1]);
            return new(
                SkillSourceKind.Repository,
                $"{host}/{string.Join('/', repositoryPath)}".ToLowerInvariant(),
                Canonical(uri),
                uri
            );
        }

        // Any other website publishes a well-known skills index. The lock file names such sources
        // after their host.
        return new(
            SkillSourceKind.Index,
            host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host,
            Canonical(uri),
            uri
        );
    }

    /// <summary>
    /// Whether skills from the given source come from this added source: the same repository, or,
    /// for an index, the same website at or below this index's address. The lock file names an index
    /// after its host only, so the address is what keeps an index on a shared host (a cloud storage
    /// bucket, a CDN folder) from vouching for every other index on that host.
    /// </summary>
    public bool Covers(SkillSourceLocator other)
    {
        if (
            Kind != other.Kind
            || IsGitHub != other.IsGitHub
            || !Name.Equals(other.Name, StringComparison.OrdinalIgnoreCase)
        )
            return false;

        if (Kind is not SkillSourceKind.Index)
            return true;

        bool sameWebsite =
            Uri.Compare(
                Url,
                other.Url,
                UriComponents.SchemeAndServer,
                UriFormat.SafeUnescaped,
                StringComparison.OrdinalIgnoreCase
            ) == 0;
        string path = Url.AbsolutePath.TrimEnd('/');
        string otherPath = other.Url.AbsolutePath.TrimEnd('/');
        return sameWebsite
            && (otherPath == path || otherPath.StartsWith(path + "/", StringComparison.Ordinal));
    }

    /// <summary>
    /// The source an installed skill came from, as its lock-file entry records it; null for skills
    /// installed from a local path or a direct download, and for skills no lock file tracks.
    /// </summary>
    public static SkillSourceLocator? ForInstalled(InstalledSkillInfo skill) =>
        skill.SourceType switch
        {
            null or "local" or "download" or "node_modules" => null,
            "github" => Parse(skill.Source) ?? Parse(skill.SourceUrl),
            // The lock file names an index after its host; the URL it records is the skill file's,
            // which the index picks and may put on any host
            "well-known" => string.IsNullOrEmpty(skill.Source) ? null : Parse($"https://{skill.Source}"),
            _ => Parse(skill.SourceUrl) ?? Parse(skill.Source),
        };

    /// <summary>
    /// The GitHub repository a skills.sh search result comes from; null for results from anywhere
    /// else, which the catalog does not vouch for.
    /// </summary>
    public static SkillSourceLocator? ForSearchResult(SkillSearchResult result)
    {
        // Results without a source carry the repository in their id (owner/repo/skill)
        string source =
            result.Source.Length > 0 ? result.Source : string.Join('/', result.Id.Split('/').Take(2));
        return Parse(source) is { IsGitHub: true } locator ? locator : null;
    }

    private static SkillSourceLocator? TryGitHub(string owner, string repository) =>
        GitHubShorthand().IsMatch($"{owner}/{repository}") && StripDotGit(repository).Trim('.').Length > 0
            ? GitHub(owner, repository)
            : null;

    private static SkillSourceLocator? ParseSsh(string input, Match ssh)
    {
        string host = (ssh.Groups[1].Success ? ssh.Groups[1].Value : ssh.Groups[2].Value).ToLowerInvariant();
        string path = StripDotGit(ssh.Groups[3].Value.Trim('/'));
        string[] segments = path.Split('/');
        if (segments.Length < 2 || segments.Any(s => s is "" or "." or ".."))
            return null;

        if (host == "github.com")
        {
            return segments is [var owner, var repository] && TryGitHub(owner, repository) is { } github
                ? github with { InstallSource = input }
                : null;
        }

        return new(
            SkillSourceKind.Repository,
            $"{host}/{path}".ToLowerInvariant(),
            input,
            new Uri($"https://{host}/{path}")
        );
    }

    /// <summary>
    /// A GitHub repository address, or a tree address that selects a folder of it: installed from
    /// that folder, with the repository as the source, as the lock file records it.
    /// </summary>
    private static SkillSourceLocator? ParseGitHubUrl(Uri uri, string[] segments)
    {
        if (segments.Length < 2 || TryGitHub(segments[0], segments[1]) is not { } repository)
            return null;

        if (segments.Length == 2)
            return repository;

        if (segments.Length < 4 || segments[2] != "tree")
            return null;

        var tree = new Uri($"https://github.com{uri.AbsolutePath.TrimEnd('/')}");
        return repository with { InstallSource = tree.AbsoluteUri, Url = tree };
    }

    // Skills are instructions, and sometimes scripts, that coding agents act on, so they are only
    // fetched over an encrypted connection, unless they come from this computer
    private static bool IsEncryptedOrLocal(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback);

    // The address as parsed here, without the trailing slash of a bare website address
    private static string Canonical(Uri uri) =>
        uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0
            ? uri.GetLeftPart(UriPartial.Authority)
            : uri.AbsoluteUri;

    private static string StripDotGit(string value) =>
        value.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
}
