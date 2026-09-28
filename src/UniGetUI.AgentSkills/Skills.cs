// Skill discovery and SKILL.md parsing (port of skills.ts).

using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Skills;

internal sealed record DiscoverOptions(bool IncludeInternal = false, bool FullDepth = false, bool IncludeDuplicateNames = false);

internal sealed class DiscoverException(string message) : Exception(message);

internal static partial class SkillDiscovery
{
    public static readonly string[] SkipDirs = ["node_modules", ".git", "dist", "build", "__pycache__"];

    public static readonly string[] AgentProjectSkillDirs =
    [
        ".agents/skills", ".claude/skills", ".cline/skills", ".codebuddy/skills", ".codex/skills", ".commandcode/skills",
        ".continue/skills", ".factory/skills", ".github/skills", ".goose/skills", ".grok/skills", ".iflow/skills",
        ".junie/skills", ".kilo/skills", ".kilocode/skills", ".kimchi/skills", ".kiro/skills", ".minimax/skills",
        ".mux/skills", ".neovate/skills", ".opencode/skills", ".openhands/skills", ".pi/skills", ".posit/assistant/skills",
        ".qoder/skills", ".roo/skills", ".trae/skills", ".windsurf/skills", ".zcode/skills", ".zencoder/skills",
    ];

    [GeneratedRegex(@"[\s_]+")]
    private static partial Regex WsUnderscore();

    [GeneratedRegex("/+")]
    private static partial Regex MultiSlash();

    public static string NormalizeSkillName(string name) => WsUnderscore().Replace(name.ToLowerInvariant(), "-");

    private static string NormalizeRelativePath(string p) => MultiSlash().Replace(string.Join("/", p.Split(NodePath.Sep)), "/");

    /// Internal skills are hidden unless INSTALL_INTERNAL_SKILLS=1|true.
    public static bool ShouldInstallInternalSkills() => Environment.GetEnvironmentVariable("INSTALL_INTERNAL_SKILLS") is "1" or "true";

    public static bool HasSkillMd(string dir) => Fs.IsFile(NodePath.Join(dir, "SKILL.md"));

    private static void WarnSkipped(string path, string reason) =>
        Sys.Warn($"⚠ Skipped {Sanitize.Metadata(path)} — {Sanitize.StripTerminalEscapes(reason)}");

    /// readFile(path, 'utf-8'): invalid UTF-8 becomes U+FFFD; a BOM is kept.
    public static string ReadUtf8(string path) => new UTF8Encoding(false).GetString(File.ReadAllBytes(path));

    public static Skill? ParseSkillMd(string skillMdPath, bool includeInternal)
    {
        string content;
        try
        {
            content = ReadUtf8(skillMdPath);
        }
        catch (Exception e)
        {
            WarnSkipped(skillMdPath, $"failed to read file: {e.Message}");
            return null;
        }
        JsonObject data;
        try
        {
            data = Frontmatter.Parse(content).Data;
        }
        catch (YamlParseException e)
        {
            WarnSkipped(skillMdPath, $"YAML parse error: {e.Message}");
            return null;
        }
        var nameOk = Json.TruthyProp(data, "name");
        var descOk = Json.TruthyProp(data, "description");
        if (!nameOk || !descOk)
        {
            var missing = new List<string>();
            if (!nameOk) missing.Add("name");
            if (!descOk) missing.Add("description");
            WarnSkipped(skillMdPath, $"missing required frontmatter field(s): {string.Join(", ", missing)}");
            return null;
        }
        if (Json.Str(data, "name") is not { } name || Json.Str(data, "description") is not { } description)
        {
            WarnSkipped(skillMdPath, $"frontmatter \"name\" and \"description\" must be strings (got {Json.TypeOf(data, "name")} and {Json.TypeOf(data, "description")})");
            return null;
        }
        var metadata = data["metadata"] as JsonObject;
        var isInternal = Json.AsBool(Json.Get(metadata, "internal")) == true;
        if (isInternal && !ShouldInstallInternalSkills() && !includeInternal) return null;
        return new Skill
        {
            Name = Sanitize.Metadata(name),
            Description = Sanitize.Metadata(description),
            Path = NodePath.Dirname(skillMdPath),
            RawContent = content,
            Metadata = metadata,
        };
    }

    private static List<string> FindSkillDirs(string dir, int depth, int maxDepth)
    {
        if (depth > maxDepth) return [];
        var output = new List<string>();
        if (HasSkillMd(dir)) output.Add(dir);
        foreach (var e in Fs.TryReadDir(dir))
            if (e.IsDirectory && !SkipDirs.Contains(e.Name))
                output.AddRange(FindSkillDirs(NodePath.Join(dir, e.Name), depth + 1, maxDepth));
        return output;
    }

    /// Validate that `subpath` stays within `basePath`.
    public static bool IsSubpathSafe(string basePath, string subpath)
    {
        var b = NodePath.Normalize(NodePath.Resolve(basePath));
        var t = NodePath.Normalize(NodePath.Resolve(NodePath.Join(basePath, subpath)));
        return t.StartsWith(b + NodePath.Sep, StringComparison.Ordinal) || t == b;
    }

    private sealed class Discovery(string basePath, DiscoverOptions options, HashSet<string> lockedNames, Dictionary<string, string> groupings)
    {
        public readonly List<Skill> Skills = new();
        private readonly HashSet<string> _seenNames = new();
        private readonly HashSet<string> _parsedPaths = new();

        private bool IsInstalledProjectSkill(Skill skill)
        {
            if (lockedNames.Count == 0) return false;
            var rel = NormalizeRelativePath(NodePath.Relative(basePath, skill.Path));
            if (!AgentProjectSkillDirs.Any(d => rel == d || rel.StartsWith(d + "/"))) return false;
            return lockedNames.Contains(NormalizeSkillName(skill.Name)) || lockedNames.Contains(NormalizeSkillName(NodePath.Basename(skill.Path)));
        }

        public Skill? ParseAt(string dir)
        {
            var md = NodePath.Resolve(dir, "SKILL.md");
            if (!_parsedPaths.Add(md)) return null;
            return ParseSkillMd(md, options.IncludeInternal);
        }

        public void Push(Skill skill)
        {
            if (groupings.TryGetValue(NodePath.Resolve(skill.Path), out var p)) skill.PluginName = p;
            _seenNames.Add(skill.Name);
            Skills.Add(skill);
        }

        public bool Seen(string name) => _seenNames.Contains(name);

        public bool IsInstalled(Skill s) => IsInstalledProjectSkill(s);

        private bool TryAddAt(string dir)
        {
            if (!HasSkillMd(dir)) return false;
            var skill = ParseAt(dir);
            if (skill == null) return true;
            if (!options.IncludeDuplicateNames && _seenNames.Contains(skill.Name)) return true;
            if (IsInstalledProjectSkill(skill)) return true;
            Push(skill);
            return true;
        }

        public void Walk(string dir, int maxDepth, int depth)
        {
            List<DirEntry> entries;
            try
            {
                entries = Fs.ReadDir(dir);
            }
            catch
            {
                return;
            }
            foreach (var e in entries)
            {
                if (!e.IsDirectory) continue;
                var child = NodePath.Join(dir, e.Name);
                var found = TryAddAt(child);
                if (found || depth >= maxDepth || SkipDirs.Contains(e.Name)) continue;
                Walk(child, maxDepth, depth + 1);
            }
        }
    }

    /// <exception cref="DiscoverException">The subpath escapes the base path.</exception>
    public static List<Skill> Discover(string basePath, string? subpath, DiscoverOptions options)
    {
        var localLock = LocalLock.Read(basePath);
        var locked = localLock.Skills.Select(kv => NormalizeSkillName(kv.Key)).ToHashSet();
        if (string.IsNullOrEmpty(subpath)) subpath = null;
        if (subpath != null && !IsSubpathSafe(basePath, subpath))
            throw new DiscoverException($"Invalid subpath: \"{subpath}\" resolves outside the repository directory. Subpath must not contain \"..\" segments that escape the base path.");
        var searchPath = subpath != null ? NodePath.Join(basePath, subpath) : basePath;
        var d = new Discovery(basePath, options, locked, PluginManifest.GetPluginGroupings(searchPath));

        if (HasSkillMd(searchPath) && d.ParseAt(searchPath) is { } root && !d.IsInstalled(root))
        {
            d.Push(root);
            if (!options.FullDepth) return d.Skills;
        }

        var priority = new List<string>
        {
            searchPath,
            NodePath.Join(searchPath, "skills"),
            NodePath.Join(searchPath, "skills/.curated"),
            NodePath.Join(searchPath, "skills/.experimental"),
            NodePath.Join(searchPath, "skills/.system"),
        };
        priority.AddRange(AgentProjectSkillDirs.Select(dir => NodePath.Join(searchPath, dir)));
        var deep = priority.Skip(1).ToHashSet();
        priority.AddRange(PluginManifest.GetPluginSkillPaths(searchPath));

        foreach (var dir in priority) d.Walk(dir, deep.Contains(dir) ? Constants.DefaultSkillContainerDepth : 1, 1);

        if (d.Skills.Count == 0 || options.FullDepth)
        {
            foreach (var dir in FindSkillDirs(searchPath, 0, 5))
            {
                if (d.ParseAt(dir) is { } s && (options.IncludeDuplicateNames || !d.Seen(s.Name)) && !d.IsInstalled(s)) d.Push(s);
            }
        }
        return d.Skills;
    }

    public static string DisplayName(Skill skill) => skill.Name.Length == 0 ? NodePath.Basename(skill.Path) : skill.Name;

    /// Case-insensitive exact match by name or display name.
    public static List<Skill> Filter(IEnumerable<Skill> skills, IEnumerable<string> inputNames)
    {
        var inputs = inputNames.Select(n => n.ToLowerInvariant()).ToList();
        return skills.Where(s => inputs.Any(i => i == s.Name.ToLowerInvariant() || i == DisplayName(s).ToLowerInvariant())).ToList();
    }
}
