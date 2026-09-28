// Skill removal (from remove.ts): scanning installed folders, resolving names
// and removing one skill from agent directories, the canonical copy and the
// lock file. The CLI adds prompts, output and telemetry around it.

namespace Skills;

internal sealed record RemoveOutcome(string Skill, bool Success, string Source, string SourceType, string? Error);

internal static class Removal
{
    /// Resolve requested names to canonical removal targets, preferring lock keys.
    public static List<string> ResolveSkillsToRemove(IEnumerable<string> requested, IEnumerable<string> folderNames, IEnumerable<string> lockKeys)
    {
        var identity = new Dictionary<string, string>();
        foreach (var f in folderNames) identity[Installer.SanitizeName(f)] = f;
        foreach (var k in lockKeys) identity[Installer.SanitizeName(k)] = k;
        var matched = new List<string>();
        foreach (var name in requested)
            if (identity.TryGetValue(Installer.SanitizeName(name), out var hit) && !matched.Contains(hit))
                matched.Add(hit);
        return matched;
    }

    /// Folder names of installed skills in the canonical and every agent
    /// directory of a scope, sorted ordinally. `warn` gets unreadable directories.
    public static List<string> ScanInstalled(bool isGlobal, string cwd, Action<string> warn)
    {
        var found = new List<string>();
        void ScanDir(string dir)
        {
            List<DirEntry> entries;
            try
            {
                entries = Fs.ReadDir(dir);
            }
            catch (Exception e) when (e is DirectoryNotFoundException or FileNotFoundException)
            {
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                warn($"Could not scan directory {dir}: {e.Message}");
                return;
            }
            foreach (var e in entries)
            {
                if (!e.IsDirectory || e.Name.StartsWith('.')) continue;
                if (!SkillDiscovery.HasSkillMd(e.FullPath)) continue;
                if (!found.Contains(e.Name)) found.Add(e.Name);
            }
        }

        if (isGlobal)
        {
            ScanDir(Installer.GetCanonicalSkillsDir(true, cwd));
            foreach (var a in Agents.List)
                if (a.GlobalSkillsDir != null) ScanDir(a.GlobalSkillsDir);
        }
        else
        {
            ScanDir(Installer.GetCanonicalSkillsDir(false, cwd));
            foreach (var a in Agents.List) ScanDir(NodePath.Join(cwd, a.SkillsDir));
            foreach (var sub in Agents.GetEveSubagents(cwd)) ScanDir(Installer.GetEveSubagentSkillsDir(sub, cwd));
        }
        return Collate.StableSort(found, string.CompareOrdinal);
    }

    /// Remove one skill from `targetAgents`. The canonical copy and the lock
    /// entry go too unless another detected agent still has the skill.
    public static RemoveOutcome RemoveSkill(string skillName, IReadOnlyList<string> targetAgents, bool isGlobal, string cwd, Action<string> warn)
    {
        try
        {
            var canonical = Installer.GetCanonicalPath(skillName, isGlobal, cwd);
            foreach (var at in targetAgents)
            {
                var a = Agents.Get(at);
                var skillPath = Installer.GetInstallPath(skillName, at, isGlobal, cwd);
                var sanitized = Installer.SanitizeName(skillName);
                var cleanup = new List<string> { skillPath };
                void Add(string p)
                {
                    if (!cleanup.Contains(p)) cleanup.Add(p);
                }
                if (isGlobal && a.GlobalSkillsDir != null)
                {
                    Add(NodePath.Join(a.GlobalSkillsDir, sanitized));
                }
                else
                {
                    Add(NodePath.Join(cwd, a.SkillsDir, sanitized));
                    if (at == "eve")
                        foreach (var sub in Agents.GetEveSubagents(cwd)) Add(NodePath.Join(Installer.GetEveSubagentSkillsDir(sub, cwd), sanitized));
                }
                foreach (var p in cleanup)
                {
                    if (p == canonical || !Fs.LExists(p)) continue;
                    try
                    {
                        Fs.RemoveAll(p);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        warn($"Could not remove skill from {a.DisplayName}: {e.Message}");
                    }
                }
            }

            var remaining = Agents.DetectInstalledAgents().Where(a => !targetAgents.Contains(a));
            var stillUsed = remaining.Any(at => Fs.LExists(Installer.GetInstallPath(skillName, at, isGlobal, cwd)));
            if (!stillUsed) Fs.RemoveAll(canonical);

            var entry = isGlobal ? SkillLock.GetSkill(skillName) : LocalLock.Read(cwd).Skills[skillName];
            var source = Json.NonEmpty(entry, "source") ?? "local";
            var sourceType = Json.NonEmpty(entry, "sourceType") ?? "local";
            if (!stillUsed)
            {
                if (isGlobal) SkillLock.RemoveSkill(skillName);
                else LocalLock.RemoveSkill(skillName, cwd);
            }
            return new RemoveOutcome(skillName, true, source, sourceType, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new RemoveOutcome(skillName, false, "", "", e.Message);
        }
    }
}
