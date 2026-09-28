// `skills add` main flow (port of add.ts runAdd).

using System.Text.Json.Nodes;

namespace Skills;

internal static partial class AddCommand
{
    // ─── JSON output state ───

    private sealed class JsonState
    {
        public readonly JsonArray Results = [];
        public bool Emitted;
    }

    private static JsonState? _jsonState;
    private static readonly object JsonLock = new();

    private static void JsonPush(JsonNode v)
    {
        lock (JsonLock) _jsonState?.Results.Add(v);
    }

    private static bool JsonIsEmpty()
    {
        lock (JsonLock) return _jsonState == null || _jsonState.Results.Count == 0;
    }

    private static void EmitJson()
    {
        lock (JsonLock)
        {
            if (_jsonState is not { Emitted: false } state) return;
            state.Emitted = true;
            Term.StdoutRedirected = false;
            Term.RealStdout(Json.Stringify(state.Results) + "\n");
        }
    }

    private sealed class AddFailure(string message) : Exception(message);

    private sealed class AddCtx(bool json)
    {
        public bool Json { get; } = json;
        public string? TempDir { get; set; }
        private bool _installTipShown;

        public void Cleanup()
        {
            var t = TempDir;
            TempDir = null;
            Git.TryCleanup(t);
        }

        /// Every exit path emits exactly one JSON array in json mode.
        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        public void Exit(int code, string? message)
        {
            if (Json)
            {
                if (message != null) Term.ErrLine(message);
                if (code != 0 && JsonIsEmpty()) JsonPush(new JsonObject { ["status"] = "failed", ["error"] = message ?? "Installation failed" });
                EmitJson();
            }
            Term.Exit(code);
        }

        [System.Diagnostics.CodeAnalysis.DoesNotReturn]
        public void Cancelled()
        {
            Cleanup();
            ExitInstallationCancelled();
        }

        public void ShowInstallTip()
        {
            if (_installTipShown) return;
            Ui.Log.Message(Pc.Dim("Tip: use the --yes (-y) and --global (-g) flags to install without prompts."));
            _installTipShown = true;
        }
    }

    private static string KebabToTitle(string s) => ListCommand.KebabToTitle(s);

    private static (List<(string Group, List<T> Items)> Groups, List<T> Ungrouped) GroupByPlugin<T>(IEnumerable<T> items, Func<T, string?> plugin)
    {
        var groups = new List<(string Group, List<T> Items)>();
        var ungrouped = new List<T>();
        foreach (var it in items)
        {
            if (plugin(it) is { Length: > 0 } g)
            {
                var i = groups.FindIndex(x => x.Group == g);
                if (i >= 0) groups[i].Items.Add(it);
                else groups.Add((g, [it]));
            }
            else
            {
                ungrouped.Add(it);
            }
        }
        return (Collate.StableSort(groups, (a, b) => string.CompareOrdinal(a.Group, b.Group)), ungrouped);
    }

    public static void Run(List<string> args, AddOptions options)
    {
        Installer.ResetPopulated();
        var source = args.FirstOrDefault();
        var jsonMode = options.Json;
        var ctx = new AddCtx(jsonMode);

        if (jsonMode)
        {
            lock (JsonLock) _jsonState = new JsonState();
            Term.StdoutRedirected = true;
            Term.OnExit(EmitJson);
        }

        if (source == null)
        {
            Term.OutLine();
            Term.OutLine($"{Pc.BgRed(Pc.White(Pc.Bold(" ERROR ")))} {Pc.Red("Missing required argument: source")}");
            Term.OutLine();
            Term.OutLine(Pc.Dim("  Usage:"));
            Term.OutLine($"    {Pc.Cyan("skills add")} {Pc.Yellow("<source>")} {Pc.Dim("[options]")}");
            Term.OutLine();
            Term.OutLine(Pc.Dim("  Example:"));
            Term.OutLine($"    {Pc.Cyan("skills add")} {Pc.Yellow("vercel-labs/agent-skills")}");
            Term.OutLine();
            ctx.Exit(1, "Missing required argument: source");
        }

        var explicitlySelected = options.Agent is { } ag && !ag.Contains("*") ? new List<string>(ag) : [];

        if (options.All)
        {
            options.Skill = ["*"];
            options.Agent = ["*"];
            options.Yes = true;
        }

        var agentResult = DetectAgent.Detect();
        if (agentResult.IsAgent)
        {
            options.Yes = true;
            if (options.Agent is not { Count: > 0 } && DetectAgent.GetAgentType(agentResult.Name) is { } mapped)
                options.Agent = EnsureUniversalAgents([mapped]);
        }

        if (jsonMode && !options.Yes) ctx.Exit(1, "The --json flag requires --yes (or --all) to run non-interactively.");
        if (jsonMode && options.List) ctx.Exit(1, "The --json flag cannot be combined with --list.");

        Term.OutLine();
        if (!agentResult.IsAgent) Ui.Intro(Pc.BgCyan(Pc.Black(" skills ")));
        if (agentResult.IsAgent) Ui.Log.Info($"{Pc.BgCyan(Pc.Black(Pc.Bold($" {agentResult.Name} ")))} Agent detected — installing non-interactively");
        else if (!Term.StdinIsTty()) ctx.ShowInstallTip();

        try
        {
            RunInner(source, options, ctx, explicitlySelected, agentResult.IsAgent);
        }
        catch (Exception e) when (e is GitCloneException or AddFailure or SourceParseException or DiscoverException or DownloadException
                                      or ArchiveValidationException or NotionException)
        {
            string msg;
            if (e is GitCloneException ce)
            {
                Ui.Log.Error(Pc.Red("Failed to clone repository"));
                foreach (var line in ce.Message.Split('\n')) Ui.Log.Message(Pc.Dim(line));
                msg = $"Failed to clone repository\n{ce.Message}";
            }
            else
            {
                Ui.Log.Error(e.Message);
                msg = e.Message;
            }
            ctx.ShowInstallTip();
            Ui.Outro(Pc.Red("Installation failed"));
            ctx.Cleanup();
            ctx.Exit(1, msg);
        }
        if (jsonMode)
        {
            Term.ClearExitHooks();
            Term.StdoutRedirected = false;
        }
        ctx.Cleanup();
    }

    private static void RunInner(string source, AddOptions options, AddCtx ctx, List<string> explicitlySelected, bool inAgent)
    {
        var jsonMode = ctx.Json;
        var effectiveSource = source;
        string? notionLabel = null;
        var notionPage = Notion.ParseSkillUrl(source);
        if (Notion.IsNotionSource(source) || notionPage != null)
        {
            var prepared = notionPage != null ? Notion.PrepareSkillSource(notionPage) : Notion.PreparePackSource(options.Yes, options.List, options.Skill);
            if (prepared == null) return;
            effectiveSource = prepared.RootDir;
            ctx.TempDir = prepared.TempDir;
            notionLabel = prepared.PackCount is { } n ? $"{n} selected Notion pack{(n == 1 ? "" : "s")}" : "Notion page";
            options.Skill = ["*"];
        }

        var spinner = jsonMode ? Ui.Spinner.Inert() : new Ui.Spinner();
        spinner.Start("Parsing source…");
        var parsed = SourceParser.Parse(effectiveSource);
        // `--pin`: install from the pinned ref, as for `<source>#<ref>`. Errors are
        // reported after the Source line so the spinner is stopped first.
        string? pinError = null;
        if (options.Pin is { } pin)
        {
            try
            {
                parsed.Ref = ResolvePin(parsed, pin);
            }
            catch (AddFailure e)
            {
                pinError = e.Message;
            }
        }
        var directDownload = parsed.Kind == "download" || notionLabel != null;
        spinner.Stop(notionLabel != null
            ? $"Source: {notionLabel}"
            : $"Source: {(parsed.Kind == "local" ? parsed.LocalPath ?? "" : parsed.Url)}"
              + (parsed.Ref != null ? $" @ {Pc.Yellow(parsed.Ref)}" : "")
              + (parsed.Subpath != null ? $" ({parsed.Subpath})" : "")
              + (parsed.SkillFilter != null ? $" {Pc.Dim("@")}{Pc.Cyan(parsed.SkillFilter)}" : ""));
        if (pinError != null) throw new AddFailure(pinError);
        var pinned = options.Pin != null;

        var ownerRepoRaw = parsed.Kind is "well-known" or "download" ? null : SourceParser.GetOwnerRepo(parsed);
        var privacy = parsed.Kind == "github" && ownerRepoRaw != null && SourceParser.ParseOwnerRepo(ownerRepoRaw) is var (po, pr)
            ? Task.Run(() => SourceParser.IsRepoPrivate(po, pr))
            : Task.FromResult<bool?>(null);

        if (parsed.Kind == "well-known")
        {
            if (jsonMode) ctx.Exit(1, "--json is not yet supported for well-known skill sources.");
            if (HandleWellKnownSkills(parsed.Url, options, spinner)) return;
            directDownload = true;
        }

        if (parsed.SkillFilter is { } filter)
        {
            options.Skill ??= [];
            if (!options.Skill.Contains(filter)) options.Skill.Add(filter);
        }

        var includeInternal = options.Skill is { Count: > 0 } && !IsWildcard(options.Skill);
        var discoverOpts = new DiscoverOptions(includeInternal, options.FullDepth);

        BlobInstallResult? blobResult = null;
        List<Skill> skills;

        if (parsed.Kind == "local")
        {
            var local = parsed.LocalPath ?? "";
            spinner.Start("Validating local path…");
            if (!Fs.Exists(local))
            {
                spinner.Stop(Pc.Red("Path not found"));
                Ui.Outro(Pc.Red($"Local path does not exist: {local}"));
                ctx.Exit(1, $"Local path does not exist: {local}");
            }
            spinner.Stop("Local path validated");
            spinner.Start("Discovering skills…");
            skills = SkillDiscovery.Discover(local, parsed.Subpath, discoverOpts);
        }
        else if (parsed.Kind is "well-known" or "download")
        {
            spinner.Start("Downloading source...");
            var downloaded = DownloadSource.Fetch(parsed.Url);
            ctx.TempDir = downloaded.TempDir;
            spinner.Stop($"Downloaded {(downloaded.Kind == DownloadKind.SkillMd ? "SKILL.md file" : "archive")}");
            spinner.Start("Discovering skills...");
            skills = SkillDiscovery.Discover(downloaded.RootDir, parsed.Subpath, discoverOpts);
        }
        else if (parsed.Kind == "github" && !options.FullDepth)
        {
            var attempted = false;
            if (SourceParser.GetOwnerRepo(parsed) is { } or)
            {
                if (InstallRecords.IsBlobEligible(or))
                {
                    attempted = true;
                    spinner.Start("Fetching skills…");
                    blobResult = Blob.TryBlobInstall(or, new BlobOptions(parsed.Subpath, parsed.SkillFilter, parsed.Ref, true, includeInternal));
                }
            }
            if (blobResult != null)
            {
                skills = blobResult.Skills;
                spinner.Stop($"Found {Pc.Green(skills.Count.ToString())} skill{(skills.Count > 1 ? "s" : "")}");
            }
            else
            {
                if (attempted) spinner.Message("Cloning repository…");
                else spinner.Start("Cloning repository…");
                var t = Git.CloneRepo(parsed.Url, parsed.Ref);
                ctx.TempDir = t;
                spinner.Stop("Repository cloned");
                spinner.Start("Discovering skills…");
                skills = SkillDiscovery.Discover(t, parsed.Subpath, discoverOpts);
            }
        }
        else
        {
            spinner.Start("Cloning repository…");
            var t = Git.CloneRepo(parsed.Url, parsed.Ref);
            ctx.TempDir = t;
            spinner.Stop("Repository cloned");
            spinner.Start("Discovering skills…");
            skills = SkillDiscovery.Discover(t, parsed.Subpath, discoverOpts);
        }

        const string NoSkills = "No valid skills found. Skills require a SKILL.md with name and description.";
        if (skills.Count == 0)
        {
            spinner.Stop(Pc.Red("No skills found"));
            Ui.Outro(Pc.Red(NoSkills));
            ctx.Cleanup();
            ctx.Exit(1, NoSkills);
        }
        if (blobResult == null) spinner.Stop($"Found {Pc.Green(skills.Count.ToString())} skill{(skills.Count > 1 ? "s" : "")}");

        if (options.List)
        {
            Term.OutLine();
            Ui.Log.Step(Pc.Bold("Available Skills"));
            var (groups, ungrouped) = GroupByPlugin(skills, s => s.PluginName);
            foreach (var (g, list) in groups)
            {
                Term.OutLine(Pc.Bold(KebabToTitle(g)));
                foreach (var s in list)
                {
                    Ui.Log.Message($"  {Pc.Cyan(SkillDiscovery.DisplayName(s))}");
                    Ui.Log.Message($"    {Pc.Dim(s.Description)}");
                }
                Term.OutLine();
            }
            if (ungrouped.Count > 0)
            {
                if (groups.Count > 0) Term.OutLine(Pc.Bold("General"));
                foreach (var s in ungrouped)
                {
                    Ui.Log.Message($"  {Pc.Cyan(SkillDiscovery.DisplayName(s))}");
                    Ui.Log.Message($"    {Pc.Dim(s.Description)}");
                }
            }
            Term.OutLine();
            Ui.Outro("Use --skill <name> to install specific skills");
            ctx.Cleanup();
            ctx.Exit(0, null);
        }

        void LogChosen(List<Skill> chosen) => LogAutoSelectedSkills(chosen.Select(s => (SkillDiscovery.DisplayName(s), s.Description)).ToList());

        List<Skill> selected;
        if (IsWildcard(options.Skill))
        {
            LogChosen(skills);
            selected = skills;
        }
        else if (options.Skill is { Count: > 0 } names)
        {
            var sel = SkillDiscovery.Filter(skills, names);
            if (jsonMode)
            {
                foreach (var requested in names)
                    if (SkillDiscovery.Filter(skills, [requested]).Count == 0)
                        JsonPush(new JsonObject { ["name"] = requested, ["status"] = "skipped", ["reason"] = "No matching skill found in source" });
            }
            if (sel.Count == 0)
            {
                Ui.Log.Error($"No matching skills found for: {string.Join(", ", names)}");
                Ui.Log.Info("Available skills:");
                foreach (var s in skills) Ui.Log.Message($"  - {SkillDiscovery.DisplayName(s)}");
                ctx.Cleanup();
                ctx.Exit(1, $"No matching skills found for: {string.Join(", ", names)}");
            }
            Ui.Log.Info($"Selected {sel.Count} skill{Plural(sel.Count)}: {string.Join(", ", sel.Select(s => Pc.Cyan(SkillDiscovery.DisplayName(s))))}");
            selected = sel;
        }
        else if (skills.Count == 1 || options.Yes)
        {
            LogChosen(skills);
            selected = skills;
        }
        else
        {
            var sorted = Collate.StableSort(skills, (a, b) => (a.PluginName, b.PluginName) switch
            {
                ({ }, null) => -1,
                (null, { }) => 1,
                ({ } x, { } y) when x != y => Collate.LocaleCompare(x, y),
                _ => Collate.LocaleCompare(SkillDiscovery.DisplayName(a), SkillDiscovery.DisplayName(b)),
            });
            var hasGroups = sorted.Any(s => s.PluginName != null);
            var items = sorted.Select(s => new SearchItem<int>(skills.IndexOf(s), "[object Object]", SkillDiscovery.DisplayName(s))
            {
                Group = hasGroups ? s.PluginName != null ? KebabToTitle(s.PluginName) : "Other" : null,
                Detail = s.Description,
            }).ToList();
            var idx = SearchMultiselect.Run(new SearchMultiselectOptions<int>
            {
                Message = hasGroups ? $"Select skills to install {Pc.Dim("(space to toggle)")}" : "Select skills to install",
                Items = items,
                Required = true,
                MaxVisible = 20,
                Searchable = !hasGroups,
                ShowDetail = true,
                ShowSelectedSummary = false,
                SelectGroups = hasGroups,
                SelectAll = true,
            });
            if (idx == null) ctx.Cancelled();
            selected = idx.Select(i => skills[i]).ToList();
        }

        // Security audit only after GitHub positively confirmed the repo is public.
        var ownerRepoForAudit = SourceParser.GetOwnerRepo(parsed);
        var selectedNames = selected.Select(SkillDiscovery.DisplayName).ToList();
        var audit = ownerRepoForAudit != null
            ? Task.Run(() => privacy.Result == false ? Telemetry.FetchAuditData(ownerRepoForAudit, selectedNames) : null)
            : Task.FromResult<JsonObject?>(null);

        List<string> targetAgents;
        if (IsWildcard(options.Agent))
        {
            targetAgents = Agents.AllNames();
            Ui.Log.Info($"Installing to all {targetAgents.Count} agents");
        }
        else if (options.Agent is { Count: > 0 } agentList)
        {
            var invalid = agentList.Where(a => Agents.Find(a) == null).ToList();
            if (invalid.Count > 0)
            {
                Ui.Log.Error($"Invalid agents: {string.Join(", ", invalid)}");
                Ui.Log.Info($"Valid agents: {string.Join(", ", Agents.AllNames())}");
                ctx.Cleanup();
                ctx.Exit(1, $"Invalid agents: {string.Join(", ", invalid)}");
            }
            targetAgents = [.. agentList];
        }
        else
        {
            spinner.Start("Loading agents…");
            var installed = Agents.DetectInstalledAgents();
            spinner.Stop($"{Agents.List.Count} agents");
            if (installed.Contains("eve") && (options.Yes || !inAgent))
            {
                var useEve = options.Yes ? true : Ui.Confirm(FormatEveInstallPromptMessage(selected));
                if (useEve == null) ctx.Cancelled();
                if (useEve == true)
                {
                    if (!options.Yes) explicitlySelected.Add("eve");
                    Ui.Log.Info($"Installing to: {Pc.Cyan(EveAgentLabel)}");
                    targetAgents = ["eve"];
                }
                else
                {
                    var v = SelectAgentsInteractive(options.Global ?? false);
                    if (v == null) ctx.Cancelled();
                    explicitlySelected.AddRange(v);
                    targetAgents = v;
                }
            }
            else if (installed.Count == 0)
            {
                if (options.Yes)
                {
                    Ui.Log.Info("Installing to all agents");
                    targetAgents = Agents.AllNames();
                }
                else
                {
                    Ui.Log.Info("Select agents to install skills to");
                    var v = PromptForAgents("Which agents do you want to install to?", Agents.AllNames().Where(a => a != "eve").ToList());
                    if (v == null) ctx.Cancelled();
                    explicitlySelected.AddRange(v);
                    targetAgents = v;
                }
            }
            else if (installed.Count == 1 || options.Yes)
            {
                Ui.Log.Info($"Installing to: {string.Join(", ", installed.Select(a => Pc.Cyan(Agents.Get(a).DisplayName)))}");
                targetAgents = EnsureUniversalAgents(installed);
            }
            else
            {
                var v = SelectAgentsInteractive(options.Global ?? false);
                if (v == null) ctx.Cancelled();
                explicitlySelected.AddRange(v);
                targetAgents = v;
            }
        }

        if (options.Subagent is { Count: > 0 })
        {
            explicitlySelected.Add("eve");
            if (!targetAgents.Contains("eve")) targetAgents.Add("eve");
        }

        List<string?> eveTargets = [null];
        if (targetAgents.Contains("eve"))
        {
            var available = Agents.GetEveSubagents(Sys.Cwd());
            if (options.Subagent is { Count: > 0 } subs)
            {
                eveTargets = subs.Select(s => s is "root" or "." ? null : s).ToList();
            }
            else if (available.Count > 0 && !options.Yes)
            {
                var choices = new List<SelectOption<string>> { new("", "Root agent", "agent/skills") };
                choices.AddRange(available.Select(name => new SelectOption<string>(name, name, $"agent/subagents/{name}/skills")));
                var sel = Ui.Multiselect("Where should Eve skills be installed?", choices, [""], true);
                if (sel == null) ctx.Cancelled();
                eveTargets = sel.Select(s => s.Length == 0 ? null : s).ToList();
            }
        }

        var targets = BuildInstallTargets(targetAgents, eveTargets);

        var installGlobally = options.Global ?? false;
        var supportsGlobal = targetAgents.Any(a => Agents.Get(a).GlobalSkillsDir != null);
        if (options.Global == null && !options.Yes && supportsGlobal)
        {
            var scopeOptions = new List<SelectOption<bool>>
            {
                new(false, "Project", "Install in current directory (committed with your project)"),
                new(true, "Global", "Install in home directory (available across all projects)"),
            };
            if (!Ui.Select("Installation scope", scopeOptions, 0, out var g)) ctx.Cancelled();
            installGlobally = g;
        }

        var mode = options.Copy ? InstallMode.Copy : InstallMode.Symlink;
        var allEve = targets.All(t => t.Agent == "eve");
        var uniqueDirs = targets.Select(t => t.Subagent != null ? $"eve:subagent:{t.Subagent}" : Agents.Get(t.Agent).SkillsDir).Distinct().Count();
        if (!options.Copy && !options.Yes && uniqueDirs > 1 && !allEve)
        {
            if (SelectMode() is not { } m) ctx.Cancelled();
            else mode = m;
        }
        else if (uniqueDirs <= 1 || allEve)
        {
            mode = InstallMode.Copy;
        }

        var cwd = Sys.Cwd();
        var summary = new List<string>();
        var (sgroups, sungrouped) = GroupByPlugin(selected, s => s.PluginName);
        void PrintSummary(List<Skill> list)
        {
            foreach (var s in list)
            {
                if (summary.Count > 0) summary.Add("");
                var canonical = targets.Count == 1
                    ? SafeCanonicalPath(s.Name, installGlobally, null, targets[0].Agent, targets[0].Subagent)
                    : SafeCanonicalPath(s.Name, installGlobally);
                summary.Add(Pc.Cyan(ShortenPath(canonical, cwd)));
                summary.AddRange(BuildTargetSummaryLines(targets, mode));
                var overwrites = targets.Where(t => Installer.IsSkillInstalled(s.Name, t.Agent, installGlobally, null, t.Subagent)).Select(TargetDisplayName).ToList();
                if (overwrites.Count > 0) summary.Add($"  {Pc.Yellow("overwrites:")} {FormatList(overwrites, 5)}");
            }
        }
        foreach (var (g, list) in sgroups)
        {
            summary.Add("");
            summary.Add(Pc.Bold(KebabToTitle(g)));
            PrintSummary(list);
        }
        if (sungrouped.Count > 0)
        {
            if (sgroups.Count > 0)
            {
                summary.Add("");
                summary.Add(Pc.Bold("General"));
            }
            PrintSummary(sungrouped);
        }
        Term.OutLine();
        Ui.Note(string.Join("\n", summary), "Installation Summary");

        var auditData = audit.Result;
        if (auditData != null && ownerRepoForAudit != null)
        {
            var lines = BuildSecurityLines(auditData, selectedNames, ownerRepoForAudit);
            if (lines.Count > 0) Ui.Note(string.Join("\n", lines), "Security Risk Assessments");
        }

        if (!options.Yes && Ui.Confirm("Proceed with installation?") != true) ctx.Cancelled();

        spinner.Start("Installing skills…");
        var results = new List<AddResult>();
        foreach (var s in selected)
        {
            foreach (var t in targets)
            {
                var opts = new InstallOptions
                {
                    Global = installGlobally,
                    Mode = mode,
                    EveSubagent = t.Subagent,
                    CreateMissingAgentRoot = explicitlySelected.Contains(t.Agent),
                };
                var r = blobResult != null && s.Blob is { } b
                    ? Installer.InstallBlobSkillForAgent(s.Name, b.Files, t.Agent, opts)
                    : Installer.InstallSkillForAgent(s, t.Agent, opts);
                results.Add(new AddResult(SkillDiscovery.DisplayName(s), TargetDisplayName(t), s.PluginName, r));
            }
        }
        spinner.Stop("Installation complete");
        Term.OutLine();

        var successful = results.Where(r => r.R.Success).ToList();
        var failed = results.Where(r => !r.R.Success).ToList();
        var okNames = successful.Select(r => r.Skill).ToHashSet();

        var tempDir = ctx.TempDir;
        var skillFiles = InstallRecords.ComputeSkillFiles(selected, blobResult, tempDir);

        var normalizedSource = InstallRecords.GetLockSources(parsed, directDownload).Normalized;

        if (normalizedSource != null)
        {
            if (SourceParser.ParseOwnerRepo(normalizedSource) == null || privacy.Result == false)
            {
                Telemetry.Track(
                    ("event", "install"),
                    ("source", normalizedSource),
                    ("skills", string.Join(",", selected.Select(s => s.Name))),
                    ("agents", string.Join(",", targetAgents)),
                    ("global", installGlobally ? "1" : null),
                    ("skillFiles", Json.Stringify(skillFiles, 0)),
                    ("metadata", options.Metadata));
            }
        }

        var eveSubagents = targetAgents.Contains("eve") ? eveTargets.Select(s => s ?? "").ToList() : null;
        var hashes = InstallRecords.RecordInstalledSkills(parsed, directDownload, blobResult, tempDir, selected, skillFiles, okNames,
            installGlobally, jsonMode, eveSubagents, cwd, pinned);

        if (jsonMode)
        {
            var jsonSource = normalizedSource ?? (parsed.Kind == "local" ? parsed.LocalPath ?? "" : parsed.Url);
            foreach (var s in selected)
            {
                var name = SkillDiscovery.DisplayName(s);
                var rs = results.Where(r => r.Skill == name).ToList();
                var fails = rs.Where(r => !r.R.Success).ToList();
                if (fails.Count > 0)
                {
                    JsonPush(new JsonObject { ["name"] = name, ["status"] = "failed", ["error"] = fails[0].R.Error ?? "Installation failed" });
                    continue;
                }
                var o = new JsonObject
                {
                    ["name"] = name,
                    ["status"] = "installed",
                    ["source"] = jsonSource,
                    ["ref"] = parsed.Ref,
                };
                if (pinned) o["pinned"] = true;
                o["hash"] = hashes.GetValueOrDefault(name);
                if (rs.Count > 0) o["path"] = rs[0].R.CanonicalPath ?? rs[0].R.Path;
                o["scope"] = installGlobally ? "global" : "project";
                o["agents"] = new JsonArray(rs.Where(r => !r.R.Skipped).Select(r => (JsonNode?)r.Agent).ToArray());
                o["mode"] = Installer.ModeName(rs.Count > 0 ? rs[0].R.Mode : mode);
                o["security"] = BuildJsonSecurity(auditData, name, ownerRepoForAudit);
                JsonPush(o);
            }
            EmitJson();
            bool anySkipped;
            lock (JsonLock) anySkipped = _jsonState?.Results.Any(r => Json.Str(r, "status") == "skipped") == true;
            if (failed.Count > 0 || anySkipped) Term.ExitCode = 1;
            return;
        }

        if (successful.Count > 0) PrintInstalledNote(successful, targetAgents, cwd);
        PrintFailures(failed);

        Term.OutLine();
        Ui.Outro(DoneOutro());
        ctx.Cleanup();
        PromptForFindSkills(options, targetAgents);
    }

    private static void PrintInstalledNote(List<AddResult> successful, List<string> targetAgents, string cwd)
    {
        var bySkill = new List<(string Skill, List<AddResult> Results)>();
        foreach (var r in successful)
        {
            var i = bySkill.FindIndex(x => x.Skill == r.Skill);
            if (i >= 0) bySkill[i].Results.Add(r);
            else bySkill.Add((r.Skill, [r]));
        }
        var (rgroups, rungrouped) = GroupByPlugin(bySkill.Select(x => x.Results[0]), r => r.PluginName);
        var symlinkFailures = successful.Where(r => r.R.Mode == InstallMode.Symlink && r.R.SymlinkFailed).ToList();
        var lines = new List<string>();
        void PrintResults(List<AddResult> entries)
        {
            foreach (var entry in entries)
            {
                var rs = bySkill.First(x => x.Skill == entry.Skill).Results;
                var first = rs[0];
                if (first.R.Mode == InstallMode.Copy)
                {
                    lines.Add($"{Pc.Green("✓")} {entry.Skill} {Pc.Dim("(copied)")}");
                    var seen = new HashSet<string>();
                    foreach (var r in rs)
                    {
                        var sp = ShortenPath(r.R.Path, cwd);
                        if (seen.Add(sp)) lines.Add($"  {Pc.Dim("→")} {sp}");
                    }
                }
                else
                {
                    lines.Add(!string.IsNullOrEmpty(first.R.CanonicalPath)
                        ? $"{Pc.Green("✓")} {ShortenPath(first.R.CanonicalPath, cwd)}"
                        : $"{Pc.Green("✓")} {entry.Skill}");
                    lines.AddRange(BuildResultLines(rs, targetAgents));
                }
            }
        }
        foreach (var (g, entries) in rgroups)
        {
            lines.Add("");
            lines.Add(Pc.Bold(KebabToTitle(g)));
            PrintResults(entries);
        }
        if (rungrouped.Count > 0)
        {
            if (rgroups.Count > 0)
            {
                lines.Add("");
                lines.Add(Pc.Bold("General"));
            }
            PrintResults(rungrouped);
        }
        var n = bySkill.Count;
        Ui.Note(string.Join("\n", lines), Pc.Green($"Installed {n} skill{Plural(n)}"));
        if (symlinkFailures.Count > 0)
        {
            Ui.Log.Warn(Pc.Yellow($"Symlinks failed for: {FormatList(symlinkFailures.Select(r => r.Agent).ToList(), 5)}"));
            Ui.Log.Message(Pc.Dim("  Files were copied instead. On Windows, enable Developer Mode for symlink support."));
        }
    }

    /// One-time prompt to install the find-skills skill after an install.
    private static void PromptForFindSkills(AddOptions options, List<string> targetAgents)
    {
        if (!Term.StdinIsTty() || options.Yes) return;
        if (SkillLock.IsPromptDismissed("findSkillsPrompt")) return;
        static void Dismiss()
        {
            try
            {
                SkillLock.DismissPrompt("findSkillsPrompt");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // best effort
            }
        }
        if (Installer.IsSkillInstalled("find-skills", "claude-code", true))
        {
            Dismiss();
            return;
        }
        Term.OutLine();
        Ui.Log.Message(Pc.Dim("One-time prompt - you won't be asked again if you dismiss."));
        var answer = Ui.Confirm($"Install the {Pc.Cyan("find-skills")} skill? It helps your agent discover and suggest skills.");
        Dismiss();
        if (answer == true)
        {
            var agents = targetAgents.Where(a => a != "replit").ToList();
            if (agents.Count == 0) return;
            Term.OutLine();
            Ui.Log.Step("Installing find-skills skill…");
            Run(["vercel-labs/skills"], new AddOptions { Skill = ["find-skills"], Global = true, Yes = true, Agent = agents });
        }
        else if (answer == false)
        {
            Ui.Log.Message(Pc.Dim("You can install it later with: skills add vercel-labs/skills@find-skills"));
        }
    }

    /// The ref to install for `--pin <pin>`: the pin itself, or the newest
    /// release tag for `--pin latest`.
    /// <exception cref="AddFailure">An invalid pin or a failed release lookup.</exception>
    private static string ResolvePin(ParsedSource parsed, string pin)
    {
        if (Pinning.CheckPin(parsed.Kind, parsed.Ref, pin) is { } error) throw new AddFailure(error);
        if (pin != Pinning.PinLatest) return pin;
        var ownerRepo = SourceParser.GetOwnerRepo(parsed) ?? "";
        string? tag;
        try
        {
            tag = GitHubRelease.ResolveLatestRelease(ownerRepo);
        }
        catch (GitHubReleaseException e)
        {
            throw new AddFailure($"Could not resolve the latest release of {ownerRepo}: {e.Message}");
        }
        return tag ?? throw new AddFailure($"No releases found for {ownerRepo}. Pass a tag, branch or commit SHA to --pin.");
    }

    /// Parse `add` arguments. Returns (sources, options, errors).
    public static (List<string> Sources, AddOptions Options, List<string> Errors) ParseOptions(IReadOnlyList<string> args)
    {
        var o = new AddOptions();
        var source = new List<string>();
        var errors = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            void Collect(List<string> list)
            {
                while (i + 1 < args.Count && args[i + 1].Length > 0 && !args[i + 1].StartsWith('-')) list.Add(args[++i]);
            }
            var a = args[i];
            switch (a)
            {
                case "-g" or "--global":
                    o.Global = true;
                    break;
                case "-y" or "--yes":
                    o.Yes = true;
                    break;
                case "-l" or "--list":
                    o.List = true;
                    break;
                case "--all":
                    o.All = true;
                    break;
                case "-a" or "--agent":
                    Collect(o.Agent ??= []);
                    break;
                case "-s" or "--skill":
                    Collect(o.Skill ??= []);
                    break;
                case "--metadata":
                    i++;
                    if (i >= args.Count) errors.Add("--metadata requires a JSON value");
                    else if (Json.TryParse(args[i], out _)) o.Metadata = args[i];
                    else errors.Add("--metadata must be valid JSON");
                    break;
                case "--pin":
                    if (i + 1 < args.Count && args[i + 1].Length > 0 && !args[i + 1].StartsWith('-')) o.Pin = args[++i];
                    else errors.Add("--pin requires a ref");
                    break;
                case "--full-depth":
                    o.FullDepth = true;
                    break;
                case "--json":
                    o.Json = true;
                    break;
                case "--copy":
                    o.Copy = true;
                    break;
                case "--subagent":
                    Collect(o.Subagent ??= []);
                    break;
                default:
                    if (a.Length > 0 && !a.StartsWith('-')) source.Add(a);
                    break;
            }
        }
        return (source, o, errors);
    }
}
