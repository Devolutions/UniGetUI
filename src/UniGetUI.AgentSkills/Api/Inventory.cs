// Installed-skill listing and removal behind
// the public API.

using System.Globalization;
using System.Text.Json.Nodes;
using Devolutions.AgentSkills;

namespace Skills;

internal static class Inventory
{
    private static SkillScope ScopeOf(string scope) => scope == "global" ? SkillScope.Global : SkillScope.Project;

    /// A lock lookup like `skills list`: exact key first, then the last entry
    /// whose sanitized key matches.
    private static Func<string, JsonNode?> LockLookup(JsonObject locked)
    {
        var bySanitized = new Dictionary<string, JsonNode?>();
        foreach (var (k, v) in locked) bySanitized[Installer.SanitizeName(k)] = v;
        return name => locked.TryGetPropertyValue(name, out var v) ? v : bySanitized.GetValueOrDefault(Installer.SanitizeName(name));
    }

    private static DateTimeOffset? Date(JsonNode? e, string key) =>
        Json.NonEmpty(e, key) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d : null;

    public static List<InstalledSkillInfo> List(SkillScope? scope)
    {
        var cwd = Sys.Cwd();
        var installed = Installer.ListInstalledSkills(scope is { } s ? s == SkillScope.Global : null, cwd);
        var globalLookup = scope != SkillScope.Project ? LockLookup(SkillLock.GetAllSkills()) : null;
        var projectLookup = scope != SkillScope.Global ? LockLookup(LocalLock.Read(cwd).Skills) : null;
        return installed.Select(skill =>
        {
            var skillScope = ScopeOf(skill.Scope);
            var e = (skillScope == SkillScope.Global ? globalLookup : projectLookup)?.Invoke(skill.Name);
            return new InstalledSkillInfo
            {
                Name = skill.Name,
                Description = skill.Description,
                Path = skill.CanonicalPath,
                Scope = skillScope,
                Agents = skill.Agents.ToList(),
                Source = Json.NonEmpty(e, "source"),
                SourceType = Json.NonEmpty(e, "sourceType"),
                SourceUrl = Json.NonEmpty(e, "sourceUrl"),
                SourceBaseUrl = Json.NonEmpty(e, "sourceBaseUrl"),
                Ref = Json.NonEmpty(e, "ref"),
                SkillPath = Json.NonEmpty(e, "skillPath"),
                Hash = Json.NonEmpty(e, skillScope == SkillScope.Global ? "skillFolderHash" : "computedHash")
                       ?? Json.NonEmpty(e, "wellKnownDigest")
                       ?? Json.NonEmpty(e, "notionVersionId"),
                PluginName = Json.NonEmpty(e, "pluginName"),
                PinnedRef = Pinning.PinnedRef(e),
                InstalledAt = Date(e, "installedAt"),
                UpdatedAt = Date(e, "updatedAt"),
            };
        }).ToList();
    }

    public static SkillRemoveResult Remove(IReadOnlyList<string> skillNames, SkillScope scope, IReadOnlyList<string>? agents, Action<string> log)
    {
        InstallEngine.ValidateAgents(agents);
        Installer.ResetPopulated();
        var cwd = Sys.Cwd();
        var global = scope == SkillScope.Global;
        var installed = Removal.ScanInstalled(global, cwd, log);
        var lockKeys = (global ? SkillLock.Read().Skills : LocalLock.Read(cwd).Skills).Select(kv => kv.Key).ToList();
        var requested = skillNames.Contains("*") ? installed.Concat(lockKeys).ToList() : skillNames.Where(n => n.Length > 0).ToList();
        var targetAgents = agents is { Count: > 0 } && !agents.Contains("*") ? agents.Distinct().ToList() : Agents.AllNames();

        var outcomes = new List<SkillRemoveOutcome>();
        var done = new HashSet<string>();
        foreach (var name in requested)
        {
            Sys.ThrowIfCancelled();
            var resolved = Removal.ResolveSkillsToRemove([name], installed, lockKeys);
            if (resolved.Count == 0)
            {
                outcomes.Add(new SkillRemoveOutcome(name, SkillOperationStatus.NotFound, "No installed skill with this name"));
                continue;
            }
            if (!done.Add(resolved[0])) continue;
            log($"Removing {resolved[0]}…");
            var r = Removal.RemoveSkill(resolved[0], targetAgents, global, cwd, log);
            outcomes.Add(new SkillRemoveOutcome(r.Skill, r.Success ? SkillOperationStatus.Succeeded : SkillOperationStatus.Failed, r.Error));
        }
        return new SkillRemoveResult(outcomes);
    }
}
