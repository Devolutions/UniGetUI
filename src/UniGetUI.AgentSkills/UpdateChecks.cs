// Update detection shared by `skills update` and the public API (from
// update.ts): lock-entry classification, well-known digest checks and skill
// relocation discovery in a fresh clone.

using System.Text.Json.Nodes;

namespace Skills;

internal sealed record WellKnownItem(string Name, string Digest, List<string>? Subagents);

internal sealed record WellKnownCheck(bool Changed, List<string> ChangedSkills, List<string> Removed, List<string> NewSkills);

internal static class UpdateChecks
{
    public static bool MatchesSkillFilter(string name, IReadOnlyCollection<string>? filter) =>
        filter is not { Count: > 0 } || filter.Any(x => x.ToLowerInvariant() == name.ToLowerInvariant());

    /// Why a global lock entry cannot be checked for updates.
    public static string GetSkipReason(JsonNode? e) => Json.Str(e, "sourceType") switch
    {
        "local" => "Local path",
        "git" => "Git URL",
        "well-known" => "Well-known skill",
        "notion" => "Notion skill without a page id",
        _ => Json.NonEmpty(e, "skillFolderHash") == null ? "Private or deleted repo"
            : Json.NonEmpty(e, "skillPath") == null ? "No skill path recorded"
            : "No version tracking",
    };

    /// The source to reinstall a skill from by hand (`skills add &lt;source&gt; -g -y`).
    public static string GetManualInstallSource(string sourceUrl, string sourceType, string? r)
    {
        var url = sourceUrl;
        if (sourceType == "well-known" && url.IndexOf("/.well-known/", StringComparison.Ordinal) is var i and >= 0) url = url[..i];
        return UpdateSource.FormatSourceInput(url, r);
    }

    /// Whether `dir` has project skills (a lock file or a skill in .agents/skills).
    public static bool HasProjectSkills(string dir)
    {
        if (Fs.Exists(NodePath.Join(dir, "skills-lock.json"))) return true;
        var skillsDir = NodePath.Join(dir, ".agents", "skills");
        return Fs.TryReadDir(skillsDir).Any(e => e.IsDirectory && Fs.Exists(NodePath.Join(skillsDir, e.Name, "SKILL.md")));
    }

    public static bool IsCheckFailure(Exception e) => e is GitCloneException or DiscoverException or IOException or UnauthorizedAccessException;

    /// Every skill in a clone, with its repo-relative SKILL.md path.
    public static List<DiscoveredSkillLocation> DiscoveredLocations(string tempDir) =>
        SkillDiscovery.Discover(tempDir, null, new DiscoverOptions(FullDepth: true, IncludeDuplicateNames: true))
            .Select(sk => new DiscoveredSkillLocation(sk.Name, string.Join("/", NodePath.Join(NodePath.Relative(tempDir, sk.Path), "SKILL.md").Split(NodePath.Sep))))
            .ToList();

    /// Compare installed well-known skills against the source's index. Null
    /// when the index (or, for v1 indexes, every skill) could not be fetched.
    /// `force` (update `--force`) marks every tracked skill still in the index
    /// as changed without comparing digests.
    public static WellKnownCheck? CheckWellKnown(string baseUrl, List<WellKnownItem> items, bool force = false)
    {
        if (WellKnown.FetchIndex(baseUrl, true) is not { } index) return null;
        var byName = new Dictionary<string, NormalizedEntry>();
        foreach (var e in index.Entries) byName[e.Name] = e;
        var removed = items.Where(i => !byName.ContainsKey(i.Name)).Select(i => i.Name).ToList();
        var local = items.Select(i => i.Name).ToHashSet();
        var newSkills = index.Entries.Select(e => e.Name).Where(n => !local.Contains(n)).ToList();
        var changed = new List<string>();
        var needsContent = new List<WellKnownItem>();
        foreach (var item in items)
        {
            if (!byName.TryGetValue(item.Name, out var entry)) continue;
            if (entry is V2Entry v2)
            {
                if (force || item.Digest.Length == 0 || v2.Digest != item.Digest) changed.Add(item.Name);
            }
            else if (force)
            {
                changed.Add(item.Name);
            }
            else
            {
                needsContent.Add(item);
            }
        }
        if (needsContent.Count > 0)
        {
            var tracked = needsContent.Select(i => i.Name).ToHashSet();
            var entries = index.Entries.Where(e => tracked.Contains(e.Name)).ToList();
            var skills = Blob.ParallelMap(entries, WellKnown.FetchSkillByEntry).Where(s => s != null).Select(s => s!).ToList();
            if (skills.Count == 0) return null;
            var digests = new Dictionary<string, string>();
            foreach (var s in skills) digests[s.InstallName] = WellKnown.ComputeSkillDigest(s);
            foreach (var item in needsContent)
                if (!(digests.TryGetValue(item.Name, out var d) && item.Digest.Length > 0 && d == item.Digest)) changed.Add(item.Name);
        }
        return new WellKnownCheck(changed.Count > 0 || removed.Count > 0, changed, removed, newSkills);
    }
}
