// `skills remove` (port of remove.ts).

namespace Skills;

internal sealed class RemoveOptions
{
    public bool Global { get; set; }
    public List<string>? Agent { get; set; }
    public bool Yes { get; set; }
    public bool All { get; set; }
}

internal static class RemoveCommand
{
    public static List<string> ResolveSkillsToRemove(IEnumerable<string> requested, IEnumerable<string> folderNames, IEnumerable<string> lockKeys) =>
        Removal.ResolveSkillsToRemove(requested, folderNames, lockKeys);

    public static void Run(List<string> skillNames, RemoveOptions options)
    {
        Installer.ResetPopulated();
        var agentResult = DetectAgent.Detect();
        if (agentResult.IsAgent)
        {
            options.Yes = true;
            Ui.Log.Info($"{Pc.BgCyan(Pc.Black(Pc.Bold($" {agentResult.Name} ")))} Agent detected — removing non-interactively");
        }

        if (skillNames.Contains("*"))
        {
            options.All = true;
            skillNames.RemoveAll(n => n == "*");
        }
        if (options.All && skillNames.Count > 0)
        {
            Ui.Log.Error("Cannot combine --all with specific skill names.");
            Ui.Log.Info("Use `skills remove --all` to remove every skill, or omit --all to remove only the named skills.");
            Ui.Log.Info($"Example: skills remove {skillNames[0]} -y");
            Term.Exit(1);
        }

        var isGlobal = options.Global;
        var cwd = Sys.Cwd();
        var spinner = new Ui.Spinner();
        spinner.Start("Scanning for installed skills…");

        var installed = Removal.ScanInstalled(isGlobal, cwd, Ui.Log.Warn);
        spinner.Stop($"Found {installed.Count} unique installed skill(s)");

        var lockKeys = (isGlobal ? SkillLock.Read().Skills : LocalLock.Read(cwd).Skills).Select(kv => kv.Key).ToList();

        var requested = options.All ? installed.Concat(lockKeys).ToList() : skillNames;
        var resolved = options.All || skillNames.Count > 0 ? ResolveSkillsToRemove(requested, installed, lockKeys) : [];

        if (installed.Count == 0 && resolved.Count == 0)
        {
            Ui.Outro(Pc.Yellow("No skills found to remove."));
            return;
        }

        if (options.Agent is { Count: > 0 } agentList)
        {
            var invalid = agentList.Where(a => Agents.Find(a) == null).ToList();
            if (invalid.Count > 0)
            {
                Ui.Log.Error($"Invalid agents: {string.Join(", ", invalid)}");
                Ui.Log.Info($"Valid agents: {string.Join(", ", Agents.AllNames())}");
                Term.Exit(1);
            }
        }

        List<string> selected;
        if (options.All)
        {
            selected = resolved;
        }
        else if (skillNames.Count > 0)
        {
            if (resolved.Count == 0)
            {
                Ui.Log.Error($"No matching skills found for: {string.Join(", ", skillNames)}");
                return;
            }
            selected = resolved;
        }
        else
        {
            var choices = installed.Select(s => new SelectOption<string>(s, s)).ToList();
            var chosen = Ui.Multiselect($"Select skills to remove {Pc.Dim("(space to toggle)")}", choices, [], true);
            if (chosen == null)
            {
                Ui.Cancel("Removal cancelled");
                Term.Exit(0);
            }
            selected = ResolveSkillsToRemove(chosen, installed, lockKeys);
        }

        List<string> targetAgents;
        if (options.Agent is { Count: > 0 } al)
        {
            targetAgents = al;
        }
        else
        {
            targetAgents = Agents.AllNames();
            spinner.Stop($"Targeting {targetAgents.Count} potential agent(s)");
        }

        if (!options.Yes)
        {
            Term.OutLine();
            Ui.Log.Info("Skills to remove:");
            foreach (var s in selected) Ui.Log.Message($"  {Pc.Red("•")} {s}");
            Term.OutLine();
            if (Ui.Confirm($"Are you sure you want to uninstall {selected.Count} skill(s)?") != true)
            {
                Ui.Cancel("Removal cancelled");
                Term.Exit(0);
            }
        }

        spinner.Start("Removing skills…");

        var results = new List<RemoveOutcome>();
        foreach (var skillName in selected) results.Add(Removal.RemoveSkill(skillName, targetAgents, isGlobal, cwd, Ui.Log.Warn));

        spinner.Stop("Removal process complete");

        var successful = results.Where(r => r.Success).ToList();
        var failed = results.Where(r => !r.Success).ToList();

        if (successful.Count > 0)
        {
            var bySource = new List<(string Source, List<string> Skills, string SourceType)>();
            foreach (var r in successful)
            {
                var src = r.Source.Length == 0 ? "local" : r.Source;
                var i = bySource.FindIndex(x => x.Source == src);
                if (i >= 0)
                {
                    bySource[i].Skills.Add(r.Skill);
                    bySource[i] = bySource[i] with { SourceType = r.SourceType };
                }
                else
                {
                    bySource.Add((src, [r.Skill], r.SourceType));
                }
            }
            foreach (var (source, skills, sourceType) in bySource)
            {
                Telemetry.Track(
                    ("event", "remove"),
                    ("source", source),
                    ("skills", string.Join(",", skills)),
                    ("agents", string.Join(",", targetAgents)),
                    ("global", isGlobal ? "1" : null),
                    ("sourceType", sourceType));
            }
            Ui.Log.Success(Pc.Green($"Successfully removed {successful.Count} skill(s)"));
        }
        if (failed.Count > 0)
        {
            Ui.Log.Error(Pc.Red($"Failed to remove {failed.Count} skill(s)"));
            foreach (var r in failed) Ui.Log.Message($"  {Pc.Red("✗")} {r.Skill}: {r.Error}");
        }
        Term.OutLine();
        Ui.Outro(Pc.Green("Done!"));
    }

    /// Separate skill names from option flags.
    public static (List<string> Skills, RemoveOptions Options) ParseOptions(IReadOnlyList<string> args)
    {
        var o = new RemoveOptions();
        var skills = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            switch (a)
            {
                case "-g" or "--global":
                    o.Global = true;
                    break;
                case "-y" or "--yes":
                    o.Yes = true;
                    break;
                case "--all":
                    o.All = true;
                    o.Yes = true;
                    break;
                case "-s" or "--skill":
                    while (i + 1 < args.Count && args[i + 1].Length > 0 && !args[i + 1].StartsWith('-')) skills.Add(args[++i]);
                    break;
                case "-a" or "--agent":
                    o.Agent ??= [];
                    while (i + 1 < args.Count && args[i + 1].Length > 0 && !args[i + 1].StartsWith('-')) o.Agent.Add(args[++i]);
                    break;
                default:
                    if (a.Length > 0 && !a.StartsWith('-')) skills.Add(a);
                    break;
            }
        }
        return (skills, o);
    }
}
