// Claude plugin manifest discovery (port of plugin-manifest.ts).

using System.Text.Json.Nodes;

namespace Skills;

internal static class PluginManifest
{
    private static bool IsValidRelativePath(string p) => p.StartsWith("./");

    private static JsonNode? ReadJson(string path)
    {
        try
        {
            return Json.Parse(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    private sealed record Plugin(string? Source, List<string>? Skills, string? Name);

    /// Parse marketplace plugins as (pluginRoot, plugins); remote (object)
    /// sources are skipped, and an invalid pluginRoot yields no plugins.
    private static (string Root, List<Plugin> Plugins)? Marketplace(string basePath)
    {
        var manifest = ReadJson(NodePath.Join(basePath, ".claude-plugin/marketplace.json"));
        if (manifest is not JsonObject) return null;
        string? root = null;
        var rootNode = Json.Get(Json.Get(manifest, "metadata"), "pluginRoot");
        if (Json.Has(Json.Get(manifest, "metadata"), "pluginRoot") && rootNode != null)
        {
            if (Json.AsString(rootNode) is not { } r || !IsValidRelativePath(r)) return ("", []);
            root = r;
        }
        var plugins = new List<Plugin>();
        if (Json.Get(manifest, "plugins") is JsonArray list)
        {
            foreach (var p in list)
            {
                if (p is not JsonObject) return (root ?? "", plugins); // TS throws on property access → rest dropped
                var sourceNode = Json.Get(p, "source");
                string? source = null;
                if (Json.Has(p, "source") && sourceNode != null)
                {
                    if (Json.AsString(sourceNode) is not { } s) continue;
                    if (!IsValidRelativePath(s)) continue;
                    source = s;
                }
                var skills = Json.Get(p, "skills") is JsonArray arr ? arr.Select(Json.AsString).Where(x => x != null).Select(x => x!).ToList() : null;
                plugins.Add(new Plugin(source, skills, Json.NonEmpty(p, "name")));
            }
        }
        return (root ?? "", plugins);
    }

    private static List<string>? StringArray(JsonNode? node, string key) =>
        Json.Get(node, key) is JsonArray arr ? arr.Select(Json.AsString).Where(x => x != null).Select(x => x!).ToList() : null;

    /// Directories that contain skills, derived from plugin manifests.
    public static List<string> GetPluginSkillPaths(string basePath)
    {
        var dirs = new List<string>();
        void Add(string pluginBase, List<string>? skills)
        {
            if (!NodePath.IsPathSafe(basePath, pluginBase)) return;
            if (skills != null)
            {
                foreach (var sp in skills)
                {
                    if (!IsValidRelativePath(sp)) continue;
                    var skillDir = NodePath.Dirname(NodePath.Join(pluginBase, sp));
                    if (NodePath.IsPathSafe(basePath, skillDir)) dirs.Add(skillDir);
                }
            }
            dirs.Add(NodePath.Join(pluginBase, "skills"));
        }

        if (Marketplace(basePath) is var (root, plugins))
            foreach (var p in plugins) Add(NodePath.Join(basePath, root, p.Source ?? ""), p.Skills);

        if (ReadJson(NodePath.Join(basePath, ".claude-plugin/plugin.json")) is JsonObject manifest)
            Add(basePath, StringArray(manifest, "skills"));
        return dirs;
    }

    /// Map of absolute skill directory → plugin name.
    public static Dictionary<string, string> GetPluginGroupings(string basePath)
    {
        var groupings = new Dictionary<string, string>();
        if (Marketplace(basePath) is var (root, plugins))
        {
            foreach (var p in plugins)
            {
                if (p.Name == null) continue;
                var pluginBase = NodePath.Join(basePath, root, p.Source ?? "");
                if (!NodePath.IsPathSafe(basePath, pluginBase)) continue;
                foreach (var sp in p.Skills ?? [])
                {
                    if (!IsValidRelativePath(sp)) continue;
                    var skillDir = NodePath.Join(pluginBase, sp);
                    if (NodePath.IsPathSafe(basePath, skillDir)) groupings[NodePath.Resolve(skillDir)] = p.Name;
                }
            }
        }
        if (ReadJson(NodePath.Join(basePath, ".claude-plugin/plugin.json")) is JsonObject manifest
            && Json.NonEmpty(manifest, "name") is { } name
            && StringArray(manifest, "skills") is { Count: > 0 } skills)
        {
            foreach (var sp in skills)
            {
                if (!IsValidRelativePath(sp)) continue;
                var skillDir = NodePath.Join(basePath, sp);
                if (NodePath.IsPathSafe(basePath, skillDir)) groupings[NodePath.Resolve(skillDir)] = name;
            }
        }
        return groupings;
    }
}
