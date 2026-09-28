// Skills installed by the GitHub CLI's `gh skill install` (extension; not in
// the reference CLI).
//
// `gh skill` copies skills into the same agent directories but records their
// origin in SKILL.md frontmatter (`metadata.github-repo`, `github-ref`,
// `github-tree-sha`, `github-path`, `github-pinned`, or `local-path`) instead
// of a lock file. `skills list` shows that origin, and `skills update` reports
// available updates but leaves reinstalling to gh.

using System.Text.Json.Nodes;
using static Skills.Ansi;

namespace Skills;

/// Where a gh-installed skill came from: a GitHub repository, or a local path.
internal abstract record GhOrigin
{
    /// `owner/repo` (github.com), `host/owner/repo`, or the local path.
    public abstract string Source();

    /// The `Source:` value shown by `skills list`.
    public string ListLabel() =>
        this is GhGitHubOrigin { Pinned: { } p } ? $"{Source()} (gh skill, pinned {p})" : $"{Source()} (gh skill)";
}

internal sealed record GhGitHubOrigin(
    string Url,
    string Host,
    string Owner,
    string Repo,
    string? GitRef,
    string? TreeSha,
    string? Path,
    string? Pinned) : GhOrigin
{
    public override string Source() =>
        PreviewCommand.AsciiEqualsIgnoreCase(Host, "github.com") ? $"{Owner}/{Repo}" : $"{Host}/{Owner}/{Repo}";
}

internal sealed record GhLocalOrigin(string LocalPath) : GhOrigin
{
    public override string Source() => LocalPath;
}

internal sealed record GhSkill(string Name, string Dir, GhOrigin Origin);

internal enum GhStatusKind
{
    UpdateAvailable,
    Pinned,
}

internal sealed record GhStatus(GhStatusKind Kind, string? Pinned = null);

internal static class GhInstalled
{
    /// `https://<host>/<owner>/<repo>` (trailing `/` and `.git` ignored).
    public static (string Host, string Owner, string Repo)? ParseRepoUrl(string url)
    {
        string rest;
        if (url.StartsWith("https://", StringComparison.Ordinal)) rest = url["https://".Length..];
        else if (url.StartsWith("http://", StringComparison.Ordinal)) rest = url["http://".Length..];
        else return null;
        rest = rest.TrimEnd('/');
        var parts = rest.Split('/');
        if (parts.Length != 3) return null;
        var repo = parts[2].EndsWith(".git", StringComparison.Ordinal) ? parts[2][..^4] : parts[2];
        if (parts[0].Length == 0 || parts[1].Length == 0 || repo.Length == 0) return null;
        return (parts[0], parts[1], repo);
    }

    /// `github-ref` without a `refs/heads/` or `refs/tags/` prefix.
    public static string ShortRef(string r)
    {
        if (r.StartsWith("refs/heads/", StringComparison.Ordinal)) return r["refs/heads/".Length..];
        if (r.StartsWith("refs/tags/", StringComparison.Ordinal)) return r["refs/tags/".Length..];
        return r;
    }

    private static string? MetaStr(JsonObject m, string key) => Json.NonEmpty(m, key);

    private static JsonObject? FrontmatterData(string raw)
    {
        try
        {
            return Frontmatter.Parse(raw, YamlFlavor.Serde).Data;
        }
        catch
        {
            return null;
        }
    }

    /// The gh origin recorded in a SKILL.md's frontmatter, if any.
    public static GhOrigin? ParseGhOrigin(string skillMd)
    {
        if (FrontmatterData(skillMd) is not { } data || Json.Get(data, "metadata") is not JsonObject m) return null;
        if (MetaStr(m, "github-repo") is { } url && ParseRepoUrl(url) is var (host, owner, repo))
            return new GhGitHubOrigin(url, host, owner, repo, MetaStr(m, "github-ref"), MetaStr(m, "github-tree-sha"), MetaStr(m, "github-path"), MetaStr(m, "github-pinned"));
        return MetaStr(m, "local-path") is { } path ? new GhLocalOrigin(path) : null;
    }

    private static string? ReadUtf8Lossy(string path)
    {
        try
        {
            return SkillDiscovery.ReadUtf8(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static GhOrigin? ReadGhOrigin(string skillDir) =>
        ReadUtf8Lossy(NodePath.Join(skillDir, "SKILL.md")) is { } raw ? ParseGhOrigin(raw) : null;

    /// Whether a lock has an entry for `name` (exact, else by sanitized name, as
    /// `skills list` matches them).
    public static bool LockHasSkill(JsonObject locked, string name)
    {
        if (locked.ContainsKey(name)) return true;
        var s = Installer.SanitizeName(name);
        return locked.Any(kv => Installer.SanitizeName(kv.Key) == s);
    }

    private static List<string> ScanRoots(bool global)
    {
        var roots = new List<string> { Installer.GetCanonicalSkillsDir(global, null) };
        var cwd = Sys.Cwd();
        foreach (var a in Agents.List)
        {
            string dir;
            if (global)
            {
                if (a.GlobalSkillsDir is not { } d) continue;
                dir = d;
            }
            else
            {
                dir = NodePath.Join(cwd, a.SkillsDir);
            }
            if (!roots.Contains(dir)) roots.Add(dir);
        }
        return roots;
    }

    /// Name of a valid installed skill (frontmatter `name` and `description`
    /// non-empty strings), parsed without printing warnings.
    private static string? InstalledSkillName(string raw)
    {
        if (FrontmatterData(raw) is not { } data) return null;
        return Json.NonEmpty(data, "name") is { } n && Json.NonEmpty(data, "description") != null ? Sanitize.Metadata(n) : null;
    }

    /// gh-installed skills of one scope that the lock does not track. Directories
    /// are scanned in order (canonical `.agents/skills`, then each agent's
    /// directory in agents-table order), entries ordinal by directory name; the
    /// first skill with a given name wins.
    public static List<GhSkill> ScanGhSkills(bool global, JsonObject locked)
    {
        var seen = new List<string>();
        var output = new List<GhSkill>();
        foreach (var root in ScanRoots(global))
        {
            List<DirEntry> entries;
            try
            {
                entries = Fs.ReadDir(root);
            }
            catch
            {
                continue;
            }
            var names = Collate.StableSort(entries.Select(e => e.Name), string.CompareOrdinal);
            foreach (var n in names)
            {
                var dir = NodePath.Join(root, n);
                if (!Fs.IsDir(dir)) continue;
                if (ReadUtf8Lossy(NodePath.Join(dir, "SKILL.md")) is not { } raw) continue;
                if (InstalledSkillName(raw) is not { } name) continue;
                if (seen.Contains(name)) continue;
                seen.Add(name);
                if (LockHasSkill(locked, name)) continue;
                if (ParseGhOrigin(raw) is { } origin) output.Add(new GhSkill(name, dir, origin));
            }
        }
        return output;
    }

    /// SKILL.md path for the tree lookup: `github-path` itself when it names the
    /// file, else `<github-path>/SKILL.md`.
    public static string GhSkillMdPath(string path) =>
        path.EndsWith("SKILL.md", StringComparison.Ordinal) ? path : $"{path.TrimEnd('/')}/SKILL.md";

    /// Check gh-installed skills the way `gh skill update` does: against the
    /// latest release, else the default branch. Only github.com skills with a
    /// tree SHA and path are checked; failures are ignored. Results are in input
    /// order; each repository is fetched once.
    public static List<(int Index, GhStatus Status)> CheckGhSkills(IReadOnlyList<GhSkill> skills)
    {
        var results = new List<(int Index, GhStatus Status)>();
        var groups = new List<(string Key, List<int> Items)>();
        for (var i = 0; i < skills.Count; i++)
        {
            if (skills[i].Origin is not GhGitHubOrigin { TreeSha: not null, Path: not null } o) continue;
            if (!PreviewCommand.AsciiEqualsIgnoreCase(o.Host, "github.com")) continue;
            if (o.Pinned is { } p)
            {
                results.Add((i, new GhStatus(GhStatusKind.Pinned, p)));
                continue;
            }
            var key = $"{o.Owner}/{o.Repo}";
            var gi = groups.FindIndex(g => g.Key == key);
            if (gi >= 0) groups[gi].Items.Add(i);
            else groups.Add((key, [i]));
        }
        foreach (var (ownerRepo, idxs) in groups)
        {
            string? r;
            try
            {
                r = GitHubRelease.ResolveLatestRelease(ownerRepo);
            }
            catch (GitHubReleaseException)
            {
                continue;
            }
            if (Blob.FetchRepoTree(ownerRepo, r, true) is not { } tree) continue;
            foreach (var i in idxs)
            {
                if (skills[i].Origin is not GhGitHubOrigin { TreeSha: { } sha, Path: { } path }) continue;
                if (Blob.GetSkillFolderHashFromTree(tree, GhSkillMdPath(path)) is { } latest && latest != sha)
                    results.Add((i, new GhStatus(GhStatusKind.UpdateAvailable)));
            }
        }
        return Collate.StableSort(results, (a, b) => a.Index.CompareTo(b.Index));
    }

    /// For `skills update`: check the gh-installed skills of one scope that match
    /// the name filter and print the `Managed by gh skill` notice when any has an
    /// update or is pinned. Returns how many gh skills matched the filter.
    public static int ReportGhSkills(bool global, JsonObject locked, List<string>? filter)
    {
        var skills = ScanGhSkills(global, locked).Where(s => UpdateCommand.MatchesSkillFilter(s.Name, filter)).ToList();
        if (skills.Count == 0) return 0;
        var results = CheckGhSkills(skills);
        if (results.Count > 0)
        {
            Term.OutLine();
            Term.OutLine($"{Dim}Managed by gh skill (update them with gh skill update):{Reset}");
            foreach (var (i, status) in results)
            {
                var s = skills[i];
                var tail = status.Kind == GhStatusKind.UpdateAvailable ? "update available" : $"pinned to {Sanitize.Metadata(status.Pinned!)}";
                Term.OutLine($"  • {Sanitize.Metadata(s.Name)} {Dim}({Sanitize.Metadata(s.Origin.Source())}){Reset} {tail}");
            }
        }
        return skills.Count;
    }
}
