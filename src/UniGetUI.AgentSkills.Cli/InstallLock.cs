// `skills experimental_install` — restore project skills from
// `skills-lock.json` into `.agents/skills` (port of install.ts).

namespace Skills;

internal static class InstallLockCommand
{
    public static void Run(IReadOnlyList<string> args)
    {
        var lockFile = LocalLock.Read();
        if (lockFile.Skills.Count == 0)
        {
            Ui.Log.Warn("No project skills found in skills-lock.json");
            Ui.Log.Info($"Add project-level skills with {Pc.Cyan("skills add <package>")} (without {Pc.Cyan("-g")})");
            return;
        }
        var universal = Agents.GetUniversalAgents();
        var nodeModuleSkills = new List<string>();
        var bySource = new List<(string Source, List<string> Skills)>();

        foreach (var (name, entry) in lockFile.Skills)
        {
            if (Json.Str(entry, "sourceType") == "node_modules")
            {
                nodeModuleSkills.Add(name);
                continue;
            }
            if (UpdateSource.BuildLocalUpdateSource(UpdateSourceEntry.FromJson(entry)) is not { } source)
            {
                Ui.Log.Error($"Cannot restore {Pc.Cyan(name)}: skills-lock.json is missing sourceUrl for this generic Git source");
                continue;
            }
            var i = bySource.FindIndex(x => x.Source == source);
            if (i >= 0) bySource[i].Skills.Add(name);
            else bySource.Add((source, [name]));
        }

        var remote = lockFile.Skills.Count - nodeModuleSkills.Count;
        if (remote > 0)
            Ui.Log.Info($"Restoring {Pc.Cyan(remote.ToString())} skill{(remote != 1 ? "s" : "")} from skills-lock.json into {Pc.Dim(".agents/skills/")}");

        foreach (var (source, skills) in bySource)
            AddCommand.Run([source], new AddOptions { Skill = skills, Agent = [.. universal], Yes = true });

        if (nodeModuleSkills.Count > 0)
        {
            var n = nodeModuleSkills.Count;
            Ui.Log.Info($"{Pc.Cyan(n.ToString())} skill{(n != 1 ? "s" : "")} from node_modules");
            var o = SyncCommand.ParseOptions(args);
            o.Yes = true;
            o.Agent = [.. universal];
            SyncCommand.Run(o);
        }
    }
}
