// `skills experimental_sync` — crawl node_modules for skills (port of sync.ts).

using System.Text.Json.Nodes;

namespace Skills;

internal sealed class SyncOptions
{
    public List<string>? Agent { get; set; }
    public bool Yes { get; set; }
    public bool Force { get; set; }
}

internal static class SyncCommand
{
    public static string ShortenPath(string full, string cwd)
    {
        var home = Sys.HomeDir();
        if (full == home || full.StartsWith(home + NodePath.Sep, StringComparison.Ordinal)) return "~" + full[home.Length..];
        if (full == cwd || full.StartsWith(cwd + NodePath.Sep, StringComparison.Ordinal)) return "." + full[cwd.Length..];
        return full;
    }

    private static List<string>? ListNames(string dir)
    {
        try
        {
            var names = new DirectoryInfo(dir).EnumerateFileSystemInfos().Select(i => i.Name).ToList();
            Fs.SortNamesLikeReaddir(names);
            return names;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// Skills in node_modules packages (top-level and scoped), tagged with the
    /// package they came from.
    private static List<(Skill Skill, string Package)> DiscoverNodeModuleSkills(string cwd)
    {
        var nm = NodePath.Join(cwd, "node_modules");
        var output = new List<(Skill, string)>();
        if (ListNames(nm) is not { } top) return output;

        void Process(string pkgDir, string pkg)
        {
            var rootMd = NodePath.Join(pkgDir, "SKILL.md");
            if (Fs.Exists(rootMd) && SkillDiscovery.ParseSkillMd(rootMd, false) is { } root)
            {
                output.Add((root, pkg));
                return;
            }
            foreach (var search in new[] { pkgDir, NodePath.Join(pkgDir, "skills"), NodePath.Join(pkgDir, ".agents", "skills") })
            {
                if (ListNames(search) is not { } names) continue;
                foreach (var name in names)
                {
                    var skillDir = NodePath.Join(search, name);
                    if (!Fs.IsDir(skillDir)) continue;
                    var md = NodePath.Join(skillDir, "SKILL.md");
                    if (!Fs.Exists(md)) continue;
                    if (SkillDiscovery.ParseSkillMd(md, false) is { } s) output.Add((s, pkg));
                }
            }
        }

        foreach (var name in top)
        {
            if (name.StartsWith('.')) continue;
            var full = NodePath.Join(nm, name);
            if (!Fs.IsDir(full)) continue;
            if (name.StartsWith('@'))
            {
                if (ListNames(full) is not { } scoped) continue;
                foreach (var sn in scoped)
                {
                    var sp = NodePath.Join(full, sn);
                    if (Fs.IsDir(sp)) Process(sp, $"{name}/{sn}");
                }
            }
            else
            {
                Process(full, name);
            }
        }
        return output;
    }

    private static string? TryHash(string path)
    {
        try
        {
            return LocalLock.ComputeSkillFolderHash(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed record Result(string Skill, string Agent, bool Success, string? Canonical, string? Error);

    private static List<T> OrCancel<T>(List<T>? v)
    {
        if (v != null) return v;
        Ui.Cancel("Sync cancelled");
        Term.Exit(0);
        return null!;
    }

    public static void Run(SyncOptions options)
    {
        Installer.ResetPopulated();
        var cwd = Sys.Cwd();
        var agentResult = DetectAgent.Detect();
        if (agentResult.IsAgent)
        {
            options.Yes = true;
            if (options.Agent is not { Count: > 0 } && DetectAgent.GetAgentType(agentResult.Name) is { } mapped)
            {
                var list = new List<string> { mapped };
                foreach (var ua in Agents.GetUniversalAgents())
                    if (!list.Contains(ua)) list.Add(ua);
                options.Agent = list;
            }
        }

        Term.OutLine();
        if (!agentResult.IsAgent) Ui.Intro(Pc.BgCyan(Pc.Black(" skills experimental_sync ")));
        else Ui.Log.Info($"{Pc.BgCyan(Pc.Black(Pc.Bold($" {agentResult.Name} ")))} Agent detected — installing non-interactively");

        var spinner = new Ui.Spinner();
        spinner.Start("Scanning node_modules for skills…");
        var discovered = DiscoverNodeModuleSkills(cwd);
        if (discovered.Count == 0)
        {
            spinner.Stop(Pc.Yellow("No skills found"));
            Ui.Outro(Pc.Dim("No SKILL.md files found in node_modules."));
            return;
        }
        spinner.Stop($"Found {Pc.Green(discovered.Count.ToString())} skill{(discovered.Count > 1 ? "s" : "")} in node_modules");

        foreach (var (s, pkg) in discovered)
        {
            Ui.Log.Info($"{Pc.Cyan(s.Name)} {Pc.Dim($"from {pkg}")}");
            if (s.Description.Length > 0) Ui.Log.Message(Pc.Dim($"  {s.Description}"));
        }

        var localLock = LocalLock.Read(cwd);
        var toInstall = new List<(Skill Skill, string Package)>();
        var upToDate = new List<string>();
        if (options.Force)
        {
            toInstall.AddRange(discovered);
            Ui.Log.Info(Pc.Dim("Force mode: reinstalling all skills"));
        }
        else
        {
            foreach (var item in discovered)
            {
                if (localLock.Skills[item.Skill.Name] is { } existing && TryHash(item.Skill.Path) is { } h && h == Json.Str(existing, "computedHash"))
                {
                    upToDate.Add(item.Skill.Name);
                    continue;
                }
                toInstall.Add(item);
            }
            if (upToDate.Count > 0) Ui.Log.Info(Pc.Dim($"{upToDate.Count} skill{(upToDate.Count != 1 ? "s" : "")} already up to date"));
            if (toInstall.Count == 0)
            {
                Term.OutLine();
                Ui.Outro(Pc.Green("All skills are up to date."));
                return;
            }
        }

        Ui.Log.Info($"{toInstall.Count} skill{(toInstall.Count != 1 ? "s" : "")} to install/update");

        var universal = Agents.GetUniversalAgents();
        var visibleUniversal = Agents.GetVisibleUniversalAgents();
        LockedSection<string> Locked() => new()
        {
            Title = "Universal (.agents/skills)",
            Items = visibleUniversal.Select(a => new SearchItem<string>(a, a, Agents.Get(a).DisplayName)).ToList(),
            HiddenCount = universal.Count - visibleUniversal.Count,
        };
        SearchItem<string> Item(string a) => new(a, a, Agents.Get(a).DisplayName) { Hint = Agents.Get(a).SkillsDir };

        List<string> targetAgents;
        if (options.Agent?.Contains("*") == true)
        {
            targetAgents = Agents.AllNames();
            Ui.Log.Info($"Installing to all {targetAgents.Count} agents");
        }
        else if (options.Agent is { Count: > 0 } list)
        {
            var invalid = list.Where(a => Agents.Find(a) == null).ToList();
            if (invalid.Count > 0)
            {
                Ui.Log.Error($"Invalid agents: {string.Join(", ", invalid)}");
                Ui.Log.Info($"Valid agents: {string.Join(", ", Agents.AllNames())}");
                Term.Exit(1);
            }
            targetAgents = list;
        }
        else
        {
            spinner.Start("Loading agents…");
            var installed = Agents.DetectInstalledAgents();
            spinner.Stop($"{Agents.List.Count} agents");
            if (installed.Count == 0)
            {
                if (options.Yes)
                {
                    Ui.Log.Info("Installing to universal agents");
                    targetAgents = universal;
                }
                else
                {
                    targetAgents = OrCancel(SearchMultiselect.Run(new SearchMultiselectOptions<string>
                    {
                        Message = "Which agents do you want to install to?",
                        Items = Agents.GetNonUniversalAgents().Select(Item).ToList(),
                        Locked = Locked(),
                    }));
                }
            }
            else if (installed.Count == 1 || options.Yes)
            {
                targetAgents = [.. installed];
                foreach (var ua in universal)
                    if (!targetAgents.Contains(ua)) targetAgents.Add(ua);
            }
            else
            {
                var others = Agents.GetNonUniversalAgents().Where(installed.Contains).ToList();
                var initial = installed.Where(a => !universal.Contains(a)).Select(a => others.IndexOf(a)).Where(i => i >= 0).ToList();
                targetAgents = OrCancel(SearchMultiselect.Run(new SearchMultiselectOptions<string>
                {
                    Message = "Which agents do you want to install to?",
                    Items = others.Select(Item).ToList(),
                    InitialSelected = initial,
                    Locked = Locked(),
                }));
            }
        }

        var summary = new List<string>();
        foreach (var (s, pkg) in toInstall)
        {
            string canonical;
            try
            {
                canonical = Installer.GetCanonicalPath(s.Name, false);
            }
            catch (InvalidOperationException)
            {
                canonical = "";
            }
            summary.Add($"{Pc.Cyan(s.Name)} {Pc.Dim($"← {pkg}")}");
            summary.Add($"  {Pc.Dim(ShortenPath(canonical, cwd))}");
        }
        Term.OutLine();
        Ui.Note(string.Join("\n", summary), "Sync Summary");

        if (!options.Yes && Ui.Confirm("Proceed with sync?") != true)
        {
            Ui.Cancel("Sync cancelled");
            Term.Exit(0);
        }

        spinner.Start("Syncing skills…");
        var results = new List<Result>();
        foreach (var (s, _) in toInstall)
        {
            foreach (var at in targetAgents)
            {
                var r = Installer.InstallSkillForAgent(s, at, new InstallOptions { Global = false, Cwd = cwd, Mode = InstallMode.Symlink });
                results.Add(new Result(s.Name, Agents.Get(at).DisplayName, r.Success, r.CanonicalPath, r.Error));
            }
        }
        spinner.Stop("Sync complete");

        var successfulNames = results.Where(r => r.Success).Select(r => r.Skill).Distinct().ToList();

        foreach (var (s, pkg) in toInstall)
        {
            if (!successfulNames.Contains(s.Name) || TryHash(s.Path) is not { } h) continue;
            try
            {
                LocalLock.AddSkill(s.Name, new JsonObject { ["source"] = pkg, ["sourceType"] = "node_modules", ["computedHash"] = h }, cwd);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // ignored, as in the TS implementation's best-effort lock update
            }
        }

        Term.OutLine();
        if (successfulNames.Count > 0)
        {
            var lines = new List<string>();
            foreach (var name in successfulNames)
            {
                var first = results.First(r => r.Success && r.Skill == name);
                var pkg = toInstall.FirstOrDefault(t => t.Skill.Name == name).Package ?? "";
                lines.Add($"{Pc.Green("✓")} {name} {Pc.Dim($"← {pkg}")}");
                if (first.Canonical != null) lines.Add($"  {Pc.Dim(ShortenPath(first.Canonical, cwd))}");
            }
            var n = successfulNames.Count;
            Ui.Note(string.Join("\n", lines), Pc.Green($"Synced {n} skill{(n != 1 ? "s" : "")}"));
        }
        var failed = results.Where(r => !r.Success).ToList();
        if (failed.Count > 0)
        {
            Term.OutLine();
            Ui.Log.Error(Pc.Red($"Failed to install {failed.Count}"));
            foreach (var r in failed) Ui.Log.Message($"  {Pc.Red("✗")} {r.Skill} → {r.Agent}: {Pc.Dim(r.Error ?? "undefined")}");
        }

        Telemetry.Track(
            ("event", "experimental_sync"),
            ("skillCount", toInstall.Count.ToString()),
            ("successCount", successfulNames.Count.ToString()),
            ("agents", string.Join(",", targetAgents)));

        Term.OutLine();
        Ui.Outro($"{Pc.Green("Done!")}{Pc.Dim("  Review skills before use; they run with full agent permissions.")}");
    }

    public static SyncOptions ParseOptions(IReadOnlyList<string> args)
    {
        var o = new SyncOptions();
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "-y" or "--yes":
                    o.Yes = true;
                    break;
                case "-f" or "--force":
                    o.Force = true;
                    break;
                case "-a" or "--agent":
                    o.Agent ??= [];
                    while (i + 1 < args.Count && args[i + 1].Length > 0 && !args[i + 1].StartsWith('-')) o.Agent.Add(args[++i]);
                    break;
            }
        }
        return o;
    }
}
