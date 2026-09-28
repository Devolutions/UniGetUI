// `skills list` (port of list.ts).

using System.Text.Json.Nodes;
using static Skills.Ansi;

namespace Skills;

internal sealed class ListOptions
{
    public bool Global { get; set; }
    public List<string>? Agent { get; set; }
    public bool Json { get; set; }
}

internal static class ListCommand
{
    /// Shorten a path for display (note: plain prefix checks, as in list.ts).
    private static string ShortenPath(string full, string cwd)
    {
        var home = Sys.HomeDir();
        if (full.StartsWith(home, StringComparison.Ordinal)) return "~" + full[home.Length..];
        if (full.StartsWith(cwd, StringComparison.Ordinal)) return "." + full[cwd.Length..];
        return full;
    }

    public static string FormatList(IReadOnlyList<string> items, int maxShow) =>
        items.Count <= maxShow ? string.Join(", ", items) : $"{string.Join(", ", items.Take(maxShow))} +{items.Count - maxShow} more";

    public static ListOptions ParseOptions(IReadOnlyList<string> args)
    {
        var o = new ListOptions();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a is "-g" or "--global") o.Global = true;
            else if (a == "--json") o.Json = true;
            else if (a is "-a" or "--agent")
            {
                o.Agent ??= [];
                while (i + 1 < args.Count && !args[i + 1].StartsWith('-')) o.Agent.Add(args[++i]);
            }
        }
        return o;
    }

    public static string KebabToTitle(string s) =>
        string.Join(" ", s.Split('-').Select(w => w.Length == 0 ? "" : char.ToUpperInvariant(w[0]) + w[1..]));

    /// The `Source:` value: the lock's source (plus ` (pinned <ref>)` for a
    /// pinned entry), a `gh skill` origin for untracked skills, else `local`.
    private static string SourceLabel(bool found, JsonNode? entry, string? source, string skillDir)
    {
        string label;
        if (source != null) label = Sanitize.Metadata(source);
        else if (!found) return GhInstalled.ReadGhOrigin(skillDir) is { } origin ? Sanitize.Metadata(origin.ListLabel()) : "local";
        else label = "local";
        return found && Pinning.PinnedRef(entry) is { } r ? $"{label} (pinned {Sanitize.Metadata(r)})" : label;
    }

    /// `list --json` extension keys: `ref`/`pinned` for pinned lock entries, the
    /// origin plus `managedBy: "gh"` for skills installed by `gh skill`.
    private static void AddExtensionJson(JsonObject o, bool found, JsonNode? entry, string skillDir)
    {
        if (found)
        {
            if (Pinning.PinnedRef(entry) is { } r)
            {
                o["ref"] = r;
                o["pinned"] = true;
            }
            return;
        }
        switch (GhInstalled.ReadGhOrigin(skillDir))
        {
            case GhGitHubOrigin g:
                o["source"] = g.Source();
                o["sourceUrl"] = g.Url;
                o["sourceType"] = "github";
                o["ref"] = g.GitRef != null ? GhInstalled.ShortRef(g.GitRef) : null;
                o["pinned"] = g.Pinned != null;
                o["managedBy"] = "gh";
                break;
            case GhLocalOrigin l:
                o["source"] = l.LocalPath;
                o["sourceUrl"] = null;
                o["sourceType"] = "local";
                o["managedBy"] = "gh";
                break;
        }
    }

    public static void Run(IReadOnlyList<string> args)
    {
        var options = ParseOptions(args);
        var scope = options.Global;

        List<string>? agentFilter = null;
        if (options.Agent is { Count: > 0 } list)
        {
            var invalid = list.Where(a => Agents.Find(a) == null).ToList();
            if (invalid.Count > 0)
            {
                Term.OutLine($"{Yellow}Invalid agents: {string.Join(", ", invalid)}{Reset}");
                Term.OutLine($"{Dim}Valid agents: {string.Join(", ", Agents.AllNames())}{Reset}");
                Term.Exit(1);
            }
            agentFilter = list;
        }

        var installed = Installer.ListInstalledSkills(scope, null, agentFilter);
        var cwd = Sys.Cwd();
        var locked = scope ? SkillLock.GetAllSkills() : LocalLock.Read(cwd).Skills;
        // Map semantics: the last entry with a given sanitized key wins
        var bySanitized = new Dictionary<string, JsonNode?>();
        foreach (var (k, v) in locked) bySanitized[Installer.SanitizeName(k)] = v;
        JsonNode? LockEntry(string name) =>
            locked.TryGetPropertyValue(name, out var v) ? v : bySanitized.GetValueOrDefault(Installer.SanitizeName(name));
        bool HasLockEntry(string name) => locked.ContainsKey(name) || bySanitized.ContainsKey(Installer.SanitizeName(name));

        if (options.Json)
        {
            var arr = new JsonArray();
            foreach (var s in installed)
            {
                var e = LockEntry(s.Name);
                JsonNode? Field(string key) => Json.Get(e, key)?.DeepClone();
                var obj = new JsonObject
                {
                    ["name"] = s.Name,
                    ["path"] = s.CanonicalPath,
                    ["scope"] = s.Scope,
                    ["agents"] = new JsonArray(s.Agents.Select(a => (JsonNode?)Agents.Get(a).DisplayName).ToArray()),
                    ["source"] = Field("source"),
                    ["sourceUrl"] = Field("sourceUrl"),
                    ["sourceType"] = Field("sourceType"),
                };
                AddExtensionJson(obj, HasLockEntry(s.Name), e, s.CanonicalPath);
                arr.Add((JsonNode)obj);
            }
            Term.OutLine(Json.Stringify(arr));
            return;
        }

        var scopeLabel = scope ? "Global" : "Project";
        if (installed.Count == 0)
        {
            Term.OutLine($"{Dim}No {scopeLabel.ToLowerInvariant()} skills found.{Reset}");
            Term.OutLine(scope ? $"{Dim}Try listing project skills without -g{Reset}" : $"{Dim}Try listing global skills with -g{Reset}");
            return;
        }

        void PrintSkill(InstalledSkill skill, bool indent, int maxName, int maxPath)
        {
            var prefix = indent ? "  " : "";
            var shortPath = ShortenPath(skill.CanonicalPath, cwd);
            var agentInfo = skill.Agents.Count > 0 ? FormatList(skill.Agents.Select(a => Agents.Get(a).DisplayName).ToList(), 5) : $"{Yellow}not linked{Reset}";
            var paddedName = Sanitize.Metadata(skill.Name).PadRight(maxName);
            var paddedPath = shortPath.PadRight(maxPath);
            var entry = LockEntry(skill.Name);
            var source = Json.NonEmpty(entry, "source");
            var sourceLabel = SourceLabel(HasLockEntry(skill.Name), entry, source, skill.CanonicalPath);
            Term.OutLine($"{prefix}{Cyan}{paddedName}{Reset} {Dim}{paddedPath}{Reset}");
            Term.OutLine($"{prefix}  {Dim}Agents:{Reset} {agentInfo}  {Dim}Source:{Reset} {sourceLabel}");
        }

        (int, int) Widths(IEnumerable<InstalledSkill> skills)
        {
            int n = 0, p = 0;
            foreach (var s in skills)
            {
                n = Math.Max(n, Sanitize.Metadata(s.Name).Length);
                p = Math.Max(p, ShortenPath(s.CanonicalPath, cwd).Length);
            }
            return (n, p);
        }

        Term.OutLine($"{Bold}{scopeLabel} Skills{Reset}");
        Term.OutLine();

        var groups = new List<(string Name, List<InstalledSkill> Skills)>();
        var ungrouped = new List<InstalledSkill>();
        foreach (var s in installed)
        {
            if (Json.NonEmpty(LockEntry(s.Name), "pluginName") is { } g)
            {
                var i = groups.FindIndex(x => x.Name == g);
                if (i >= 0) groups[i].Skills.Add(s);
                else groups.Add((g, [s]));
            }
            else
            {
                ungrouped.Add(s);
            }
        }

        if (groups.Count > 0)
        {
            foreach (var (group, skills) in Collate.StableSort(groups, (a, b) => string.CompareOrdinal(a.Name, b.Name)))
            {
                Term.OutLine($"{Bold}{KebabToTitle(group)}{Reset}");
                var (n, p) = Widths(skills);
                foreach (var s in skills) PrintSkill(s, true, n, p);
                Term.OutLine();
            }
            if (ungrouped.Count > 0)
            {
                Term.OutLine($"{Bold}General{Reset}");
                var (n, p) = Widths(ungrouped);
                foreach (var s in ungrouped) PrintSkill(s, true, n, p);
                Term.OutLine();
            }
        }
        else
        {
            var (n, p) = Widths(installed);
            foreach (var s in installed) PrintSkill(s, false, n, p);
            Term.OutLine();
        }
    }
}
