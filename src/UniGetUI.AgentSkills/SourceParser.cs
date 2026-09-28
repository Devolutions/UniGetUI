// Parse git URLs, GitHub shorthand, local paths, well-known and download URLs
// (port of source-parser.ts).

using System.Text.RegularExpressions;

namespace Skills;

internal sealed class SourceParseException(string message) : Exception(message);

internal static partial class SourceParser
{
    [GeneratedRegex(@"^git@[^:]+:([\s\S]+)$")] private static partial Regex Ssh();
    [GeneratedRegex(@"\.git$")] private static partial Regex DotGitEnd();
    [GeneratedRegex(@"^([^/]+)/([^/]+)$")] private static partial Regex OwnerRepo();
    [GeneratedRegex(@"^(?:GB|GT)([\s\S]+)$", RegexOptions.IgnoreCase)] private static partial Regex AzureVersion();
    [GeneratedRegex(@"^[a-zA-Z]:[/\\]")] private static partial Regex WinDrive();
    [GeneratedRegex(@"^ssh://[\s\S]+\.git(?:$|[/?])", RegexOptions.IgnoreCase)] private static partial Regex SshGit();
    [GeneratedRegex(@"^/[^/]+/[^/]+(?:\.git)?(?:/tree/[^/]+(?:/[\s\S]*)?)?/?$")] private static partial Regex GhRepoTreePath();
    [GeneratedRegex(@"^/[\s\S]+?/[^/]+(?:\.git)?(?:/-/tree/[^/]+(?:/[\s\S]*)?)?/?$")] private static partial Regex GlRepoTreePath();
    [GeneratedRegex(@"^https?://[\s\S]+\.git(?:$|[/?])", RegexOptions.IgnoreCase)] private static partial Regex HttpGit();
    [GeneratedRegex(@"^([^/]+)/([^/]+)(?:/([\s\S]+)|@([\s\S]+))?$")] private static partial Regex ShorthandLike();
    [GeneratedRegex(@"^/[^/]+/[^/]+/(?:archive/|raw/|releases/(?:download/|latest/download/))")] private static partial Regex GhArtifact();
    [GeneratedRegex(@"/-/(?:archive|raw)/")] private static partial Regex GlArtifact();
    [GeneratedRegex(@"^github:([\s\S]+)$")] private static partial Regex GithubPrefix();
    [GeneratedRegex(@"^gitlab:([\s\S]+)$")] private static partial Regex GitlabPrefix();
    [GeneratedRegex(@"^https?://")] private static partial Regex HttpPrefix();
    [GeneratedRegex(@"github\.com/([^/]+)/([^/]+)/tree/([^/]+)/([\s\S]+)")] private static partial Regex GhTreeWithPath();
    [GeneratedRegex(@"github\.com/([^/]+)/([^/]+)/tree/([^/]+)$")] private static partial Regex GhTree();
    [GeneratedRegex(@"github\.com/([^/]+)/([^/]+)")] private static partial Regex GhRepo();
    [GeneratedRegex(@"^(https?)://([^/]+)/([\s\S]+?)/-/tree/([^/]+)/([\s\S]+)")] private static partial Regex GlTreeWithPath();
    [GeneratedRegex(@"^(https?)://([^/]+)/([\s\S]+?)/-/tree/([^/]+)$")] private static partial Regex GlTree();
    [GeneratedRegex(@"gitlab\.com/([\s\S]+?)(?:\.git)?/?$")] private static partial Regex GlRepo();
    [GeneratedRegex(@"^([^/]+)/([^/@]+)@([\s\S]+)$")] private static partial Regex AtSkill();
    [GeneratedRegex(@"^([^/]+)/([^/]+)(?:/([\s\S]+?))?/?$")] private static partial Regex Shorthand();

    private static string StripDotGit(string s) => DotGitEnd().Replace(s, "", 1);

    /// owner/repo (or group/subgroup/repo) for lockfile tracking and telemetry;
    /// null for local paths, downloads or unparseable sources.
    public static string? GetOwnerRepo(ParsedSource parsed)
    {
        if (parsed.Kind is "local" or "download") return null;
        var ssh = Ssh().Match(parsed.Url);
        if (ssh.Success)
        {
            var path = StripDotGit(ssh.Groups[1].Value);
            return path.Contains('/') ? path : null;
        }
        if (parsed.Url.StartsWith("ssh://"))
        {
            var u = WebUrl.Parse(parsed.Url);
            if (u == null) return null;
            var path = StripDotGit(u.Pathname.Length > 0 ? u.Pathname[1..] : "");
            return path.Contains('/') ? path : null;
        }
        if (!parsed.Url.StartsWith("http://") && !parsed.Url.StartsWith("https://")) return null;
        var url = WebUrl.Parse(parsed.Url);
        if (url == null) return null;
        var p = StripDotGit(url.Pathname.Length > 0 ? url.Pathname[1..] : "");
        return p.Contains('/') ? p : null;
    }

    public static (string Owner, string Repo)? ParseOwnerRepo(string ownerRepo)
    {
        var m = OwnerRepo().Match(ownerRepo);
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : null;
    }

    /// true private, false public, null when it cannot be determined.
    public static bool? IsRepoPrivate(string owner, string repo)
    {
        var res = HttpRequest.Get($"https://api.github.com/repos/{owner}/{repo}").Timeout(TimeSpan.FromSeconds(30)).TrySend();
        if (res == null || !res.Ok) return null;
        if (!res.TryJson(out var data)) return null;
        return Json.AsBool(Json.Get(data, "private")) == true;
    }

    /// Reject subpaths containing `..` segments.
    public static string SanitizeSubpath(string subpath)
    {
        if (subpath.Replace('\\', '/').Split('/').Any(s => s == ".."))
            throw new SourceParseException($"Unsafe subpath: \"{subpath}\" contains path traversal segments. Subpaths must not contain \"..\" components.");
        return subpath;
    }

    private static string? AzureVersionToRef(string? version)
    {
        if (string.IsNullOrEmpty(version)) return null;
        var m = AzureVersion().Match(version);
        return m.Success && m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : null;
    }

    private static ParsedSource? ParseAzureRepos(string input, string? fragmentRef, string? fragmentSkillFilter)
    {
        if (!input.StartsWith("http://") && !input.StartsWith("https://")) return null;
        var parsed = WebUrl.Parse(input);
        if (parsed == null) return null;
        var segments = parsed.Pathname.Split('/').Where(s => s.Length > 0).ToList();
        var gitIndex = segments.IndexOf("_git");
        if (gitIndex < 0 || gitIndex == segments.Count - 1) return null;
        var repo = StripDotGit(segments[gitIndex + 1]);
        if (repo.Length == 0) return null;
        var parts = segments.Take(gitIndex).Append("_git").Append(repo)
            .Select(seg => UrlUtil.DecodeUriComponent(seg) is { } d ? UrlUtil.EncodeUriComponent(d) : UrlUtil.EncodeUriComponent(seg));
        var cloneUrl = $"{parsed.Protocol}//{parsed.Host}/{string.Join("/", parts)}";
        var r = AzureVersionToRef(parsed.SearchParam("version")) ?? (string.IsNullOrEmpty(fragmentRef) ? null : fragmentRef);
        string? subpath = null;
        var pathParam = parsed.SearchParam("path");
        if (!string.IsNullOrEmpty(pathParam))
        {
            var trimmed = SanitizeSubpath(pathParam.TrimStart('/'));
            subpath = trimmed.Length > 0 ? trimmed : null;
        }
        return new ParsedSource("git", cloneUrl)
        {
            Ref = r,
            Subpath = subpath,
            SkillFilter = string.IsNullOrEmpty(fragmentSkillFilter) ? null : fragmentSkillFilter,
        };
    }

    private static bool IsLocalPath(string input) =>
        NodePath.IsAbsolute(input) || input.StartsWith("./") || input.StartsWith("../") || input is "." or ".." || WinDrive().IsMatch(input);

    private static string? SourceAlias(string input) => input switch
    {
        "coinbase/agentWallet" => "coinbase/agentic-wallet-skills",
        "vercel-labs/vercel-skills" => "vercel-labs/agent-skills",
        _ => null,
    };

    private static string DecodeFragmentValue(string v) => UrlUtil.DecodeUriComponent(v) ?? v;

    private static bool LooksLikeGitSource(string input)
    {
        if (input.StartsWith("github:") || input.StartsWith("gitlab:") || input.StartsWith("git@")) return true;
        if (SshGit().IsMatch(input)) return true;
        if (input.StartsWith("http://") || input.StartsWith("https://"))
        {
            var parsed = WebUrl.Parse(input);
            if (parsed != null)
            {
                if (GitHubHost.IsGitHubHost(parsed.Host)) return GhRepoTreePath().IsMatch(parsed.Pathname);
                if (parsed.Hostname == "gitlab.com") return GlRepoTreePath().IsMatch(parsed.Pathname);
                var segs = parsed.Pathname.Split('/').Where(s => s.Length > 0).ToList();
                var i = segs.IndexOf("_git");
                if (i >= 0 && i < segs.Count - 1) return true;
            }
        }
        if (HttpGit().IsMatch(input)) return true;
        return !input.Contains(':') && !input.StartsWith('.') && !input.StartsWith('/') && ShorthandLike().IsMatch(input);
    }

    private static (string Input, string? Ref, string? SkillFilter) ParseFragmentRef(string input)
    {
        var hash = input.IndexOf('#');
        if (hash < 0) return (input, null, null);
        var without = input[..hash];
        var fragment = input[(hash + 1)..];
        if (fragment.Length == 0 || !LooksLikeGitSource(without)) return (input, null, null);
        var at = fragment.IndexOf('@');
        if (at < 0) return (without, DecodeFragmentValue(fragment), null);
        var r = fragment[..at];
        var sf = fragment[(at + 1)..];
        return (without, r.Length > 0 ? DecodeFragmentValue(r) : null, sf.Length > 0 ? DecodeFragmentValue(sf) : null);
    }

    private static string AppendFragmentRef(string input, string? r, string? skillFilter)
    {
        if (string.IsNullOrEmpty(r)) return input;
        return $"{input}#{r}{(string.IsNullOrEmpty(skillFilter) ? "" : "@" + skillFilter)}";
    }

    private static bool IsHostedArtifactUrl(string input)
    {
        var parsed = WebUrl.Parse(input);
        if (parsed == null) return false;
        var host = parsed.Hostname.ToLowerInvariant();
        if (host is "raw.githubusercontent.com" or "codeload.github.com" or "objects.githubusercontent.com") return true;
        if (host == "github.com") return GhArtifact().IsMatch(parsed.Pathname);
        if (host == "gitlab.com") return GlArtifact().IsMatch(parsed.Pathname);
        return false;
    }

    private static bool IsWellKnownUrl(string input)
    {
        if (!input.StartsWith("http://") && !input.StartsWith("https://")) return false;
        var parsed = WebUrl.Parse(input);
        if (parsed == null) return false;
        if (parsed.Hostname is "github.com" or "gitlab.com" or "raw.githubusercontent.com") return false;
        return !input.EndsWith(".git");
    }

    private static string? Opt(string s) => s.Length == 0 ? null : s;

    private static string? OptSubpath(string s) => s.Length == 0 ? null : SanitizeSubpath(s);

    /// Parse a source string into a structured form.
    /// <exception cref="SourceParseException">Unsafe subpath.</exception>
    public static ParsedSource Parse(string input)
    {
        if (IsLocalPath(input))
        {
            var resolved = NodePath.Resolve(input);
            return new ParsedSource("local", resolved) { LocalPath = resolved };
        }

        var (withoutFragment, fragmentRef, fragmentSkillFilter) = ParseFragmentRef(input);
        input = withoutFragment;
        if (SourceAlias(input) is { } alias) input = alias;

        var gh = GithubPrefix().Match(input);
        if (gh.Success) return Parse(AppendFragmentRef(gh.Groups[1].Value, fragmentRef, fragmentSkillFilter));
        var gl = GitlabPrefix().Match(input);
        if (gl.Success) return Parse(AppendFragmentRef($"https://gitlab.com/{gl.Groups[1].Value}", fragmentRef, fragmentSkillFilter));

        if (IsHostedArtifactUrl(input)) return new ParsedSource("download", input);

        // Explicit GitHub Enterprise URL → generic git handling.
        if (GitHubHost.Get() != "github.com" && HttpPrefix().IsMatch(input))
        {
            var parsedUrl = WebUrl.Parse(input);
            if (parsedUrl != null && GitHubHost.IsGitHubHost(parsedUrl.Host) && parsedUrl.Host != "github.com")
            {
                var segments = parsedUrl.Pathname.Split('/').Where(s => s.Length > 0).ToList();
                if (segments.Count >= 2)
                {
                    var repo = StripDotGit(segments[1]);
                    var isTree = segments.Count > 3 && segments[2] == "tree";
                    var result = new ParsedSource("git", $"{parsedUrl.Protocol}//{parsedUrl.Host}/{segments[0]}/{repo}.git");
                    if (isTree)
                    {
                        result.Ref = segments[3];
                        if (segments.Count > 4) result.Subpath = SanitizeSubpath(string.Join("/", segments.Skip(4)));
                    }
                    else if (fragmentRef != null)
                    {
                        result.Ref = fragmentRef;
                    }
                    return result;
                }
            }
        }

        var m = GhTreeWithPath().Match(input);
        if (m.Success)
            return new ParsedSource("github", $"https://github.com/{m.Groups[1].Value}/{m.Groups[2].Value}.git")
            {
                Ref = Opt(m.Groups[3].Value) ?? fragmentRef,
                Subpath = OptSubpath(m.Groups[4].Value),
            };

        m = GhTree().Match(input);
        if (m.Success)
            return new ParsedSource("github", $"https://github.com/{m.Groups[1].Value}/{m.Groups[2].Value}.git") { Ref = Opt(m.Groups[3].Value) ?? fragmentRef };

        m = GhRepo().Match(input);
        if (m.Success)
            return new ParsedSource("github", $"https://github.com/{m.Groups[1].Value}/{StripDotGit(m.Groups[2].Value)}.git") { Ref = fragmentRef };

        m = GlTreeWithPath().Match(input);
        if (m.Success && m.Groups[2].Value != "github.com" && m.Groups[3].Value.Length > 0)
            return new ParsedSource("gitlab", $"{m.Groups[1].Value}://{m.Groups[2].Value}/{StripDotGit(m.Groups[3].Value)}.git")
            {
                Ref = Opt(m.Groups[4].Value) ?? fragmentRef,
                Subpath = OptSubpath(m.Groups[5].Value),
            };

        m = GlTree().Match(input);
        if (m.Success && m.Groups[2].Value != "github.com" && m.Groups[3].Value.Length > 0)
            return new ParsedSource("gitlab", $"{m.Groups[1].Value}://{m.Groups[2].Value}/{StripDotGit(m.Groups[3].Value)}.git") { Ref = Opt(m.Groups[4].Value) ?? fragmentRef };

        m = GlRepo().Match(input);
        if (m.Success && m.Groups[1].Value.Contains('/'))
            return new ParsedSource("gitlab", $"https://gitlab.com/{m.Groups[1].Value}.git") { Ref = fragmentRef };

        if (ParseAzureRepos(input, fragmentRef, fragmentSkillFilter) is { } azure) return azure;

        var githubHost = GitHubHost.Get();
        var shorthandType = githubHost == "github.com" ? "github" : "git";
        var plain = !input.Contains(':') && !input.StartsWith('.') && !input.StartsWith('/');

        m = AtSkill().Match(input);
        if (m.Success && plain)
            return new ParsedSource(shorthandType, $"https://{githubHost}/{m.Groups[1].Value}/{m.Groups[2].Value}.git")
            {
                Ref = fragmentRef,
                SkillFilter = fragmentSkillFilter ?? Opt(m.Groups[3].Value),
            };

        m = Shorthand().Match(input);
        if (m.Success && plain)
            return new ParsedSource(shorthandType, $"https://{githubHost}/{m.Groups[1].Value}/{m.Groups[2].Value}.git")
            {
                Ref = fragmentRef,
                Subpath = m.Groups[3].Success ? OptSubpath(m.Groups[3].Value) : null,
                SkillFilter = fragmentSkillFilter,
            };

        if (IsWellKnownUrl(input)) return new ParsedSource("well-known", input);

        return new ParsedSource("git", input) { Ref = fragmentRef };
    }
}
