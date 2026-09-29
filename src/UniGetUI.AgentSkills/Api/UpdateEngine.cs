// Update checks and updates behind the public API (split into a
// check that reports and an update that reinstalls). Global skills are checked
// the way `skills update -g` checks them: Git tree SHAs through the GitHub API,
// falling back to a clone. Project skills, which `skills update` always
// refreshes, are compared against a fresh clone by content hash.

using System.Text.Json.Nodes;
using Devolutions.AgentSkills;

namespace Skills;

internal static class UpdateEngine
{
    private const string FailedToCheck = "Failed to check source";
    private const string DeletedUpstream = "Deleted upstream";
    private const string Ambiguous = "Multiple current paths match this skill";
    private const string MissingSourceUrl = "Lock file is missing sourceUrl for this generic Git source";

    private static string? S(JsonNode? e, string k) => Json.Str(e, k);

    private static string? NonEmpty(JsonNode? e, string k) => Json.NonEmpty(e, k);

    private static void AddToGroup<T>(List<(string Key, List<T> Items)> groups, string key, T item)
    {
        var i = groups.FindIndex(g => g.Key == key);
        if (i >= 0) groups[i].Items.Add(item);
        else groups.Add((key, [item]));
    }

    private sealed class Collector(SkillScope scope)
    {
        public readonly List<SkillUpdate> Updates = [];
        public readonly List<UncheckedSkill> Unchecked = [];

        public void Skip(string name, string reason, string? source) => Unchecked.Add(new UncheckedSkill(name, scope, reason, source));
    }

    /// Pinned skills (`skills add --pin`) are left alone, as `skills update` does.
    private static bool SkipPinned(string name, JsonNode entry, Collector c)
    {
        if (Pinning.PinnedRef(entry) is not { } pin) return false;
        c.Skip(name, $"Pinned to {pin}", S(entry, "source"));
        return true;
    }

    public static SkillUpdateCheckResult Check(IReadOnlyList<SkillScope> scopes, IReadOnlyCollection<string>? filter, Action<string> log)
    {
        var updates = new List<SkillUpdate>();
        var skipped = new List<UncheckedSkill>();
        foreach (var scope in scopes)
        {
            var c = new Collector(scope);
            if (scope == SkillScope.Global) CheckGlobal(filter, c, log);
            else CheckProject(filter, c, log);
            updates.AddRange(c.Updates);
            skipped.AddRange(c.Unchecked);
        }
        return new SkillUpdateCheckResult(updates, skipped);
    }

    private static void CheckWellKnownGroups(List<(string Key, List<WellKnownItem> Items)> groups, SkillScope scope, Collector c, Action<string> log)
    {
        foreach (var (baseUrl, items) in groups)
        {
            Sys.ThrowIfCancelled();
            log($"Checking skills from source: {baseUrl}");
            var check = UpdateChecks.CheckWellKnown(baseUrl, items);
            if (check == null)
            {
                foreach (var i in items) c.Skip(i.Name, FailedToCheck, baseUrl);
                continue;
            }
            foreach (var name in check.Removed) c.Skip(name, DeletedUpstream, baseUrl);
            foreach (var name in check.ChangedSkills)
            {
                var item = items.First(i => i.Name == name);
                c.Updates.Add(new SkillUpdate
                {
                    Name = name,
                    Scope = scope,
                    Source = baseUrl,
                    CurrentHash = item.Digest,
                    InstallSource = baseUrl,
                    EveSubagents = scope == SkillScope.Project ? item.Subagents : null,
                });
            }
        }
    }

    private static void CheckGlobal(IReadOnlyCollection<string>? filter, Collector c, Action<string> log)
    {
        var skills = SkillLock.Read().Skills;
        var checkable = new List<(string Name, JsonNode Entry)>();
        var wkGroups = new List<(string Key, List<WellKnownItem> Items)>();
        var notionGroups = new List<(string Key, List<(string Name, JsonNode Entry, string PageId)> Items)>();
        foreach (var (name, entry) in skills)
        {
            if (!UpdateChecks.MatchesSkillFilter(name, filter) || entry == null) continue;
            if (SkipPinned(name, entry, c)) continue;
            if (S(entry, "sourceType") == "well-known" && NonEmpty(entry, "sourceBaseUrl") is { } b && NonEmpty(entry, "wellKnownDigest") is { } digest)
            {
                AddToGroup(wkGroups, b, new WellKnownItem(name, digest, null));
                continue;
            }
            if (S(entry, "sourceType") == "notion" && NonEmpty(entry, "notionPageId") is { } pageId)
            {
                AddToGroup(notionGroups, S(entry, "source") ?? "", (name, entry, pageId));
                continue;
            }
            if (NonEmpty(entry, "skillFolderHash") == null || NonEmpty(entry, "skillPath") == null)
            {
                c.Skip(name, UpdateChecks.GetSkipReason(entry), UpdateChecks.GetManualInstallSource(S(entry, "sourceUrl") ?? "", S(entry, "sourceType") ?? "", S(entry, "ref")));
                continue;
            }
            checkable.Add((name, entry));
        }

        CheckWellKnownGroups(wkGroups, SkillScope.Global, c, log);
        CheckNotionGroups(notionGroups, c, log);

        var bySource = new List<(string Key, List<(string Name, JsonNode Entry)> Items)>();
        foreach (var item in checkable) AddToGroup(bySource, $"{S(item.Entry, "source")}\n{S(item.Entry, "ref")}", item);

        foreach (var (_, items) in bySource)
        {
            Sys.ThrowIfCancelled();
            var first = items[0].Entry;
            var source = S(first, "source") ?? "";
            var sourceUrl = NonEmpty(first, "sourceUrl") ?? source;
            var firstRef = S(first, "ref");
            log($"Checking skills from source: {source}");
            var isGithub = S(first, "sourceType") == "github";
            var lockedForSource = skills.Where(kv => S(kv.Value, "source") == source && S(kv.Value, "ref") == firstRef).Select(kv => kv.Key).ToList();

            void AddUpdate(string name, JsonNode entry, string? latest, bool relocated)
            {
                var use = UpdateSourceEntry.FromJson(entry);
                if (UpdateSource.BuildUpdateInstallSource(use) is not { } installSource)
                {
                    c.Skip(name, MissingSourceUrl, null);
                    return;
                }
                c.Updates.Add(new SkillUpdate
                {
                    Name = name,
                    Scope = SkillScope.Global,
                    Source = source,
                    CurrentHash = S(entry, "skillFolderHash"),
                    LatestHash = latest,
                    Relocated = relocated,
                    InstallSource = installSource,
                    FullDepth = UpdateSource.ShouldUseFullDepthForUpdate(use),
                    PinGitHubHost = use.SourceType == "github",
                });
            }

            if (isGithub)
            {
                if (Blob.FetchRepoTree(source, firstRef, true) is { } tree)
                {
                    var blobPaths = tree.Tree.Where(e => e.Kind == "blob").Select(e => e.Path).ToHashSet();
                    var missing = lockedForSource.Any(n => NonEmpty(skills[n], "skillPath") is { } p && !blobPaths.Contains(p));
                    if (!missing)
                    {
                        foreach (var (name, entry) in items)
                        {
                            if (Blob.GetSkillFolderHashFromTree(tree, S(entry, "skillPath") ?? "") is { } latest && latest != S(entry, "skillFolderHash"))
                                AddUpdate(name, entry, latest, false);
                        }
                        continue;
                    }
                    log("  Skill paths changed; resolving via Git clone");
                }
                else
                {
                    log("  GitHub API unavailable; checking via Git clone");
                }
            }

            string? temp = null;
            try
            {
                temp = Git.CloneRepo(sourceUrl, firstRef);
                var r = SkillRelocation.Resolve(lockedForSource, skills, UpdateChecks.DiscoveredLocations(temp));
                foreach (var (name, entry) in items)
                {
                    if (r.AmbiguousSkills.Contains(name))
                    {
                        c.Skip(name, Ambiguous, source);
                        continue;
                    }
                    if (r.DeletedSkills.Contains(name))
                    {
                        c.Skip(name, DeletedUpstream, source);
                        continue;
                    }
                    if (!r.ResolvedPaths.TryGetValue(name, out var sp)) continue;
                    var hash = S(entry, "skillFolderHash") ?? "";
                    var usesTreeHash = isGithub && hash.Length == 40 && hash.All(char.IsAsciiHexDigit);
                    var latest = usesTreeHash
                        ? Git.GetGitTreeHash(temp, sp)
                        : InstallRecords.TryComputeHash(NodePath.Join(temp, NodePath.Dirname(sp)));
                    var relocated = sp != S(entry, "skillPath");
                    if (relocated || (latest != null && latest != hash))
                    {
                        var moved = entry.DeepClone();
                        moved["skillPath"] = sp;
                        AddUpdate(name, moved, latest, relocated);
                    }
                }
            }
            catch (Exception e) when (UpdateChecks.IsCheckFailure(e))
            {
                log($"  Failed to check skills from {source}: {e.Message}");
                foreach (var (name, _) in items) c.Skip(name, FailedToCheck, source);
            }
            finally
            {
                Git.TryCleanup(temp);
            }
        }
    }

    /// Notion skills: each page's version id against the one recorded at install.
    /// Without the Notion CLI, or signed out, nothing more can be checked.
    private static void CheckNotionGroups(List<(string Key, List<(string Name, JsonNode Entry, string PageId)> Items)> groups, Collector c, Action<string> log)
    {
        NotionCliException? blocked = null;
        foreach (var (source, items) in groups)
        {
            if (blocked == null) log($"Checking skills from source: {source}");
            foreach (var (name, entry, pageId) in items)
            {
                Sys.ThrowIfCancelled();
                if (blocked != null)
                {
                    c.Skip(name, blocked.Message, source);
                    continue;
                }
                try
                {
                    var (latest, _) = NotionFeed.GetSkillDirectory(pageId);
                    var current = NonEmpty(entry, "notionVersionId");
                    if (latest == current) continue;
                    c.Updates.Add(new SkillUpdate
                    {
                        Name = name,
                        Scope = SkillScope.Global,
                        Source = source,
                        CurrentHash = current,
                        LatestHash = latest,
                        InstallSource = NotionFeed.CanonicalUrl(pageId),
                    });
                }
                catch (NotionCliException e) when (e.Failure == NotionCliFailure.NotFound)
                {
                    c.Skip(name, DeletedUpstream, source);
                }
                catch (NotionCliException e) when (e.Failure is NotionCliFailure.NotInstalled or NotionCliFailure.SignedOut)
                {
                    log($"  {e.Message}");
                    blocked = e;
                    c.Skip(name, e.Message, source);
                }
                catch (NotionCliException e)
                {
                    log($"  Failed to check {name} in Notion: {e.Message}");
                    c.Skip(name, FailedToCheck, source);
                }
            }
        }
    }

    private static string SourceKey(JsonNode? e) => NonEmpty(e, "sourceUrl") ?? S(e, "source") ?? "";

    private static void CheckProject(IReadOnlyCollection<string>? filter, Collector c, Action<string> log)
    {
        var localLock = LocalLock.Read();
        var wkGroups = new List<(string Key, List<WellKnownItem> Items)>();
        var updatable = new List<(string Name, JsonNode Entry)>();
        foreach (var (name, entry) in localLock.Skills)
        {
            if (!UpdateChecks.MatchesSkillFilter(name, filter) || entry == null) continue;
            if (S(entry, "sourceType") is "node_modules" or "local") continue;
            if (SkipPinned(name, entry, c)) continue;
            if (S(entry, "sourceType") == "well-known" && NonEmpty(entry, "sourceUrl") is { } url && NonEmpty(entry, "wellKnownDigest") is { } digest)
            {
                AddToGroup(wkGroups, url, new WellKnownItem(name, digest, Json.StringArray(entry, "subagents")));
                continue;
            }
            if (NonEmpty(entry, "skillPath") == null)
            {
                c.Skip(name, "Installed before skillPath tracking", UpdateSource.BuildLocalUpdateSource(UpdateSourceEntry.FromJson(entry)));
                continue;
            }
            updatable.Add((name, entry));
        }

        CheckWellKnownGroups(wkGroups, SkillScope.Project, c, log);

        var bySource = new List<(string Key, List<(string Name, JsonNode Entry)> Items)>();
        foreach (var sk in updatable) AddToGroup(bySource, $"{SourceKey(sk.Entry)}\n{S(sk.Entry, "ref")}", sk);

        foreach (var (_, group) in bySource)
        {
            Sys.ThrowIfCancelled();
            var first = group[0].Entry;
            var source = SourceKey(first);
            var r = S(first, "ref");
            if (UpdateSource.BuildLocalCloneSource(UpdateSourceEntry.FromJson(first)) is not { } cloneSource)
            {
                foreach (var (name, _) in group) c.Skip(name, MissingSourceUrl, null);
                continue;
            }
            log($"Checking skills from source: {source}");
            var lockedForSource = localLock.Skills.Where(kv => SourceKey(kv.Value) == source && S(kv.Value, "ref") == r).Select(kv => kv.Key).ToList();
            string? temp = null;
            try
            {
                temp = Git.CloneRepo(cloneSource, r);
                var res = SkillRelocation.Resolve(lockedForSource, localLock.Skills, UpdateChecks.DiscoveredLocations(temp));
                foreach (var (name, entry) in group)
                {
                    if (res.AmbiguousSkills.Contains(name))
                    {
                        c.Skip(name, Ambiguous, source);
                        continue;
                    }
                    if (res.DeletedSkills.Contains(name))
                    {
                        c.Skip(name, DeletedUpstream, source);
                        continue;
                    }
                    if (!res.ResolvedPaths.TryGetValue(name, out var sp)) continue;
                    var folder = NodePath.Join(temp, NodePath.Dirname(sp));
                    var current = S(entry, "computedHash");
                    var folderHash = InstallRecords.TryComputeHash(folder);
                    var relocated = sp != S(entry, "skillPath");
                    // Blob installs record a snapshot hash (installable files only).
                    if (!relocated && current != null && (current == folderHash || current == SnapshotHash(folder))) continue;
                    var moved = entry.DeepClone();
                    moved["skillPath"] = sp;
                    var use = UpdateSourceEntry.FromJson(moved);
                    if (UpdateSource.BuildLocalUpdateSource(use) is not { } installSource)
                    {
                        c.Skip(name, MissingSourceUrl, null);
                        continue;
                    }
                    c.Updates.Add(new SkillUpdate
                    {
                        Name = name,
                        Scope = SkillScope.Project,
                        Source = source,
                        CurrentHash = current,
                        LatestHash = folderHash,
                        Relocated = relocated,
                        InstallSource = installSource,
                        FullDepth = UpdateSource.ShouldUseFullDepthForUpdate(use),
                        PinGitHubHost = use.SourceType == "github",
                        EveSubagents = Json.StringArray(entry, "subagents"),
                    });
                }
            }
            catch (Exception e) when (UpdateChecks.IsCheckFailure(e))
            {
                log($"  Failed to check skills from {source}: {e.Message}");
                foreach (var (name, _) in group) c.Skip(name, FailedToCheck, source);
            }
            finally
            {
                Git.TryCleanup(temp);
            }
        }
    }

    /// Blob.ComputeSnapshotHash over the files an install would copy.
    private static string? SnapshotHash(string dir)
    {
        try
        {
            var files = new List<SnapshotFile>();
            void Walk(string d, string rel)
            {
                foreach (var e in Fs.ReadDir(d))
                {
                    var p = rel.Length == 0 ? e.Name : $"{rel}/{e.Name}";
                    if (e.IsDirectory)
                    {
                        if (e.Name is not (".git" or "__pycache__" or "__pypackages__" or "node_modules")) Walk(e.FullPath, p);
                    }
                    else if (e.IsFile && e.Name != "metadata.json")
                    {
                        files.Add(new SnapshotFile(p, File.ReadAllBytes(e.FullPath)));
                    }
                }
            }
            Walk(dir, "");
            return Blob.ComputeSnapshotHash(files);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static SkillUpdateResult Update(IReadOnlyList<SkillUpdate> updates, Action<string> log)
    {
        var outcomes = new List<SkillUpdateOutcome>();
        var installed = new Dictionary<SkillScope, List<InstalledSkill>>();
        foreach (var u in updates)
        {
            Sys.ThrowIfCancelled();
            if (u.InstallSource.Length == 0)
            {
                outcomes.Add(new SkillUpdateOutcome(u.Name, u.Scope, SkillOperationStatus.Failed, "No install source recorded"));
                continue;
            }
            log($"Updating {u.Name}…");
            // Reinstall for the agents that have the skill now (the CLI uses the detected agents).
            if (!installed.TryGetValue(u.Scope, out var list))
                installed[u.Scope] = list = Installer.ListInstalledSkills(u.Scope == SkillScope.Global);
            var sanitized = Installer.SanitizeName(u.Name);
            var agents = list.FirstOrDefault(s => Installer.SanitizeName(s.Name) == sanitized)?.Agents;
            var request = new SkillInstallRequest
            {
                Source = u.InstallSource,
                Skills = [u.Name],
                Scope = u.Scope,
                FullDepth = u.FullDepth,
                Agents = agents is { Count: > 0 } ? agents : null,
                EveSubagents = u.EveSubagents is { Count: > 0 } subs ? subs.Select(s => s.Length == 0 ? "root" : s).ToList() : null,
            };
            try
            {
                using var pin = u.PinGitHubHost ? Sys.EnterWithEnv("GH_HOST", "github.com") : null;
                var result = InstallEngine.Install(request, log);
                var o = result.Skills.FirstOrDefault(s => s.Name.Equals(u.Name, StringComparison.OrdinalIgnoreCase)) ?? result.Skills.FirstOrDefault();
                outcomes.Add(o is { Status: SkillOperationStatus.Succeeded }
                    ? new SkillUpdateOutcome(u.Name, u.Scope, SkillOperationStatus.Succeeded)
                    : new SkillUpdateOutcome(u.Name, u.Scope, SkillOperationStatus.Failed, o?.Error ?? "Installation failed"));
            }
            catch (SkillsException e)
            {
                outcomes.Add(new SkillUpdateOutcome(u.Name, u.Scope, SkillOperationStatus.Failed, e.Message));
            }
        }
        return new SkillUpdateResult(outcomes);
    }
}
