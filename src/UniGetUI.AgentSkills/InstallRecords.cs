// Lock-file bookkeeping after an install: the global
// ~/.agents/.skill-lock.json and project skills-lock.json entries, their
// hashes, and the source fields used by `update`. Shared by the CLI and the
// public API so both write identical lock files.

using System.Text.Json.Nodes;

namespace Skills;

/// Lock `source` fields derived from a parsed source.
internal sealed record LockSources(string? Normalized, string? LockSource, string? ProjectLockSourceUrl);

internal static class InstallRecords
{
    /// Only independently hosted repository snapshots can bypass a git clone.
    public static bool IsBlobEligible(string ownerRepo) => Blob.IsAllowedRepo(ownerRepo.ToLowerInvariant());

    public static string? GetLockSource(string parsedUrl, string? normalized)
    {
        if (parsedUrl.StartsWith("git@") || parsedUrl.StartsWith("ssh://")) return parsedUrl;
        if (parsedUrl.StartsWith("http://") || parsedUrl.StartsWith("https://"))
        {
            var u = WebUrl.Parse(parsedUrl);
            if (u == null) return normalized;
            if (u.Hostname != "github.com") return parsedUrl;
        }
        return normalized;
    }

    public static string? GetProjectLockSourceUrl(string sourceType, string sourceUrl) => sourceType is "git" or "gitlab" ? sourceUrl : null;

    public static LockSources GetLockSources(ParsedSource parsed, bool directDownload)
    {
        if (directDownload) return new LockSources(null, null, null);
        var normalized = SourceParser.GetOwnerRepo(parsed);
        return new LockSources(normalized, GetLockSource(parsed.Url, normalized), GetProjectLockSourceUrl(parsed.Kind, parsed.Url));
    }

    /// Skill name → repo-relative SKILL.md path, for skills fetched into
    /// `tempDir` or from a blob snapshot.
    public static JsonObject ComputeSkillFiles(IEnumerable<Skill> selected, BlobInstallResult? blob, string? tempDir)
    {
        var skillFiles = new JsonObject();
        foreach (var s in selected)
        {
            if (blob != null && s.Blob is { } b) skillFiles[s.Name] = b.RepoPath;
            else if (tempDir != null && s.Path == tempDir) skillFiles[s.Name] = "SKILL.md";
            else if (tempDir != null && s.Path.StartsWith(tempDir + NodePath.Sep, StringComparison.Ordinal))
                skillFiles[s.Name] = $"{string.Join("/", s.Path[(tempDir.Length + 1)..].Split(NodePath.Sep))}/SKILL.md";
        }
        return skillFiles;
    }

    /// Hash each installed skill (when `computeHashes` or for a project install)
    /// and record it in the global or project lock file. Returns the hashes by
    /// display name. `eveSubagents` is non-null when Eve was a target; `pinned`
    /// marks the ref as a pin (`add --pin`) that `update` leaves alone.
    public static Dictionary<string, string> RecordInstalledSkills(
        ParsedSource parsed, bool directDownload, BlobInstallResult? blob, string? tempDir,
        IReadOnlyList<Skill> selected, JsonObject skillFiles, IReadOnlySet<string> okNames,
        bool installGlobally, bool computeHashes, List<string>? eveSubagents, string cwd, bool pinned = false)
    {
        var (normalizedSource, lockSource, projectLockSourceUrl) = GetLockSources(parsed, directDownload);
        var hashes = new Dictionary<string, string>();
        if (okNames.Count > 0 && (computeHashes || !installGlobally))
        {
            foreach (var s in selected)
            {
                var name = SkillDiscovery.DisplayName(s);
                if (!okNames.Contains(name) || hashes.ContainsKey(name)) continue;
                var h = blob != null && s.Blob is { } b ? b.SnapshotHash : TryComputeHash(s.Path);
                if (h != null) hashes[name] = h;
            }
        }

        if (okNames.Count > 0 && installGlobally && normalizedSource != null)
        {
            var cachedTree = parsed.Kind == "github" && blob == null ? Blob.FetchRepoTree(normalizedSource, parsed.Ref, true) : null;
            foreach (var s in selected)
            {
                if (!okNames.Contains(SkillDiscovery.DisplayName(s))) continue;
                var skillPath = Json.AsString(skillFiles[s.Name]);
                var folderHash = "";
                if (blob != null && skillPath != null)
                {
                    folderHash = Blob.GetSkillFolderHashFromTree(blob.Tree, skillPath) ?? folderHash;
                }
                else if (parsed.Kind == "github" && skillPath != null && cachedTree != null)
                {
                    folderHash = Blob.GetSkillFolderHashFromTree(cachedTree, skillPath) ?? folderHash;
                }
                else if (skillPath != null && tempDir != null)
                {
                    folderHash = TryComputeHash(NodePath.Join(tempDir, NodePath.Dirname(skillPath))) ?? folderHash;
                }
                var e = new JsonObject
                {
                    ["source"] = lockSource ?? normalizedSource,
                    ["sourceType"] = parsed.Kind,
                    ["sourceUrl"] = parsed.Url,
                };
                if (parsed.Ref != null)
                {
                    e["ref"] = parsed.Ref;
                    if (pinned) e["pinned"] = true;
                }
                if (skillPath != null) e["skillPath"] = skillPath;
                e["skillFolderHash"] = folderHash;
                if (s.PluginName != null) e["pluginName"] = s.PluginName;
                TryAddToGlobalLock(s.Name, e);
            }
        }

        if (okNames.Count > 0 && !installGlobally && !directDownload)
        {
            var record = eveSubagents != null && (eveSubagents.Count > 1 || eveSubagents.Any(s => s.Length > 0));
            foreach (var s in selected)
            {
                var name = SkillDiscovery.DisplayName(s);
                if (!okNames.Contains(name) || !hashes.TryGetValue(name, out var h)) continue;
                var skillPath = Json.AsString(skillFiles[s.Name]);
                var e = new JsonObject { ["source"] = string.IsNullOrEmpty(lockSource) ? parsed.Url : lockSource };
                if (projectLockSourceUrl != null) e["sourceUrl"] = projectLockSourceUrl;
                if (parsed.Ref != null)
                {
                    e["ref"] = parsed.Ref;
                    if (pinned) e["pinned"] = true;
                }
                e["sourceType"] = parsed.Kind;
                if (!string.IsNullOrEmpty(skillPath)) e["skillPath"] = skillPath;
                e["computedHash"] = h;
                if (record) e["subagents"] = new JsonArray(eveSubagents!.Select(x => (JsonNode?)x).ToArray());
                TryAddToLocalLock(s.Name, e, cwd);
            }
        }
        return hashes;
    }

    /// Record well-known skills installed from `url`. `installedDirs` maps an
    /// install name to the directory it landed in (hashed for project locks).
    public static void RecordWellKnownSkills(string url, IEnumerable<WellKnownSkill> installed, IReadOnlyDictionary<string, string> installedDirs, bool installGlobally, string cwd)
    {
        var sourceIdentifier = WellKnown.GetSourceIdentifier(url);
        foreach (var s in installed)
        {
            if (installGlobally)
            {
                TryAddToGlobalLock(s.InstallName, new JsonObject
                {
                    ["source"] = sourceIdentifier,
                    ["sourceType"] = "well-known",
                    ["sourceUrl"] = s.SourceUrl,
                    ["skillFolderHash"] = "",
                    ["sourceBaseUrl"] = url,
                    ["wellKnownDigest"] = WellKnown.ComputeSkillDigest(s),
                });
                continue;
            }
            if (!installedDirs.TryGetValue(s.InstallName, out var dir) || dir.Length == 0 || TryComputeHash(dir) is not { } hash) continue;
            TryAddToLocalLock(s.InstallName, new JsonObject
            {
                ["source"] = sourceIdentifier,
                ["sourceUrl"] = url,
                ["sourceType"] = "well-known",
                ["computedHash"] = hash,
                ["wellKnownDigest"] = WellKnown.ComputeSkillDigest(s),
            }, cwd);
        }
    }

    /// Record skills installed from Notion in the global lock, which the
    /// Notion installs have no skillPath; an empty skillFolderHash prevents
    /// GitHub update checks, while the Notion fields enable Notion update
    /// checks. Project installs from Notion stay untracked, like downloads.
    public static void RecordNotionSkills(IEnumerable<(string Name, NotionOrigin Origin)> installed, bool installGlobally)
    {
        if (!installGlobally) return;
        foreach (var (name, origin) in installed)
        {
            TryAddToGlobalLock(name, new JsonObject
            {
                ["source"] = origin.Source,
                ["sourceType"] = "notion",
                ["sourceUrl"] = origin.PageUrl,
                ["skillFolderHash"] = "",
                ["notionPageId"] = origin.Page.Id,
                ["notionVersionId"] = origin.VersionId,
            });
        }
    }

    public static string? TryComputeHash(string dir)
    {
        try
        {
            return LocalLock.ComputeSkillFolderHash(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void TryAddToGlobalLock(string name, JsonObject entry)
    {
        try
        {
            SkillLock.AddSkill(name, entry);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    public static void TryAddToLocalLock(string name, JsonObject entry, string cwd)
    {
        try
        {
            LocalLock.AddSkill(name, entry, cwd);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }
}
