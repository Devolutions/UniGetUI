// Blob-based skill download (port of blob.ts).
//  1. GitHub Trees API → discover SKILL.md locations
//  2. raw.githubusercontent.com → fetch frontmatter to get skill names
//  3. skills.sh/api/download → fetch full file contents from a cached blob

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Skills;

internal sealed record TreeEntry(string Path, string Kind, string Sha);

internal sealed record RepoTree(string Sha, string Branch, List<TreeEntry> Tree);

internal sealed record BlobInstallResult(List<Skill> Skills, RepoTree Tree);

internal sealed record BlobOptions(string? Subpath, string? SkillFilter, string? Ref, bool UseToken, bool IncludeInternal);

internal static partial class Blob
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(10);
    private const int GhApiMaxBuffer = 16 * 1024 * 1024;
    private static volatile bool _rateLimited;

    private static string DownloadBaseUrl => Sys.Env("SKILLS_DOWNLOAD_URL") ?? "https://skills.sh";

    /// Repos that self-host their downloads on the blob fast-path.
    public static string? AllowedRepoDownloadUrl(string ownerRepoLower, string slug) => ownerRepoLower switch
    {
        "zapier/connectors" => $"https://connectors-skills.zapier.com/download/{UrlUtil.EncodeUriComponent(slug)}/snapshot.json",
        _ => null,
    };

    public static bool IsAllowedRepo(string ownerRepoLower) => AllowedRepoDownloadUrl(ownerRepoLower, "") != null;

    [GeneratedRegex(@"[\s_]+")] private static partial Regex WsUnderscore();
    [GeneratedRegex("[^a-z0-9-]")] private static partial Regex NonSlug();
    [GeneratedRegex("-+")] private static partial Regex Dashes();
    [GeneratedRegex("^-|-$")] private static partial Regex EdgeDash();

    /// Must match the server-side toSkillSlug() exactly.
    public static string ToSkillSlug(string name)
    {
        var s = WsUnderscore().Replace(name.ToLowerInvariant(), "-");
        s = NonSlug().Replace(s, "");
        s = Dashes().Replace(s, "-");
        return EdgeDash().Replace(s, "");
    }

    private static RepoTree? ParseTree(JsonNode? data, string branch)
    {
        if (Json.Str(data, "sha") is not { } sha || Json.Get(data, "tree") is not JsonArray arr) return null;
        var tree = arr.Select(e => new TreeEntry(Json.Str(e, "path") ?? "", Json.Str(e, "type") ?? "", Json.Str(e, "sha") ?? "")).ToList();
        return new RepoTree(sha, branch, tree);
    }

    private sealed record BranchResult(RepoTree? Tree, bool RateLimited, bool AuthRetryable);

    /// REST API base for the selected GitHub host (`GH_HOST`).
    public static string GitHubApiBase()
    {
        var host = GitHubHost.Get();
        return host == "github.com" ? "https://api.github.com" : $"https://{host}/api/v3";
    }

    private static BranchResult FetchTreeBranch(string ownerRepo, string branch, string? token)
    {
        var apiBase = GitHubApiBase();
        var req = HttpRequest.Get($"{apiBase}/repos/{ownerRepo}/git/trees/{UrlUtil.EncodeUriComponent(branch)}?recursive=1")
            .Timeout(FetchTimeout).Header("Accept", "application/vnd.github.v3+json").Header("User-Agent", "skills-cli");
        if (token != null) req.Header("Authorization", $"Bearer {token}");
        var resp = req.TrySend();
        if (resp == null) return new BranchResult(null, false, false);
        if (resp.Ok) return new BranchResult(resp.TryJson(out var d) ? ParseTree(d, branch) : null, false, false);
        return new BranchResult(null, resp.Status == 403 && resp.Header("x-ratelimit-remaining") == "0", resp.Status is 401 or 404);
    }

    private static RepoTree? FetchTreeWithToken(string ownerRepo, List<string> branches)
    {
        var token = SkillLock.GetGitHubToken();
        if (token == null) return null;
        foreach (var b in branches)
            if (FetchTreeBranch(ownerRepo, b, token).Tree is { } t) return t;
        return null;
    }

    private static RepoTree? FetchTreeWithGitHubCli(string ownerRepo, List<string> branches)
    {
        foreach (var branch in branches)
        {
            try
            {
                var endpoint = $"repos/{ownerRepo}/git/trees/{UrlUtil.EncodeUriComponent(branch)}?recursive=1";
                var output = Proc.Command("gh", "api", endpoint, "--method", "GET", "--hostname", GitHubHost.Get())
                    .Env("GH_PROMPT_DISABLED", "1").Timeout(FetchTimeout).MaxOutput(GhApiMaxBuffer).HideWindow().Output();
                if (!output.Success) continue;
                if (Json.TryParse(output.StdoutText, out var data) && ParseTree(data, branch) is { } tree) return tree;
            }
            catch (ProcException)
            {
                // try the next candidate branch
            }
        }
        return null;
    }

    private static RepoTree? FetchTreeWithAvailableAuth(string ownerRepo, List<string> branches, bool useToken)
    {
        if (useToken && FetchTreeWithToken(ownerRepo, branches) is { } t) return t;
        return FetchTreeWithGitHubCli(ownerRepo, branches);
    }

    /// Full recursive tree for a GitHub repo. Anonymous first; an explicit token
    /// (when useToken) and `gh api` only after a rate limit or 401/404.
    public static RepoTree? FetchRepoTree(string ownerRepo, string? r, bool useToken)
    {
        var branches = r != null ? new List<string> { r } : ["HEAD", "main", "master"];
        if (_rateLimited) return FetchTreeWithAvailableAuth(ownerRepo, branches, useToken);
        bool rateLimited = false, authRetryable = false;
        foreach (var b in branches)
        {
            var res = FetchTreeBranch(ownerRepo, b, null);
            if (res.Tree != null) return res.Tree;
            if (res.RateLimited)
            {
                rateLimited = true;
                break;
            }
            if (res.AuthRetryable)
            {
                authRetryable = true;
                break;
            }
        }
        if (!(rateLimited || authRetryable)) return null;
        if (rateLimited) _rateLimited = true;
        return FetchTreeWithAvailableAuth(ownerRepo, branches, useToken);
    }

    /// Folder tree SHA for a skill path (SKILL.md suffix optional).
    public static string? GetSkillFolderHashFromTree(RepoTree tree, string skillPath)
    {
        var folder = skillPath.Replace('\\', '/');
        var lower = folder.ToLowerInvariant();
        if (lower.EndsWith("/skill.md")) folder = folder[..^9];
        else if (lower.EndsWith("skill.md")) folder = folder[..^8];
        if (folder.EndsWith('/')) folder = folder[..^1];
        if (folder.Length == 0) return tree.Sha;
        return tree.Tree.FirstOrDefault(e => e.Kind == "tree" && e.Path == folder)?.Sha;
    }

    private static readonly string[] PriorityPrefixes =
    [
        "", "skills/", "skills/.curated/", "skills/.experimental/", "skills/.system/", ".agents/skills/", ".claude/skills/",
        ".cline/skills/", ".codebuddy/skills/", ".codex/skills/", ".commandcode/skills/", ".continue/skills/", ".factory/skills/",
        ".github/skills/", ".goose/skills/", ".grok/skills/", ".iflow/skills/", ".junie/skills/", ".kilo/skills/", ".kilocode/skills/",
        ".kimchi/skills/", ".kiro/skills/", ".minimax/skills/", ".mux/skills/", ".neovate/skills/", ".opencode/skills/",
        ".openhands/skills/", ".pi/skills/", ".posit/assistant/skills/", ".qoder/skills/", ".roo/skills/", ".trae/skills/",
        ".windsurf/skills/", ".zcode/skills/", ".zencoder/skills/",
    ];

    /// SKILL.md paths in a tree, with the same priority-dir logic as discovery.
    public static List<string> FindSkillMdPaths(RepoTree tree, string? subpath)
    {
        var all = tree.Tree.Where(e => e.Kind == "blob" && e.Path.ToLowerInvariant().EndsWith("skill.md")).Select(e => e.Path).ToList();
        var prefix = string.IsNullOrEmpty(subpath) ? "" : subpath.EndsWith('/') ? subpath : subpath + "/";
        var filtered = prefix.Length == 0 ? all : all.Where(p => p.StartsWith(prefix, StringComparison.Ordinal) || p == prefix + "SKILL.md").ToList();
        if (filtered.Count == 0) return [];
        var skip = new HashSet<string>(SkillDiscovery.SkipDirs);
        var lowerSet = filtered.Select(p => p.ToLowerInvariant()).ToHashSet();
        var results = new List<string>();
        var seen = new HashSet<string>();
        foreach (var pp in PriorityPrefixes)
        {
            var full = prefix + pp;
            var isContainer = pp.Length > 0;
            foreach (var md in filtered)
            {
                if (!md.StartsWith(full, StringComparison.Ordinal)) continue;
                var rest = md[full.Length..];
                if (rest.ToLowerInvariant() == "skill.md")
                {
                    if (seen.Add(md)) results.Add(md);
                    continue;
                }
                var parts = rest.Split('/');
                if (parts.Length == 2 && parts[1].ToLowerInvariant() == "skill.md")
                {
                    if (seen.Add(md)) results.Add(md);
                    continue;
                }
                var dirs = parts[..^1];
                var hasAncestor = Enumerable.Range(0, Math.Max(0, dirs.Length - 1))
                    .Any(i => lowerSet.Contains($"{full}{string.Join("/", dirs[..(i + 1)])}/SKILL.md".ToLowerInvariant()));
                if (isContainer && parts.Length >= 3 && parts.Length <= Constants.DefaultSkillContainerDepth + 1
                    && parts[^1].ToLowerInvariant() == "skill.md" && dirs.All(d => !skip.Contains(d)) && !hasAncestor && seen.Add(md))
                    results.Add(md);
            }
        }
        if (results.Count > 0) return results;
        return filtered.Where(p => p.Split('/').Length <= 6).ToList();
    }

    private static string? FetchSkillMdContent(string ownerRepo, string branch, string path)
    {
        var r = HttpRequest.Get($"https://raw.githubusercontent.com/{ownerRepo}/{branch}/{path}").Timeout(FetchTimeout).TrySend();
        return r is { Ok: true } ? r.Text() : null;
    }

    private sealed record Download(List<SnapshotFile> Files, string Hash);

    private static Download? FetchSkillDownload(string source, string slug)
    {
        var parts = source.Split('/');
        var owner = parts[0];
        var repo = parts.Length > 1 ? parts[1] : "undefined";
        var url = AllowedRepoDownloadUrl(source.ToLowerInvariant(), slug)
                  ?? $"{DownloadBaseUrl}/api/download/{UrlUtil.EncodeUriComponent(owner)}/{UrlUtil.EncodeUriComponent(repo)}/{UrlUtil.EncodeUriComponent(slug)}";
        var r = HttpRequest.Get(url).Timeout(FetchTimeout).TrySend();
        if (r is not { Ok: true } || !r.TryJson(out var data) || Json.Get(data, "files") is not JsonArray files) return null;
        return new Download(
            files.Select(f => new SnapshotFile(Json.Str(f, "path") ?? "", Encoding.UTF8.GetBytes(Json.Str(f, "contents") ?? ""))).ToList(),
            Json.Str(data, "hash") ?? "");
    }

    public static string ComputeSnapshotHash(IEnumerable<SnapshotFile> files)
    {
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var f in Collate.StableSort(files, (a, b) => Collate.LocaleCompare(a.Path, b.Path)))
        {
            h.AppendData(Encoding.UTF8.GetBytes(f.Path));
            h.AppendData(f.Contents);
        }
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }

    private static string GetSkillFolderPath(string md)
    {
        var lower = md.ToLowerInvariant();
        if (lower.EndsWith("/skill.md")) return md[..^9];
        if (lower == "skill.md") return "";
        return md[..Math.Max(0, md.Length - 9)];
    }

    private static bool IsInstallableSnapshotPath(string p)
    {
        var parts = p.Split('/');
        var file = parts[^1];
        if (file.Length == 0 || file == "metadata.json") return false;
        return parts[..^1].All(d => d is not (".git" or "__pycache__" or "__pypackages__"));
    }

    private static bool HasCompleteNestedSnapshot(RepoTree tree, string md, List<SnapshotFile> files)
    {
        var folder = GetSkillFolderPath(md);
        if (folder.Length == 0) return true;
        var prefix = folder + "/";
        var paths = files.Select(f => f.Path).ToHashSet();
        return tree.Tree.All(e =>
        {
            if (e.Kind != "blob" || !e.Path.StartsWith(prefix, StringComparison.Ordinal)) return true;
            var rel = e.Path[prefix.Length..];
            return !IsInstallableSnapshotPath(rel) || paths.Contains(rel);
        });
    }

    /// Order-preserving parallel map with bounded concurrency.
    public static List<TR> ParallelMap<T, TR>(IReadOnlyList<T> items, Func<T, TR> f)
    {
        var results = new TR[items.Count];
        Parallel.For(0, items.Count, new ParallelOptions { MaxDegreeOfParallelism = 16 }, i => results[i] = f(items[i]));
        return results.ToList();
    }

    private sealed record ParsedBlobSkill(string MdPath, string Name, string Description, string Content, string Slug, JsonObject? Metadata);

    /// Resolve skills from blob storage instead of cloning. Null means the caller
    /// should fall back to git clone.
    public static BlobInstallResult? TryBlobInstall(string ownerRepo, BlobOptions options)
    {
        // Snapshots are ref-agnostic; an explicit ref must use the clone path.
        if (options.Ref != null) return null;
        var tree = FetchRepoTree(ownerRepo, null, options.UseToken);
        if (tree == null) return null;
        var mdPaths = FindSkillMdPaths(tree, options.Subpath);
        if (mdPaths.Count == 0) return null;
        if (options.SkillFilter != null)
        {
            var slug = ToSkillSlug(options.SkillFilter);
            var byFolder = mdPaths.Where(p =>
            {
                var parts = p.Split('/');
                return parts.Length >= 2 && ToSkillSlug(parts[^2]) == slug;
            }).ToList();
            if (byFolder.Count > 0) mdPaths = byFolder;
        }

        var contents = ParallelMap(mdPaths, p => FetchSkillMdContent(ownerRepo, tree.Branch, p));
        var parsed = new List<ParsedBlobSkill>();
        for (var i = 0; i < mdPaths.Count; i++)
        {
            var content = contents[i];
            if (content == null) continue;
            JsonObject data;
            try
            {
                data = Frontmatter.Parse(content).Data;
            }
            catch (YamlParseException)
            {
                return null;
            }
            if (Json.NonEmpty(data, "name") is not { } name || Json.NonEmpty(data, "description") is not { } desc) continue;
            var internalSkill = Json.AsBool(Json.Get(data["metadata"], "internal")) == true;
            if (internalSkill && !options.IncludeInternal) continue;
            var safeName = Sanitize.Metadata(name);
            parsed.Add(new ParsedBlobSkill(mdPaths[i], safeName, Sanitize.Metadata(desc), content, ToSkillSlug(safeName), data["metadata"] as JsonObject));
        }
        if (parsed.Count == 0) return null;
        if (options.SkillFilter != null)
        {
            var slug = ToSkillSlug(options.SkillFilter);
            var byName = parsed.Where(s => s.Slug == slug).ToList();
            if (byName.Count > 0) parsed = byName;
        }

        var source = ownerRepo.ToLowerInvariant();
        var downloads = ParallelMap(parsed, s => FetchSkillDownload(source, s.Slug));
        if (downloads.Any(d => d == null)) return null;
        if (!parsed.Select((s, i) => HasCompleteNestedSnapshot(tree, s.MdPath, downloads[i]!.Files)).All(x => x)) return null;

        var skills = parsed.Select((s, i) =>
        {
            var d = downloads[i]!;
            var folder = GetSkillFolderPath(s.MdPath);
            var files = folder.Length > 0 ? d.Files : d.Files.Where(f => f.Path.ToLowerInvariant() == "skill.md").ToList();
            var hash = files.Count == d.Files.Count ? d.Hash : ComputeSnapshotHash(files);
            return new Skill
            {
                Name = s.Name,
                Description = s.Description,
                Path = "",
                RawContent = s.Content,
                Metadata = s.Metadata,
                Blob = new BlobData(files, hash, s.MdPath),
            };
        }).ToList();
        return new BlobInstallResult(skills, tree);
    }
}
