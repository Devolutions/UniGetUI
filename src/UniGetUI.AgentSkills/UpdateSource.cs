// Build `skills add` source arguments for updates (port of update-source.ts).

using System.Text.Json.Nodes;

namespace Skills;

internal sealed record UpdateSourceEntry(string Source, string? SourceUrl = null, string? SourceType = null, string? Ref = null, string? SkillPath = null)
{
    /// Build from a lock-file JSON entry (empty strings count as absent).
    public static UpdateSourceEntry FromJson(JsonNode? e) =>
        new(Json.Str(e, "source") ?? "", Json.NonEmpty(e, "sourceUrl"), Json.NonEmpty(e, "sourceType"), Json.NonEmpty(e, "ref"), Json.NonEmpty(e, "skillPath"));
}

internal static class UpdateSource
{
    public static string FormatSourceInput(string sourceUrl, string? r) => string.IsNullOrEmpty(r) ? sourceUrl : $"{sourceUrl}#{r}";

    private static string DeriveSkillFolder(string skillPath)
    {
        var folder = skillPath;
        if (folder.EndsWith("/SKILL.md")) folder = folder[..^9];
        else if (folder.EndsWith("SKILL.md")) folder = folder[..^8];
        if (folder.EndsWith('/')) folder = folder[..^1];
        return folder;
    }

    private static bool SupportsAppendedSubpath(string source)
    {
        if (source.StartsWith("git@") || source.StartsWith("ssh://") || source.EndsWith(".git")) return false;
        if (source.StartsWith("http://") || source.StartsWith("https://"))
            return WebUrl.Parse(source) is { Hostname: "github.com" or "gitlab.com" };
        return true;
    }

    private static bool IsBareShorthand(string source) => !source.Contains(':') && !source.StartsWith('.') && !source.StartsWith('/');

    private static string? GetLocalSource(UpdateSourceEntry e)
    {
        if (e.SourceUrl != null) return e.SourceUrl;
        var requiresUrl = e.SourceType is "git" or "gitlab";
        return requiresUrl && IsBareShorthand(e.Source) ? null : e.Source;
    }

    /// Cloneable repository URL for project update checks.
    public static string? BuildLocalCloneSource(UpdateSourceEntry e)
    {
        var source = GetLocalSource(e);
        if (source == null) return null;
        if (e.SourceType == "github" && IsBareShorthand(source))
            return $"https://github.com/{(source.EndsWith(".git") ? source[..^4] : source)}.git";
        return source;
    }

    public static bool ShouldUseFullDepthForUpdate(UpdateSourceEntry e)
    {
        if (e.SkillPath == null) return false;
        var source = e.SourceType is { } t && t != "github" ? GetLocalSource(e) : e.Source;
        return source != null && !SupportsAppendedSubpath(source);
    }

    private static string AppendFolderAndRef(string source, string skillPath, string? r)
    {
        if (!SupportsAppendedSubpath(source)) return FormatSourceInput(source, r);
        var folder = DeriveSkillFolder(skillPath);
        return FormatSourceInput(folder.Length == 0 ? source : $"{source}/{folder}", r);
    }

    /// Source argument for `skills add` during a global update.
    public static string? BuildUpdateInstallSource(UpdateSourceEntry e)
    {
        var nonGithub = e.SourceType is { } t && t != "github";
        if (e.SkillPath is not { } sp)
        {
            var source = nonGithub ? GetLocalSource(e) : e.SourceUrl ?? e.Source;
            return string.IsNullOrEmpty(source) ? null : FormatSourceInput(source, e.Ref);
        }
        var s = nonGithub ? GetLocalSource(e) : e.Source;
        return string.IsNullOrEmpty(s) ? null : AppendFolderAndRef(s, sp, e.Ref);
    }

    /// Source argument for `skills add` during a project update.
    public static string? BuildLocalUpdateSource(UpdateSourceEntry e)
    {
        var source = GetLocalSource(e);
        if (source == null) return null;
        return e.SkillPath is { } sp ? AppendFolderAndRef(source, sp, e.Ref) : FormatSourceInput(source, e.Ref);
    }
}

internal sealed record DiscoveredSkillLocation(string Name, string SkillPath);

internal sealed record SkillLocationResolution(List<string> DeletedSkills, List<string> AmbiguousSkills, Dictionary<string, string> ResolvedPaths);

/// Resolve locked skills against their current locations (port of skill-relocation.ts).
internal static class SkillRelocation
{
    private static string NormalizePath(string p)
    {
        var s = p.Replace('\\', '/');
        var sb = new System.Text.StringBuilder();
        foreach (var c in s)
        {
            if (c == '/' && sb.Length > 0 && sb[^1] == '/') continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// Exact paths win; a missing path is a relocation only when exactly one
    /// discovered skill has the same normalized name. Ambiguity fails closed.
    public static SkillLocationResolution Resolve(IEnumerable<string> locked, JsonObject lockSkills, IReadOnlyList<DiscoveredSkillLocation> discovered)
    {
        var discoveredPaths = discovered.Select(d => NormalizePath(d.SkillPath)).ToHashSet();
        var byName = new Dictionary<string, List<string>>();
        foreach (var d in discovered)
        {
            var key = SkillDiscovery.NormalizeSkillName(d.Name);
            if (!byName.TryGetValue(key, out var list)) byName[key] = list = [];
            var p = NormalizePath(d.SkillPath);
            if (!list.Contains(p)) list.Add(p);
        }
        var output = new SkillLocationResolution([], [], []);
        foreach (var name in locked)
        {
            if (Json.NonEmpty(lockSkills[name], "skillPath") is not { } lockedPath) continue;
            var nlp = NormalizePath(lockedPath);
            var candidates = byName.GetValueOrDefault(SkillDiscovery.NormalizeSkillName(name)) ?? [];
            if (candidates.Count > 1)
            {
                output.AmbiguousSkills.Add(name);
                continue;
            }
            if (discoveredPaths.Contains(nlp)) output.ResolvedPaths[name] = nlp;
            else if (candidates.Count == 1) output.ResolvedPaths[name] = candidates[0];
            else output.DeletedSkills.Add(name);
        }
        return output;
    }
}
