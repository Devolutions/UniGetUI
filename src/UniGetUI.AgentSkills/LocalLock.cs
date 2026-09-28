// Project lock file skills-lock.json (port of local-lock.ts).
//
// Entries are kept as ordered JSON objects so rewriting a lock file preserves
// existing key order exactly like the TS implementation.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Skills;

internal sealed class LocalLockFile
{
    public JsonNode? Version { get; set; } = JsonValue.Create(1.0);
    public JsonObject Skills { get; set; } = new();
}

internal static class LocalLock
{
    private const string FileName = "skills-lock.json";
    private const int CurrentVersion = 1;

    /// Serializes read-modify-write cycles between threads of one process.
    private static readonly object Gate = new();

    public static string GetPath(string? cwd = null) => NodePath.Join(cwd ?? Sys.Cwd(), FileName);

    private static LocalLockFile Empty() => new();

    public static LocalLockFile Read(string? cwd = null)
    {
        var dir = cwd ?? Sys.Cwd();
        JsonNode? parsed;
        try
        {
            parsed = Json.Parse(File.ReadAllText(GetPath(dir)));
        }
        catch
        {
            return Empty();
        }
        var version = Json.AsNumber(Json.Get(parsed, "version"));
        if (version == null || !Json.TruthyProp(parsed, "skills")) return Empty();
        if (version < CurrentVersion) return Empty();
        var skills = Json.Get(parsed, "skills") as JsonObject ?? new JsonObject();
        foreach (var (_, entry) in skills.ToList())
        {
            if (entry is JsonObject e && Json.Str(e, "sourceType") == "local" && Json.Str(e, "source") is { } src && !NodePath.IsAbsolute(src))
                e["source"] = NodePath.Resolve(dir, src);
        }
        ((JsonObject)parsed!).Remove("skills");
        return new LocalLockFile { Version = Json.Clone(Json.Get(parsed, "version")), Skills = skills };
    }

    private static string PortableLocalSource(string source, string lockDir)
    {
        var absolute = NodePath.IsAbsolute(source) ? source : NodePath.Resolve(lockDir, source);
        var rel = NodePath.Relative(lockDir, absolute);
        if (NodePath.IsAbsolute(rel)) return string.Join("/", absolute.Split(NodePath.Sep));
        var portable = string.Join("/", rel.Split(NodePath.Sep));
        if (portable.Length == 0) return ".";
        if (portable == ".." || portable.StartsWith("../")) return portable;
        return "./" + portable;
    }

    public static void Write(LocalLockFile lockFile, string? cwd = null)
    {
        var dir = cwd ?? Sys.Cwd();
        var sorted = new JsonObject();
        foreach (var key in lockFile.Skills.Select(kv => kv.Key).OrderBy(k => k, StringComparer.Ordinal).ToList())
        {
            var entry = Json.Clone(lockFile.Skills[key]);
            if (entry is JsonObject e && Json.Str(e, "sourceType") == "local" && Json.Str(e, "source") is { } src)
                e["source"] = PortableLocalSource(src, dir);
            sorted[key] = entry;
        }
        var root = new JsonObject { ["version"] = Json.Clone(lockFile.Version), ["skills"] = sorted };
        File.WriteAllText(GetPath(dir), Json.Stringify(root) + "\n");
    }

    /// SHA-256 over all files in a skill directory, sorted with localeCompare by
    /// relative path. .git and node_modules directories are skipped.
    public static string ComputeSkillFolderHash(string skillDir)
    {
        var files = new List<(string Rel, byte[] Content)>();
        Collect(skillDir, skillDir, files);
        var sorted = Collate.StableSort(files, (a, b) => Collate.LocaleCompare(a.Rel, b.Rel));
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var (rel, content) in sorted)
        {
            sha.AppendData(Encoding.UTF8.GetBytes(rel));
            sha.AppendData(content);
        }
        return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
    }

    private static void Collect(string baseDir, string current, List<(string, byte[])> output)
    {
        foreach (var e in Fs.ReadDir(current))
        {
            if (e.IsDirectory)
            {
                if (e.Name is ".git" or "node_modules") continue;
                Collect(baseDir, e.FullPath, output);
            }
            else if (e.IsFile)
            {
                output.Add((NodePath.Relative(baseDir, e.FullPath).Replace('\\', '/'), File.ReadAllBytes(e.FullPath)));
            }
        }
    }

    public static void AddSkill(string name, JsonObject entry, string? cwd = null)
    {
        lock (Gate)
        {
            var l = Read(cwd);
            l.Skills[name] = entry;
            Write(l, cwd);
        }
    }

    public static bool RemoveSkill(string name, string? cwd = null)
    {
        lock (Gate)
        {
            var l = Read(cwd);
            if (!l.Skills.Remove(name)) return false;
            Write(l, cwd);
            return true;
        }
    }
}
