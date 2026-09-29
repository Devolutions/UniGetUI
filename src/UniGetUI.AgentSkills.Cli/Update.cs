// `skills update` / `check` / `upgrade`.
//
// Changed skills are reinstalled through the same `add` command flow in-process.

using System.Text.Json.Nodes;
using static Skills.Ansi;

namespace Skills;

internal enum UpdateScope
{
    Project,
    Global,
    Both,
}

internal sealed class UpdateOptions
{
    public bool Global { get; set; }
    public bool Project { get; set; }
    public bool Yes { get; set; }
    public List<string>? Skills { get; set; }

    // Extensions: report without changing anything / reinstall even when
    // current / include pinned skills and clear their pin.
    public bool DryRun { get; set; }
    public bool Force { get; set; }
    public bool Unpin { get; set; }
}

internal sealed record SkippedSkill(string Name, string Reason, string SourceUrl, string SourceType, string? Ref);

internal static class UpdateCommand
{
    private sealed record ProjectSkill(string Name, JsonNode Entry);

    /// `WellKnownOutcome.Pending`: changed skills as (name, base URL) under `--dry-run`.
    private sealed record WellKnownOutcome(int Ok, int Fail, bool ChangedAny, List<(string Name, string Source)> Pending);

    private sealed record Resolution(List<string> Deleted, Dictionary<string, string> Resolved);

    public static UpdateOptions ParseOptions(IReadOnlyList<string> args)
    {
        var o = new UpdateOptions();
        var positional = new List<string>();
        foreach (var a in args)
        {
            switch (a)
            {
                case "-g" or "--global":
                    o.Global = true;
                    break;
                case "-p" or "--project":
                    o.Project = true;
                    break;
                case "-y" or "--yes":
                    o.Yes = true;
                    break;
                case "--dry-run":
                    o.DryRun = true;
                    break;
                case "--force":
                    o.Force = true;
                    break;
                case "--unpin":
                    o.Unpin = true;
                    break;
                default:
                    if (!a.StartsWith('-')) positional.Add(a);
                    break;
            }
        }
        if (positional.Count > 0) o.Skills = positional;
        return o;
    }

    /// Whether cwd has project skills (a lock file or a skill in .agents/skills).
    public static bool HasProjectSkills(string? cwd = null) => UpdateChecks.HasProjectSkills(cwd ?? Sys.Cwd());

    public static UpdateScope ResolveScope(UpdateOptions o)
    {
        if (o.Skills is { Count: > 0 }) return o.Global ? UpdateScope.Global : o.Project ? UpdateScope.Project : UpdateScope.Both;
        if (o.Global && o.Project) return UpdateScope.Both;
        if (o.Global) return UpdateScope.Global;
        if (o.Project) return UpdateScope.Project;
        if (o.Yes || !Term.StdinIsTty()) return HasProjectSkills() ? UpdateScope.Project : UpdateScope.Global;
        var options = new List<SelectOption<UpdateScope>>
        {
            new(UpdateScope.Project, "Project", "Update skills in current directory"),
            new(UpdateScope.Global, "Global", "Update skills in home directory"),
            new(UpdateScope.Both, "Both", "Update all skills"),
        };
        if (Ui.Select("Update scope", options, 0, out var scope)) return scope;
        Ui.Cancel("Cancelled");
        Term.Exit(0);
        return default;
    }

    public static bool MatchesSkillFilter(string name, List<string>? filter) => UpdateChecks.MatchesSkillFilter(name, filter);

    private static string? S(JsonNode? e, string k) => Json.Str(e, k);

    private static string? NonEmpty(JsonNode? e, string k) => Json.NonEmpty(e, k);

    public static string GetSkipReason(JsonNode? e) => UpdateChecks.GetSkipReason(e);

    public static string GetInstallSource(SkippedSkill skill) => UpdateChecks.GetManualInstallSource(skill.SourceUrl, skill.SourceType, skill.Ref);

    /// `Pinned skills (not updated; use --unpin to update them):` notice.
    private static void PrintPinnedNotice(List<(string Name, string Ref)> pinned)
    {
        if (pinned.Count == 0) return;
        Term.OutLine();
        Term.OutLine($"{Dim}Pinned skills (not updated; use --unpin to update them):{Reset}");
        foreach (var (name, r) in pinned) Term.OutLine($"  • {Sanitize.Metadata(name)} {Dim}({Sanitize.Metadata(r)}){Reset}");
    }

    public static void PrintSkippedSkills(List<SkippedSkill> skipped)
    {
        if (skipped.Count == 0) return;
        Term.OutLine();
        Term.OutLine($"{Dim}{skipped.Count} skill(s) cannot be checked automatically:{Reset}");
        var grouped = new List<(string Source, List<SkippedSkill> Skills)>();
        foreach (var sk in skipped)
        {
            var src = GetInstallSource(sk);
            var i = grouped.FindIndex(g => g.Source == src);
            if (i >= 0) grouped[i].Skills.Add(sk);
            else grouped.Add((src, [sk]));
        }
        foreach (var (source, skills) in grouped)
        {
            var names = string.Join(", ", skills.Select(x => Sanitize.Metadata(x.Name)));
            Term.OutLine($"  {Text}•{Reset} {names} {Dim}({skills[0].Reason}){Reset}");
            Term.OutLine($"    {Dim}To update: {Text}skills add {source} -g -y{Reset}");
        }
    }

    private static List<ProjectSkill> GetProjectSkillsForUpdate(List<string>? filter)
    {
        var output = new List<ProjectSkill>();
        foreach (var (name, entry) in LocalLock.Read().Skills)
        {
            if (!MatchesSkillFilter(name, filter) || entry == null) continue;
            if (S(entry, "sourceType") is "node_modules" or "local") continue;
            output.Add(new ProjectSkill(name, entry));
        }
        return output;
    }

    private static void PromptDeletions(string source, List<string> deleted, bool isGlobal, UpdateOptions o)
    {
        if (deleted.Count == 0) return;
        Term.OutLine();
        Term.OutLine($"{Dim}Warning:{Reset} The following skills from {Dim}{source}{Reset} appear to have been deleted upstream:");
        foreach (var d in deleted) Term.OutLine($"  {Dim}•{Reset} {d}");
        if (o.Yes || o.DryRun || !Term.StdinIsTty())
        {
            Term.OutLine($"{Dim}Skipping deletion in non-interactive mode.{Reset}");
            return;
        }
        if (Ui.Confirm("Would you like to remove the local copies of these deleted skills?") != true) return;
        foreach (var d in deleted)
        {
            Term.OutLine($"{Dim}Removing{Reset} {d}…");
            RemoveCommand.Run([d], new RemoveOptions { Yes = true, Global = isGlobal });
        }
    }

    private static Resolution CheckAndPromptForDeletions(string source, List<string> lockedNames, JsonObject lockSkills, bool isGlobal, UpdateOptions o, List<DiscoveredSkillLocation> discovered)
    {
        var r = SkillRelocation.Resolve(lockedNames, lockSkills, discovered);
        if (r.AmbiguousSkills.Count > 0)
        {
            Term.OutLine();
            Term.OutLine($"{Dim}Warning:{Reset} Multiple current paths match these skills from {Dim}{source}{Reset}; skipping them rather than deleting or migrating the wrong skill:");
            foreach (var n in r.AmbiguousSkills) Term.OutLine($"  {Dim}•{Reset} {Sanitize.Metadata(n)}");
        }
        PromptDeletions(source, r.DeletedSkills, isGlobal, o);
        return new Resolution(r.DeletedSkills, r.ResolvedPaths);
    }

    private static WellKnownCheck? CheckWellKnown(string baseUrl, List<WellKnownItem> items, bool force) =>
        UpdateChecks.CheckWellKnown(baseUrl, items, force);

    private static void PrintNewSkills(string baseUrl, List<string> newSkills, bool isGlobal)
    {
        if (newSkills.Count == 0) return;
        var names = newSkills.Select(Sanitize.Metadata).ToList();
        Term.OutLine($"  {Dim}{newSkills.Count} new skill(s) available from this source:{Reset} {string.Join(", ", names)}");
        Term.OutLine($"    {Dim}To install: {Text}skills add {baseUrl} --skill {string.Join(" ", names)}{(isGlobal ? " -g" : "")}{Reset}");
    }

    /// Reinstall with stdin inherited and the add command's output hidden,
    /// as when the standalone CLI captures its subprocess's stdout and stderr.
    private static bool SpawnAdd(List<string> args, bool githubPin)
    {
        var previousExitCode = Term.ExitCode;
        var previousRedirect = Term.StdoutRedirected;
        using var quiet = Term.SuppressOutput();
        using var host = githubPin
            ? Sys.Enter(new SysContext
            {
                Env = new Dictionary<string, string?> { ["GH_HOST"] = "github.com" },
                Warn = Term.ErrLine,
            })
            : null;
        try
        {
            return Program.RunCommand(args.ToArray()) == 0;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            Term.ExitCode = previousExitCode;
            Term.StdoutRedirected = previousRedirect;
        }
    }

    private static void AddSubagentArgs(List<string> args, List<string>? subs)
    {
        if (subs is not { Count: > 0 }) return;
        args.Add("--subagent");
        args.AddRange(subs.Select(s => s.Length == 0 ? "root" : s));
    }

    private static WellKnownOutcome ProcessWellKnownUpdates(List<(string BaseUrl, List<WellKnownItem> Items)> groups, bool isGlobal, UpdateOptions o)
    {
        int ok = 0, fail = 0;
        var changedAny = false;
        var pending = new List<(string Name, string Source)>();
        foreach (var (baseUrl, items) in groups)
        {
            Term.Out($"\r{Dim}Checking skills from source: {baseUrl}{Reset}\x1b[K\n");
            var check = CheckWellKnown(baseUrl, items, o.Force);
            if (check == null)
            {
                Term.OutLine($"  {Dim}✗ Failed to check skills from {baseUrl}{Reset}");
                continue;
            }
            if (!check.Changed)
            {
                PrintNewSkills(baseUrl, check.NewSkills, isGlobal);
                continue;
            }
            changedAny = true;
            PromptDeletions(baseUrl, check.Removed, isGlobal, o);
            PrintNewSkills(baseUrl, check.NewSkills, isGlobal);
            if (check.ChangedSkills.Count == 0) continue;
            if (o.DryRun)
            {
                pending.AddRange(check.ChangedSkills.Select(n => (n, baseUrl)));
                continue;
            }
            foreach (var name in check.ChangedSkills)
            {
                var safe = Sanitize.Metadata(name);
                Term.OutLine($"{Text}Updating {safe}…{Reset}");
                var args = new List<string> { "add", baseUrl, "--skill", name };
                if (!isGlobal) AddSubagentArgs(args, items.FirstOrDefault(i => i.Name == name)?.Subagents);
                if (isGlobal) args.Add("-g");
                args.Add("-y");
                if (SpawnAdd(args, false))
                {
                    ok++;
                    Term.OutLine($"  {Text}✓{Reset} Updated {safe}");
                }
                else
                {
                    fail++;
                    Term.OutLine($"  {Dim}✗ Failed to update {safe}{Reset}");
                }
            }
        }
        return new WellKnownOutcome(ok, fail, changedAny, pending);
    }

    /// `--dry-run` result: `Found N <scope> update(s)`, one bullet per update,
    /// and the no-changes note.
    private static void PrintDryRunUpdates(string scope, List<(string Name, string Source)> pending)
    {
        Term.OutLine($"{Text}Found {pending.Count} {scope} update(s){Reset}");
        Term.OutLine();
        foreach (var (name, source) in pending) Term.OutLine($"  • {Sanitize.Metadata(name)} {Dim}({Sanitize.Metadata(source)}){Reset}");
        Term.OutLine();
        Term.OutLine($"{Dim}Dry run: no changes made. Run {Program.CommandName} update without --dry-run to apply.{Reset}");
    }

    private static List<DiscoveredSkillLocation> DiscoveredLocations(string tempDir) => UpdateChecks.DiscoveredLocations(tempDir);

    private static void AddToGroup<T>(List<(string Key, List<T> Items)> groups, string key, T item)
    {
        var i = groups.FindIndex(g => g.Key == key);
        if (i >= 0) groups[i].Items.Add(item);
        else groups.Add((key, [item]));
    }

    private static bool IsCheckFailure(Exception e) => UpdateChecks.IsCheckFailure(e);

    private static (int Ok, int Fail, int Checked) UpdateGlobalSkills(UpdateOptions o)
    {
        var skills = SkillLock.Read().Skills;
        int ok = 0, fail = 0;
        if (skills.Count == 0)
        {
            if (o.Skills == null)
            {
                Term.OutLine($"{Dim}No global skills tracked in lock file.{Reset}");
                Term.OutLine($"{Dim}Install skills with{Reset} {Text}{Program.CommandName} add <package> -g{Reset}");
            }
            return (0, 0, GhInstalled.ReportGhSkills(true, skills, o.Skills));
        }

        var updates = new List<(string Name, JsonNode Entry)>();
        var skipped = new List<SkippedSkill>();
        var checkable = new List<(string Name, JsonNode Entry)>();
        var wkGroups = new List<(string Key, List<WellKnownItem> Items)>();
        var pinned = new List<(string Name, string Ref)>();
        var unpinned = new List<(string Name, JsonNode Entry)>();

        foreach (var (name, entry) in skills)
        {
            if (!MatchesSkillFilter(name, o.Skills) || entry == null) continue;
            var action = Pinning.GetPinAction(entry, o.Force, o.Unpin);
            if (action.Kind == PinActionKind.Skip)
            {
                pinned.Add((name, action.Ref!));
                continue;
            }
            if (action.Kind == PinActionKind.Unpin)
            {
                unpinned.Add((name, Pinning.UnpinnedEntry(entry)!));
                continue;
            }
            if (S(entry, "sourceType") == "well-known" && NonEmpty(entry, "sourceBaseUrl") is { } b && NonEmpty(entry, "wellKnownDigest") is { } digest)
            {
                AddToGroup(wkGroups, b, new WellKnownItem(name, digest, null));
                continue;
            }
            if (NonEmpty(entry, "skillFolderHash") == null || NonEmpty(entry, "skillPath") == null)
            {
                skipped.Add(new SkippedSkill(name, GetSkipReason(entry), S(entry, "sourceUrl") ?? "", S(entry, "sourceType") ?? "", S(entry, "ref")));
                continue;
            }
            checkable.Add((name, entry));
        }

        var wkCount = wkGroups.Sum(g => g.Items.Count);
        var wk = ProcessWellKnownUpdates(wkGroups, true, o);
        ok += wk.Ok;
        fail += wk.Fail;
        var wkChanged = wk.ChangedAny;

        var bySource = new List<(string Key, List<(string Name, JsonNode Entry)> Items)>();
        foreach (var item in checkable) AddToGroup(bySource, $"{S(item.Entry, "source")}\n{S(item.Entry, "ref")}", item);

        foreach (var (_, items) in bySource)
        {
            var first = items[0].Entry;
            var source = S(first, "source") ?? "";
            var sourceUrl = NonEmpty(first, "sourceUrl") ?? source;
            var firstRef = S(first, "ref");
            Term.Out($"\r{Dim}Checking skills from source: {source}{Reset}\x1b[K\n");
            var isGithub = S(first, "sourceType") == "github";
            var lockedForSource = skills.Where(kv => S(kv.Value, "source") == source && S(kv.Value, "ref") == firstRef).Select(kv => kv.Key).ToList();

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
                            if (Blob.GetSkillFolderHashFromTree(tree, S(entry, "skillPath") ?? "") is { } latest && (o.Force || latest != S(entry, "skillFolderHash")))
                                updates.Add((name, entry));
                        }
                        continue;
                    }
                    Term.OutLine($"  {Dim}Skill paths changed; resolving via Git clone{Reset}");
                }
                else
                {
                    Term.OutLine($"  {Dim}GitHub API unavailable; checking via Git clone{Reset}");
                }
            }

            string? temp = null;
            try
            {
                temp = Git.CloneRepo(sourceUrl, firstRef);
                var locations = DiscoveredLocations(temp);
                var res = CheckAndPromptForDeletions(source, lockedForSource, skills, true, o, locations);
                foreach (var (name, entry) in items)
                {
                    if (res.Deleted.Contains(name) || !res.Resolved.TryGetValue(name, out var sp)) continue;
                    var hash = S(entry, "skillFolderHash") ?? "";
                    var usesTreeHash = isGithub && hash.Length == 40 && hash.All(char.IsAsciiHexDigit);
                    string? latest;
                    if (usesTreeHash)
                    {
                        latest = Git.GetGitTreeHash(temp, sp);
                    }
                    else
                    {
                        try
                        {
                            latest = LocalLock.ComputeSkillFolderHash(NodePath.Join(temp, NodePath.Dirname(sp)));
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                        {
                            latest = null;
                        }
                    }
                    var relocated = sp != S(entry, "skillPath");
                    if (relocated || o.Force || (latest != null && latest != hash))
                    {
                        var e = entry.DeepClone();
                        e["skillPath"] = sp;
                        updates.Add((name, e));
                    }
                }
            }
            catch (Exception e) when (IsCheckFailure(e))
            {
                Term.OutLine($"  {Dim}✗ Failed to check skills from {source}{Reset}");
            }
            finally
            {
                Git.TryCleanup(temp);
            }
        }

        if (checkable.Count > 0) Term.Out("\r\x1b[K");
        var nothingToCheck = checkable.Count == 0 && skipped.Count == 0 && wkCount == 0 && unpinned.Count == 0;
        var checkedCount = nothingToCheck ? pinned.Count : checkable.Count + skipped.Count + wkCount + unpinned.Count + pinned.Count;
        // `--unpin`: pinned skills are reinstalled from their default branch.
        updates.AddRange(unpinned);
        var hasUpdates = updates.Count > 0 || (o.DryRun && wk.Pending.Count > 0);
        if (nothingToCheck)
        {
            if (pinned.Count == 0 && o.Skills == null) Term.OutLine($"{Dim}No global skills to check.{Reset}");
        }
        else if (!hasUpdates)
        {
            if (!(checkable.Count == 0 && skipped.Count > 0) && !wkChanged) Term.OutLine($"{Text}✓ All global skills are up to date{Reset}");
        }
        else if (o.DryRun)
        {
            var pending = new List<(string Name, string Source)>(wk.Pending);
            pending.AddRange(updates.Select(u => (u.Name, S(u.Entry, "source") ?? "")));
            PrintDryRunUpdates("global", pending);
        }
        else
        {
            var (a, b) = ReinstallGlobalUpdates(updates);
            ok += a;
            fail += b;
        }
        PrintPinnedNotice(pinned);
        checkedCount += GhInstalled.ReportGhSkills(true, skills, o.Skills);
        PrintSkippedSkills(skipped);
        return (ok, fail, checkedCount);
    }

    private static (int Ok, int Fail) ReinstallGlobalUpdates(List<(string Name, JsonNode Entry)> updates)
    {
        int ok = 0, fail = 0;
        Term.OutLine($"{Text}Found {updates.Count} global update(s){Reset}");
        Term.OutLine();
        foreach (var (name, entry) in updates)
        {
            var safe = Sanitize.Metadata(name);
            Term.OutLine($"{Text}Updating {safe}…{Reset}");
            // `--force` on a pinned skill: reinstall at its ref and keep it pinned.
            var pin = Pinning.PinnedRef(entry);
            var useEntry = UpdateSourceEntry.FromJson(entry);
            if (pin != null) useEntry = useEntry with { Ref = null };
            if (UpdateSource.BuildUpdateInstallSource(useEntry) is not { } installUrl)
            {
                fail++;
                Term.OutLine($"  {Dim}✗ Cannot update {safe}: lock file is missing sourceUrl for this generic Git source{Reset}");
                continue;
            }
            var args = new List<string> { "add", installUrl, "--skill", name };
            if (pin != null)
            {
                args.Add("--pin");
                args.Add(pin);
            }
            if (UpdateSource.ShouldUseFullDepthForUpdate(useEntry)) args.Add("--full-depth");
            args.Add("-g");
            args.Add("-y");
            if (SpawnAdd(args, useEntry.SourceType == "github"))
            {
                ok++;
                Term.OutLine($"  {Text}✓{Reset} Updated {safe}");
            }
            else
            {
                fail++;
                Term.OutLine($"  {Dim}✗ Failed to update {safe}{Reset}");
            }
        }
        return (ok, fail);
    }

    private static void PrintLegacyProjectSkills(List<ProjectSkill> legacy)
    {
        if (legacy.Count == 0) return;
        Term.OutLine();
        Term.OutLine($"{Dim}{legacy.Count} project skill(s) cannot be updated automatically (installed before skillPath tracking):{Reset}");
        foreach (var sk in legacy)
        {
            var reinstall = UpdateSource.BuildLocalUpdateSource(UpdateSourceEntry.FromJson(sk.Entry));
            Term.OutLine($"  {Text}•{Reset} {Sanitize.Metadata(sk.Name)}");
            Term.OutLine(reinstall != null
                ? $"    {Dim}To refresh: {Text}skills add {reinstall} -y{Reset}"
                : $"    {Dim}To refresh: reinstall using the original full Git URL; this lock entry only has an ambiguous shorthand.{Reset}");
        }
    }

    private static string SourceKey(JsonNode? e) => NonEmpty(e, "sourceUrl") ?? S(e, "source") ?? "";

    private static (int Ok, int Fail, int Checked) UpdateProjectSkills(UpdateOptions o)
    {
        var all = GetProjectSkillsForUpdate(o.Skills);
        var localLock = LocalLock.Read();
        int ok = 0, fail = 0;
        if (all.Count == 0)
        {
            if (o.Skills == null)
            {
                Term.OutLine($"{Dim}No project skills to update.{Reset}");
                Term.OutLine($"{Dim}Install project skills with{Reset} {Text}{Program.CommandName} add <package>{Reset}");
            }
            return (0, 0, GhInstalled.ReportGhSkills(false, localLock.Skills, o.Skills));
        }
        var total = all.Count;

        // Pinned skills are skipped (listed in a notice) unless --force/--unpin;
        // --unpin reinstalls them from their default branch.
        var pinned = new List<(string Name, string Ref)>();
        var project = new List<ProjectSkill>();
        var lockSkills = (JsonObject)localLock.Skills.DeepClone();
        foreach (var sk in all)
        {
            var action = Pinning.GetPinAction(sk.Entry, o.Force, o.Unpin);
            switch (action.Kind)
            {
                case PinActionKind.Skip:
                    pinned.Add((sk.Name, action.Ref!));
                    break;
                case PinActionKind.Unpin:
                {
                    var entry = Pinning.UnpinnedEntry(sk.Entry)!;
                    lockSkills[sk.Name] = entry.DeepClone();
                    project.Add(sk with { Entry = entry });
                    break;
                }
                default:
                    project.Add(sk);
                    break;
            }
        }
        if (project.Count == 0)
        {
            PrintPinnedNotice(pinned);
            var gh = GhInstalled.ReportGhSkills(false, localLock.Skills, o.Skills);
            return (0, 0, total + gh);
        }

        var wkGroups = new List<(string Key, List<WellKnownItem> Items)>();
        var nonWk = new List<ProjectSkill>();
        foreach (var sk in project)
        {
            if (S(sk.Entry, "sourceType") == "well-known" && NonEmpty(sk.Entry, "sourceUrl") is { } url && NonEmpty(sk.Entry, "wellKnownDigest") is { } digest)
            {
                AddToGroup(wkGroups, url, new WellKnownItem(sk.Name, digest, Json.StringArray(sk.Entry, "subagents")));
                continue;
            }
            nonWk.Add(sk);
        }
        var wkCount = wkGroups.Sum(g => g.Items.Count);
        var updatable = nonWk.Where(x => NonEmpty(x.Entry, "skillPath") != null).ToList();
        var legacy = nonWk.Where(x => NonEmpty(x.Entry, "skillPath") == null).ToList();

        if (updatable.Count == 0 && wkCount == 0)
        {
            Term.OutLine($"{Dim}No project skills can be updated in place.{Reset}");
            PrintPinnedNotice(pinned);
            var gh = GhInstalled.ReportGhSkills(false, localLock.Skills, o.Skills);
            PrintLegacyProjectSkills(legacy);
            return (ok, fail, total + gh);
        }

        if (!o.DryRun)
        {
            var cwd = Sys.Cwd();
            var targets = new List<string>();
            var hasUniversal = false;
            foreach (var a in Agents.List)
            {
                if (Agents.IsUniversalAgent(a.Name))
                {
                    if (!hasUniversal && Fs.Exists(NodePath.Join(cwd, ".agents"))) hasUniversal = true;
                }
                else if (Fs.Exists(NodePath.Join(cwd, a.SkillsDir.Split('/')[0])))
                {
                    targets.Add(a.DisplayName);
                }
            }
            var parts = new List<string>();
            if (hasUniversal) parts.Add("Universal");
            parts.AddRange(targets);
            if (parts.Count > 0) Term.OutLine($"{Text}Updating for: {string.Join(", ", parts)}{Reset}");
            Term.OutLine($"{Text}Refreshing {updatable.Count + wkCount} skill(s)…{Reset}");
            Term.OutLine();
        }

        var wk = ProcessWellKnownUpdates(wkGroups, false, o);
        ok += wk.Ok;
        fail += wk.Fail;
        var pendingUpdates = new List<(string Name, string Source)>(wk.Pending);

        var bySource = new List<(string Key, List<ProjectSkill> Items)>();
        foreach (var sk in updatable) AddToGroup(bySource, $"{SourceKey(sk.Entry)}\n{S(sk.Entry, "ref")}", sk);

        foreach (var (_, group) in bySource)
        {
            var first = group[0].Entry;
            var source = SourceKey(first);
            var cloneSource = UpdateSource.BuildLocalCloneSource(UpdateSourceEntry.FromJson(first));
            var r = S(first, "ref");
            var lockedForSource = lockSkills.Where(kv => SourceKey(kv.Value) == source && S(kv.Value, "ref") == r).Select(kv => kv.Key).ToList();

            if (cloneSource == null)
            {
                fail += group.Count;
                Term.OutLine($"{Dim}✗ Cannot update {source}: skills-lock.json is missing sourceUrl for this generic Git source{Reset}");
                continue;
            }

            Resolution res;
            string? temp = null;
            try
            {
                temp = Git.CloneRepo(cloneSource, r);
                res = CheckAndPromptForDeletions(source, lockedForSource, lockSkills, false, o, DiscoveredLocations(temp));
            }
            catch (Exception e) when (IsCheckFailure(e))
            {
                Git.TryCleanup(temp);
                Term.OutLine($"{Dim}✗ Failed to check for deleted skills from {source}{Reset}");
                fail += group.Count;
                continue;
            }
            Git.TryCleanup(temp);

            foreach (var sk in group.Where(x => !res.Deleted.Contains(x.Name)))
            {
                var safe = Sanitize.Metadata(sk.Name);
                if (!res.Resolved.TryGetValue(sk.Name, out var resolved)) continue;
                if (o.DryRun)
                {
                    pendingUpdates.Add((sk.Name, S(sk.Entry, "source") ?? ""));
                    continue;
                }
                var entry = sk.Entry.DeepClone();
                entry["skillPath"] = resolved;
                Term.OutLine($"{Text}Updating {safe}…{Reset}");
                // `--force` on a pinned skill: reinstall at its ref and keep it pinned.
                var pin = Pinning.PinnedRef(entry);
                var useEntry = UpdateSourceEntry.FromJson(entry);
                if (pin != null) useEntry = useEntry with { Ref = null };
                if (UpdateSource.BuildLocalUpdateSource(useEntry) is not { } installUrl)
                {
                    fail++;
                    Term.OutLine($"  {Dim}✗ Cannot update {safe}: skills-lock.json is missing sourceUrl for this generic Git source{Reset}");
                    continue;
                }
                var args = new List<string> { "add", installUrl, "--skill", sk.Name };
                if (pin != null)
                {
                    args.Add("--pin");
                    args.Add(pin);
                }
                AddSubagentArgs(args, Json.StringArray(sk.Entry, "subagents"));
                if (UpdateSource.ShouldUseFullDepthForUpdate(useEntry)) args.Add("--full-depth");
                args.Add("-y");
                if (SpawnAdd(args, useEntry.SourceType == "github"))
                {
                    ok++;
                    Term.OutLine($"  {Text}✓{Reset} Updated {safe}");
                }
                else
                {
                    fail++;
                    Term.OutLine($"  {Dim}✗ Failed to update {safe}{Reset}");
                }
            }
        }

        if (o.DryRun)
        {
            if (pendingUpdates.Count == 0) Term.OutLine($"{Text}✓ All project skills are up to date{Reset}");
            else PrintDryRunUpdates("project", pendingUpdates);
        }
        PrintPinnedNotice(pinned);
        var ghCount = GhInstalled.ReportGhSkills(false, localLock.Skills, o.Skills);
        PrintLegacyProjectSkills(legacy);
        return (ok, fail, total + ghCount);
    }

    public static void Run(IReadOnlyList<string> args)
    {
        var o = ParseOptions(args);
        var scope = ResolveScope(o);
        Term.OutLine(o.Skills != null ? $"{Text}Updating {string.Join(", ", o.Skills)}…{Reset}" : $"{Text}Checking for skill updates…{Reset}");
        Term.OutLine();

        int totalOk = 0, totalFail = 0, totalFound = 0;
        var headers = scope == UpdateScope.Both && o.Skills == null;
        if (scope is UpdateScope.Global or UpdateScope.Both)
        {
            if (headers) Term.OutLine($"{Bold}Global Skills{Reset}");
            var (a, b, c) = UpdateGlobalSkills(o);
            totalOk += a;
            totalFail += b;
            totalFound += c;
            if (headers) Term.OutLine();
        }
        if (scope is UpdateScope.Project or UpdateScope.Both)
        {
            if (headers) Term.OutLine($"{Bold}Project Skills{Reset}");
            var (a, b, c) = UpdateProjectSkills(o);
            totalOk += a;
            totalFail += b;
            totalFound += c;
        }

        if (o.Skills != null && totalFound == 0) Term.OutLine($"{Dim}No installed skills found matching: {string.Join(", ", o.Skills)}{Reset}");
        Term.OutLine();
        if (totalOk > 0) Term.OutLine($"{Text}✓ Updated {totalOk} skill(s){Reset}");
        if (totalFail > 0)
        {
            Term.OutLine($"{Dim}Failed to update {totalFail} skill(s){Reset}");
            Term.ExitCode = 1;
        }
        Term.OutLine();
    }
}
