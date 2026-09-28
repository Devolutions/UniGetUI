// Non-interactive install pipeline behind the public API: the `skills add`
// flow (add.ts) without prompts, terminal output or process exit. Source
// resolution, installation and lock-file records reuse the CLI's core, so a
// skill installed here is indistinguishable from `skills add -y`.

using Devolutions.AgentSkills;

namespace Skills;

/// A source fetched and scanned for skills. Disposing removes its temp directory.
internal sealed class ResolvedSource : IDisposable
{
    public required ParsedSource Parsed { get; init; }

    /// Direct SKILL.md / archive download (no lock `source` is recorded).
    public bool DirectDownload { get; init; }

    public BlobInstallResult? Blob { get; init; }

    public string? TempDir { get; init; }

    public List<Skill> Skills { get; init; } = [];

    /// Set instead of <see cref="Skills"/> when a well-known index resolved.
    public List<WellKnownSkill>? WellKnownSkills { get; init; }

    /// For a Notion source, where each skill (by its directory) came from.
    public Dictionary<string, NotionOrigin>? Notion { get; init; }

    public void Dispose() => Git.TryCleanup(TempDir);
}

internal static class InstallEngine
{
    private const string NoSkills = "No valid skills found. Skills require a SKILL.md with name and description.";

    private static bool IsWildcard(IReadOnlyCollection<string>? list) => list?.Contains("*") == true;

    /// A Notion database or skill page, read through the Notion CLI. Notion
    /// packs (`notion`) stay with the interactive CLI, which lets the user pick them.
    private static string? NotionId(string source)
    {
        if (NotionFeed.ParseId(source) is { } id) return id;
        if (source.Trim().Equals("notion", StringComparison.OrdinalIgnoreCase))
            throw new SkillsException("Notion packs need the interactive skills CLI (`skills add notion`). Pass the address of a Notion skills database or skill page instead.");
        if (NotionFeed.IsNotionUrl(source))
            throw new SkillsException("This Notion address names no page or database. Use the address of a Notion skills database or skill page, over HTTPS.");
        return null;
    }

    /// Parse, fetch and scan a source. `requested` narrows discovery the way
    /// `--skill` does (internal skills become visible when named).
    public static ResolvedSource Resolve(string source, IReadOnlyList<string>? requested, bool fullDepth, Action<string> log)
    {
        if (NotionId(source) is { } notionId)
            return NotionFeed.Resolve(notionId, requested?.Where(n => n.Length > 0).ToList() ?? [], log);
        ParsedSource parsed;
        try
        {
            parsed = SourceParser.Parse(source);
        }
        catch (SourceParseException e)
        {
            throw new SkillsException(e.Message, e);
        }

        var names = requested?.Where(n => n.Length > 0).ToList() ?? [];
        var includeInternal = names.Count > 0 && !IsWildcard(names);
        var directDownload = parsed.Kind == "download";

        if (parsed.Kind == "well-known")
        {
            log("Discovering skills from well-known endpoint…");
            List<WellKnownSkill> wk;
            try
            {
                wk = WellKnown.FetchAllSkills(parsed.Url, includeInternal);
            }
            catch (ScopeNotFoundException e)
            {
                throw new SkillsException(e.Message, e);
            }
            if (wk.Count > 0)
            {
                log($"Found {wk.Count} skill{(wk.Count == 1 ? "" : "s")}");
                return new ResolvedSource { Parsed = parsed, WellKnownSkills = wk };
            }
            log("No well-known skills found; trying direct download…");
            directDownload = true;
        }

        if (parsed.SkillFilter is { } filter && !names.Contains(filter)) names.Add(filter);
        includeInternal = names.Count > 0 && !IsWildcard(names);
        var discover = new DiscoverOptions(includeInternal, fullDepth);

        string? temp = null;
        BlobInstallResult? blob = null;
        List<Skill> skills;
        try
        {
            if (parsed.Kind == "local")
            {
                var local = parsed.LocalPath ?? "";
                if (!Fs.Exists(local)) throw new SkillsException($"Local path does not exist: {local}");
                skills = SkillDiscovery.Discover(local, parsed.Subpath, discover);
            }
            else if (parsed.Kind is "well-known" or "download")
            {
                log("Downloading source…");
                var downloaded = DownloadSource.Fetch(parsed.Url);
                temp = downloaded.TempDir;
                skills = SkillDiscovery.Discover(downloaded.RootDir, parsed.Subpath, discover);
            }
            else
            {
                if (parsed.Kind == "github" && !fullDepth && SourceParser.GetOwnerRepo(parsed) is { } ownerRepo && InstallRecords.IsBlobEligible(ownerRepo))
                {
                    log("Fetching skills…");
                    blob = Blob.TryBlobInstall(ownerRepo, new BlobOptions(parsed.Subpath, parsed.SkillFilter, parsed.Ref, true, includeInternal));
                }
                if (blob != null)
                {
                    skills = blob.Skills;
                }
                else
                {
                    log($"Cloning {parsed.Url}…");
                    temp = Git.CloneRepo(parsed.Url, parsed.Ref);
                    skills = SkillDiscovery.Discover(temp, parsed.Subpath, discover);
                }
            }
        }
        catch (Exception e) when (e is GitCloneException or DiscoverException or DownloadException or ArchiveValidationException)
        {
            Git.TryCleanup(temp);
            throw new SkillsException(e is GitCloneException ? $"Failed to clone repository\n{e.Message}" : e.Message, e);
        }
        catch
        {
            Git.TryCleanup(temp);
            throw;
        }

        if (skills.Count == 0)
        {
            Git.TryCleanup(temp);
            throw new SkillsException(NoSkills);
        }
        log($"Found {skills.Count} skill{(skills.Count == 1 ? "" : "s")}");
        return new ResolvedSource { Parsed = parsed, DirectDownload = directDownload, Blob = blob, TempDir = temp, Skills = skills };
    }

    public static List<AvailableSkill> ListAvailable(string source, bool fullDepth, Action<string> log)
    {
        // Notion lists a database's skills without downloading them
        if (NotionId(source) is { } notionId) return NotionFeed.ListAvailable(notionId, log);
        using var src = Resolve(source, null, fullDepth, log);
        if (src.WellKnownSkills is { } wk) return wk.Select(s => new AvailableSkill(s.InstallName, s.Description, null)).ToList();
        return src.Skills.Select(s => new AvailableSkill(SkillDiscovery.DisplayName(s), s.Description, s.PluginName)).ToList();
    }

    /// <exception cref="ArgumentException">Unknown agent ids.</exception>
    public static void ValidateAgents(IReadOnlyList<string>? agents)
    {
        if (agents is not { Count: > 0 } || IsWildcard(agents)) return;
        var invalid = agents.Where(a => Agents.Find(a) == null).ToList();
        if (invalid.Count > 0)
            throw new ArgumentException($"Unknown agents: {string.Join(", ", invalid)}. Valid agents: {string.Join(", ", Agents.AllNames())}", nameof(agents));
    }

    /// Requested agents, or the detected agents plus the universal ones.
    private static List<string> ResolveTargetAgents(IReadOnlyList<string>? requested, bool global)
    {
        if (IsWildcard(requested)) return Agents.AllNames();
        if (requested is { Count: > 0 }) return requested.Distinct().ToList();
        var targets = Agents.DetectInstalledAgents().Where(a => a != "eve").ToList();
        foreach (var ua in Agents.GetUniversalAgents())
            if (!targets.Contains(ua)) targets.Add(ua);
        return global ? targets.Where(a => Agents.Get(a).GlobalSkillsDir != null).ToList() : targets;
    }

    private static SkillInstallMode PublicMode(InstallMode m) => m == InstallMode.Copy ? SkillInstallMode.Copy : SkillInstallMode.Symlink;

    private sealed record Target(string Agent, string? Subagent);

    private sealed record Attempt(string Skill, string Agent, InstallResult R);

    public static SkillInstallResult Install(SkillInstallRequest request, Action<string> log)
    {
        ValidateAgents(request.Agents);
        Installer.ResetPopulated();
        var cwd = Sys.Cwd();
        var global = request.Scope == SkillScope.Global;

        using var src = Resolve(request.Source, request.Skills, request.FullDepth, log);
        Sys.ThrowIfCancelled();

        var names = request.Skills?.Where(n => n.Length > 0).ToList() ?? [];
        if (src.Parsed.SkillFilter is { } filter && !names.Contains(filter)) names.Add(filter);
        var everything = names.Count == 0 || IsWildcard(names);

        var targetAgents = ResolveTargetAgents(request.Agents, global);
        var explicitlySelected = request.Agents is { Count: > 0 } && !IsWildcard(request.Agents) ? request.Agents.ToList() : [];
        List<string?> eveTargets = [null];
        if (request.EveSubagents is { Count: > 0 } subs)
        {
            explicitlySelected.Add("eve");
            if (!targetAgents.Contains("eve")) targetAgents.Add("eve");
            eveTargets = subs.Select(s => s is "root" or "." ? null : s).ToList();
        }
        if (targetAgents.Count == 0) throw new SkillsException("No agents to install to.");

        return src.WellKnownSkills is { } wk
            ? InstallWellKnown(src.Parsed.Url, wk, names, everything, targetAgents, request, global, cwd, log)
            : InstallSkills(src, names, everything, targetAgents, explicitlySelected, eveTargets, request, global, cwd, log);
    }

    private static List<T> Select<T>(List<T> all, List<string> names, bool everything, Func<IEnumerable<T>, string, bool> matches, List<SkillInstallOutcome> outcomes, SkillScope scope, Func<T, string> display)
    {
        if (everything) return all;
        var selected = all.Where(s => names.Any(n => matches([s], n))).ToList();
        foreach (var n in names)
            if (!matches(all, n))
                outcomes.Add(new SkillInstallOutcome { Name = n, Status = SkillOperationStatus.NotFound, Error = "No matching skill found in source", Scope = scope });
        if (selected.Count == 0)
            throw new SkillsException($"No matching skills found for: {string.Join(", ", names)}. Available skills: {string.Join(", ", all.Select(display))}");
        return selected;
    }

    private static SkillInstallResult InstallSkills(ResolvedSource src, List<string> names, bool everything, List<string> targetAgents,
        List<string> explicitlySelected, List<string?> eveTargets, SkillInstallRequest request, bool global, string cwd, Action<string> log)
    {
        var parsed = src.Parsed;
        var outcomes = new List<SkillInstallOutcome>();
        // A Notion skill also answers to its page title and the name derived from it
        var notion = src.Notion;
        Func<IEnumerable<Skill>, string, bool> matches = (skills, n) => SkillDiscovery.Filter(skills, [n]).Count > 0;
        if (notion is { } origins)
            matches = (skills, n) => skills.Any(s => SkillDiscovery.Filter([s], [n]).Count > 0 || (origins.TryGetValue(s.Path, out var o) && o.Page.Matches(n)));
        var selected = Select(src.Skills, names, everything, matches, outcomes, request.Scope, SkillDiscovery.DisplayName);

        var targets = new List<Target>();
        foreach (var a in targetAgents)
        {
            if (a == "eve") targets.AddRange(eveTargets.Select(s => new Target(a, s)));
            else targets.Add(new Target(a, null));
        }
        var mode = request.Mode == SkillInstallMode.Copy ? InstallMode.Copy : InstallMode.Symlink;
        var uniqueDirs = targets.Select(t => t.Subagent != null ? $"eve:subagent:{t.Subagent}" : Agents.Get(t.Agent).SkillsDir).Distinct().Count();
        if (uniqueDirs <= 1 || targets.All(t => t.Agent == "eve")) mode = InstallMode.Copy;

        var attempts = new List<Attempt>();
        foreach (var s in selected)
        {
            Sys.ThrowIfCancelled();
            var name = SkillDiscovery.DisplayName(s);
            log($"Installing {name}…");
            foreach (var t in targets)
            {
                var opts = new InstallOptions
                {
                    Global = global,
                    Mode = mode,
                    EveSubagent = t.Subagent,
                    CreateMissingAgentRoot = explicitlySelected.Contains(t.Agent),
                };
                var r = src.Blob != null && s.Blob is { } b
                    ? Installer.InstallBlobSkillForAgent(s.Name, b.Files, t.Agent, opts)
                    : Installer.InstallSkillForAgent(s, t.Agent, opts);
                if (!r.Success) log($"  Failed for {Agents.Get(t.Agent).DisplayName}: {r.Error}");
                attempts.Add(new Attempt(name, t.Agent, r));
            }
        }

        var okNames = attempts.Where(a => a.R.Success).Select(a => a.Skill).ToHashSet();
        var skillFiles = InstallRecords.ComputeSkillFiles(selected, src.Blob, src.TempDir);
        TrackInstall(parsed, src.DirectDownload, selected, targetAgents, global, skillFiles);
        var eveSubagents = targetAgents.Contains("eve") ? eveTargets.Select(s => s ?? "").ToList() : null;
        var hashes = InstallRecords.RecordInstalledSkills(parsed, src.DirectDownload, src.Blob, src.TempDir, selected, skillFiles, okNames,
            global, true, eveSubagents, cwd);
        if (notion != null)
        {
            var installed = selected
                .Where(s => okNames.Contains(SkillDiscovery.DisplayName(s)) && notion.ContainsKey(s.Path))
                .Select(s => (s.Name, notion[s.Path]))
                .ToList();
            InstallRecords.RecordNotionSkills(installed, global);
            // Notion's version id is what update checks compare
            foreach (var s in selected)
                if (notion.TryGetValue(s.Path, out var o)) hashes[SkillDiscovery.DisplayName(s)] = o.VersionId;
        }

        foreach (var s in selected)
        {
            var name = SkillDiscovery.DisplayName(s);
            outcomes.Add(Outcome(name, attempts.Where(a => a.Skill == name).ToList(), request.Scope, mode, hashes.GetValueOrDefault(name)));
        }
        return new SkillInstallResult(outcomes);
    }

    private static SkillInstallResult InstallWellKnown(string url, List<WellKnownSkill> skills, List<string> names, bool everything,
        List<string> targetAgents, SkillInstallRequest request, bool global, string cwd, Action<string> log)
    {
        var outcomes = new List<SkillInstallOutcome>();
        static bool Matches(WellKnownSkill s, string n) =>
            s.InstallName.Equals(n, StringComparison.OrdinalIgnoreCase) || s.Name.Equals(n, StringComparison.OrdinalIgnoreCase);
        var selected = Select(skills, names, everything, (all, n) => all.Any(s => Matches(s, n)), outcomes, request.Scope, s => s.InstallName);

        targetAgents = targetAgents.Where(a => a != "eve").ToList();
        var mode = request.Mode == SkillInstallMode.Copy ? InstallMode.Copy : InstallMode.Symlink;
        if (targetAgents.Select(a => Agents.Get(a).SkillsDir).Distinct().Count() <= 1) mode = InstallMode.Copy;

        var attempts = new List<Attempt>();
        foreach (var s in selected)
        {
            Sys.ThrowIfCancelled();
            log($"Installing {s.InstallName}…");
            foreach (var a in targetAgents)
            {
                var r = Installer.InstallWellKnownSkillForAgent(s.InstallName, s.Files, a, new InstallOptions { Global = global, Mode = mode });
                if (!r.Success) log($"  Failed for {Agents.Get(a).DisplayName}: {r.Error}");
                attempts.Add(new Attempt(s.InstallName, a, r));
            }
        }

        var okNames = attempts.Where(a => a.R.Success).Select(a => a.Skill).ToHashSet();
        var installedDirs = new Dictionary<string, string>();
        foreach (var a in attempts.Where(a => a.R.Success))
            if (!installedDirs.ContainsKey(a.Skill)) installedDirs[a.Skill] = !string.IsNullOrEmpty(a.R.CanonicalPath) ? a.R.CanonicalPath : a.R.Path;
        InstallRecords.RecordWellKnownSkills(url, selected.Where(s => okNames.Contains(s.InstallName)), installedDirs, global, cwd);
        TrackWellKnownInstall(url, selected, targetAgents, global);

        foreach (var s in selected)
            outcomes.Add(Outcome(s.InstallName, attempts.Where(a => a.Skill == s.InstallName).ToList(), request.Scope, mode, WellKnown.ComputeSkillDigest(s)));
        return new SkillInstallResult(outcomes);
    }

    private static SkillInstallOutcome Outcome(string name, List<Attempt> rs, SkillScope scope, InstallMode mode, string? hash)
    {
        var failed = rs.FirstOrDefault(r => !r.R.Success);
        return new SkillInstallOutcome
        {
            Name = name,
            Status = failed == null ? SkillOperationStatus.Succeeded : SkillOperationStatus.Failed,
            Error = failed == null ? null : failed.R.Error ?? "Installation failed",
            Path = rs.FirstOrDefault(r => r.R.Success) is { } ok ? ok.R.CanonicalPath ?? ok.R.Path : null,
            Scope = scope,
            Agents = rs.Where(r => r.R.Success && !r.R.Skipped).Select(r => r.Agent).Distinct().ToList(),
            SkippedAgents = rs.Where(r => r.R.Skipped && r.R.SkipReason == "missing-agent-project-directory").Select(r => r.Agent).Distinct().ToList(),
            Mode = PublicMode(rs.Count > 0 ? rs[0].R.Mode : mode),
            Hash = hash,
        };
    }

    /// The CLI's install event, only when the host opted into telemetry.
    private static void TrackInstall(ParsedSource parsed, bool directDownload, List<Skill> selected, List<string> targetAgents, bool global, System.Text.Json.Nodes.JsonObject skillFiles)
    {
        if (!Telemetry.Enabled || InstallRecords.GetLockSources(parsed, directDownload).Normalized is not { } normalized) return;
        var ownerRepo = SourceParser.ParseOwnerRepo(normalized);
        bool? isPrivate = parsed.Kind == "github" && ownerRepo is var (o, r) ? SourceParser.IsRepoPrivate(o, r) : null;
        if (ownerRepo != null && isPrivate != false) return;
        Telemetry.Track(
            ("event", "install"),
            ("source", normalized),
            ("skills", string.Join(",", selected.Select(s => s.Name))),
            ("agents", string.Join(",", targetAgents)),
            ("global", global ? "1" : null),
            ("skillFiles", Json.Stringify(skillFiles, 0)));
    }

    private static void TrackWellKnownInstall(string url, List<WellKnownSkill> selected, List<string> targetAgents, bool global)
    {
        if (!Telemetry.Enabled) return;
        var sourceIdentifier = WellKnown.GetSourceIdentifier(url);
        var isPrivate = SourceParser.ParseOwnerRepo(sourceIdentifier) is var (o, r) ? SourceParser.IsRepoPrivate(o, r) : false;
        if (isPrivate == true) return;
        var skillFiles = new System.Text.Json.Nodes.JsonObject();
        foreach (var s in selected) skillFiles[s.InstallName] = s.SourceUrl;
        Telemetry.Track(
            ("event", "install"),
            ("source", sourceIdentifier),
            ("skills", string.Join(",", selected.Select(s => s.InstallName))),
            ("agents", string.Join(",", targetAgents)),
            ("global", global ? "1" : null),
            ("skillFiles", Json.Stringify(skillFiles, 0)),
            ("installUrl", url),
            ("sourceType", "well-known"));
    }
}
