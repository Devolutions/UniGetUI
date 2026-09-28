// `skills add` well-known discovery flow (port of add.ts handleWellKnownSkills).

using System.Text.Json.Nodes;

namespace Skills;

internal static partial class AddCommand
{
    /// Well-known discovery flow. Returns false to fall back to a direct download.
    private static bool HandleWellKnownSkills(string url, AddOptions options, Ui.Spinner spinner)
    {
        spinner.Start("Discovering skills from well-known endpoint...");
        var includeInternal = options.Skill is { Count: > 0 } && !IsWildcard(options.Skill);
        List<WellKnownSkill> skills;
        try
        {
            skills = WellKnown.FetchAllSkills(url, includeInternal);
        }
        catch (ScopeNotFoundException e)
        {
            spinner.Stop(Pc.Red("No matching skills"));
            Ui.Log.Error(e.Message);
            Term.Exit(1);
            return false;
        }
        if (skills.Count == 0)
        {
            spinner.Stop(Pc.Dim("No well-known skills found; trying direct download..."));
            return false;
        }
        spinner.Stop($"Found {Pc.Green(skills.Count.ToString())} skill{(skills.Count > 1 ? "s" : "")}");

        foreach (var s in skills)
        {
            Ui.Log.Info($"Skill: {Pc.Cyan(s.InstallName)}");
            Ui.Log.Message(Pc.Dim(s.Description));
            if (s.Files.Count > 1) Ui.Log.Message(Pc.Dim($"  Files: {string.Join(", ", s.Files.Select(f => f.Path))}"));
        }

        if (options.List)
        {
            Term.OutLine();
            Ui.Log.Step(Pc.Bold("Available Skills"));
            foreach (var s in skills)
            {
                Ui.Log.Message($"  {Pc.Cyan(s.InstallName)}");
                Ui.Log.Message($"    {Pc.Dim(s.Description)}");
                if (s.Files.Count > 1) Ui.Log.Message($"    {Pc.Dim($"Files: {s.Files.Count}")}");
            }
            Term.OutLine();
            Ui.Outro("Run without --list to install");
            Term.Exit(0);
        }

        static void LogChosen(List<WellKnownSkill> chosen) => LogAutoSelectedSkills(chosen.Select(s => (s.InstallName, s.Description)).ToList());

        List<WellKnownSkill> selected;
        if (IsWildcard(options.Skill))
        {
            selected = skills;
            LogChosen(selected);
        }
        else if (options.Skill is { Count: > 0 } names)
        {
            selected = skills.Where(s => names.Any(n => s.InstallName.ToLowerInvariant() == n.ToLowerInvariant() || s.Name.ToLowerInvariant() == n.ToLowerInvariant())).ToList();
            if (selected.Count == 0)
            {
                Ui.Log.Error($"No matching skills found for: {string.Join(", ", names)}");
                Ui.Log.Info("Available skills:");
                foreach (var s in skills) Ui.Log.Message($"  - {s.InstallName}");
                Term.Exit(1);
            }
        }
        else if (skills.Count == 1 || options.Yes)
        {
            selected = skills;
            LogChosen(selected);
        }
        else
        {
            var items = skills.Select((s, i) => new SearchItem<int>(i, "[object Object]", s.InstallName)
            {
                Hint = s.Description.Length > 60 ? $"{s.Description[..57]}…" : s.Description,
            }).ToList();
            var idx = OrCancelled(SearchMultiselect.Run(new SearchMultiselectOptions<int>
            {
                Message = "Select skills to install",
                Items = items,
                InitialSelected = IsSkillsShPackUrl(url) ? Enumerable.Range(0, skills.Count).ToList() : [],
                Required = true,
                MaxVisible = 20,
                SelectAll = true,
            }));
            selected = idx.Select(i => skills[i]).ToList();
        }

        var targetAgents = ResolveTargetAgents(options, spinner);

        var installGlobally = options.Global ?? false;
        var supportsGlobal = targetAgents.Any(a => Agents.Get(a).GlobalSkillsDir != null);
        if (options.Global == null && !options.Yes && supportsGlobal) installGlobally = SelectScope();

        var mode = options.Copy ? InstallMode.Copy : InstallMode.Symlink;
        var uniqueDirs = targetAgents.Select(a => Agents.Get(a).SkillsDir).Distinct().Count();
        if (!options.Copy && !options.Yes && uniqueDirs > 1)
        {
            if (SelectMode() is not { } m) ExitInstallationCancelled();
            else mode = m;
        }
        else if (uniqueDirs <= 1)
        {
            mode = InstallMode.Copy;
        }

        var cwd = Sys.Cwd();
        var summary = new List<string>();
        foreach (var s in selected)
        {
            if (summary.Count > 0) summary.Add("");
            summary.Add(Pc.Cyan(ShortenPath(SafeCanonicalPath(s.InstallName, installGlobally), cwd)));
            summary.AddRange(BuildAgentSummaryLines(targetAgents, mode));
            if (s.Files.Count > 1) summary.Add($"  {Pc.Dim("files:")} {s.Files.Count}");
            var overwrites = targetAgents.Where(a => Installer.IsSkillInstalled(s.InstallName, a, installGlobally)).Select(a => Agents.Get(a).DisplayName).ToList();
            if (overwrites.Count > 0) summary.Add($"  {Pc.Yellow("overwrites:")} {FormatList(overwrites, 5)}");
        }
        Term.OutLine();
        Ui.Note(string.Join("\n", summary), "Installation Summary");

        if (!options.Yes && Ui.Confirm("Proceed with installation?") != true) ExitInstallationCancelled();

        var sourceIdentifier = WellKnown.GetSourceIdentifier(url);
        var privacy = Task.Run(() => IsSourcePrivate(sourceIdentifier));

        spinner.Start("Installing skills…");
        var results = new List<AddResult>();
        foreach (var s in selected)
            foreach (var a in targetAgents)
                results.Add(new AddResult(s.InstallName, Agents.Get(a).DisplayName, null,
                    Installer.InstallWellKnownSkillForAgent(s.InstallName, s.Files, a, new InstallOptions { Global = installGlobally, Mode = mode })));
        spinner.Stop("Installation complete");
        Term.OutLine();

        var successful = results.Where(r => r.R.Success).ToList();
        var failed = results.Where(r => !r.R.Success).ToList();
        var okNames = successful.Select(r => r.Skill).ToHashSet();

        var skillFiles = new JsonObject();
        foreach (var s in selected) skillFiles[s.InstallName] = s.SourceUrl;

        if (privacy.Result != true)
        {
            Telemetry.Track(
                ("event", "install"),
                ("source", sourceIdentifier),
                ("skills", string.Join(",", selected.Select(s => s.InstallName))),
                ("agents", string.Join(",", targetAgents)),
                ("global", installGlobally ? "1" : null),
                ("skillFiles", Json.Stringify(skillFiles, 0)),
                ("installUrl", url),
                ("metadata", options.Metadata),
                ("sourceType", "well-known"));
        }

        if (successful.Count > 0)
        {
            var installedDirs = new Dictionary<string, string>();
            foreach (var r in successful)
                if (!installedDirs.ContainsKey(r.Skill)) installedDirs[r.Skill] = !string.IsNullOrEmpty(r.R.CanonicalPath) ? r.R.CanonicalPath : r.R.Path;
            InstallRecords.RecordWellKnownSkills(url, selected.Where(s => okNames.Contains(s.InstallName)), installedDirs, installGlobally, cwd);
        }

        if (successful.Count > 0) PrintInstalledNote(successful, targetAgents, cwd);
        PrintFailures(failed);

        Term.OutLine();
        Ui.Outro(DoneOutro());
        PromptForFindSkills(options, targetAgents);
        return true;
    }

    private static string SafeCanonicalPath(string name, bool global, string? cwd = null, string? agentType = null, string? eveSubagent = null)
    {
        try
        {
            return Installer.GetCanonicalPath(name, global, cwd, agentType, eveSubagent);
        }
        catch (InvalidOperationException)
        {
            return "";
        }
    }

    private static void PrintFailures(List<AddResult> failed)
    {
        if (failed.Count == 0) return;
        Term.OutLine();
        Ui.Log.Error(Pc.Red($"Failed to install {failed.Count}"));
        foreach (var r in failed) Ui.Log.Message($"  {Pc.Red("✗")} {r.Skill} → {r.Agent}: {Pc.Dim(r.R.Error ?? "")}");
    }
}
