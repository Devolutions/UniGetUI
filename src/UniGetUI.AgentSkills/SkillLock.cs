// Global lock file ~/.agents/.skill-lock.json (port of skill-lock.ts).

using System.Text.Json.Nodes;

namespace Skills;

internal sealed class SkillLockFile(JsonObject root)
{
    /// The whole file as an ordered object (unknown fields round-trip).
    public JsonObject Root { get; } = root;

    public JsonObject Skills
    {
        get
        {
            if (Root["skills"] is not JsonObject s)
            {
                s = new JsonObject();
                Root["skills"] = s;
            }
            return s;
        }
    }
}

internal static class SkillLock
{
    private const string LockFile = ".skill-lock.json";
    private const int CurrentVersion = 3;

    /// Serializes read-modify-write cycles between threads of one process
    /// (concurrent API calls in a host application).
    private static readonly object Gate = new();

    /// $XDG_STATE_HOME/skills/.skill-lock.json or ~/.agents/.skill-lock.json.
    public static string GetPath() =>
        Sys.Env("XDG_STATE_HOME") is { } xdg ? NodePath.Join(xdg, "skills", LockFile) : NodePath.Join(Sys.HomeDir(), ".agents", LockFile);

    private static SkillLockFile Empty() => new(new JsonObject
    {
        ["version"] = CurrentVersion,
        ["skills"] = new JsonObject(),
        ["dismissed"] = new JsonObject(),
    });

    /// Read the lock file; old versions or invalid content yield an empty lock.
    public static SkillLockFile Read()
    {
        JsonNode? parsed;
        try
        {
            parsed = Json.Parse(File.ReadAllText(GetPath()));
        }
        catch
        {
            return Empty();
        }
        if (parsed is not JsonObject root) return Empty();
        var version = Json.AsNumber(root["version"]);
        if (version == null || !Json.TruthyProp(root, "skills")) return Empty();
        if (version < CurrentVersion) return Empty();
        return new SkillLockFile(root);
    }

    public static void Write(SkillLockFile l)
    {
        var path = GetPath();
        Directory.CreateDirectory(NodePath.Dirname(path));
        File.WriteAllText(path, Json.Stringify(l.Root));
    }

    /// GITHUB_TOKEN or GH_TOKEN. Stored GitHub CLI credentials are never
    /// extracted; callers fall back to `gh api` or a normal git clone instead.
    public static string? GetGitHubToken() => Sys.Env("GITHUB_TOKEN") ?? Sys.Env("GH_TOKEN");

    public static void AddSkill(string name, JsonObject entry)
    {
        lock (Gate)
        {
            var l = Read();
            var now = Sys.NowIso();
            var installedAt = Json.Get(l.Skills[name], "installedAt") is { } existing ? Json.Clone(existing) : JsonValue.Create(now);
            entry["installedAt"] = installedAt;
            entry["updatedAt"] = now;
            // Assigning a fresh object to an existing JS key keeps its position.
            l.Skills[name] = entry;
            Write(l);
        }
    }

    public static bool RemoveSkill(string name)
    {
        lock (Gate)
        {
            var l = Read();
            if (!l.Skills.ContainsKey(name)) return false;
            l.Skills.Remove(name);
            Write(l);
            return true;
        }
    }

    public static JsonNode? GetSkill(string name) => Read().Skills.TryGetPropertyValue(name, out var v) ? v : null;

    public static JsonObject GetAllSkills() => Read().Skills;

    public static bool IsPromptDismissed(string key) => Json.AsBool(Json.Get(Read().Root["dismissed"], key)) == true;

    public static void DismissPrompt(string key)
    {
        lock (Gate)
        {
            var l = Read();
            if (l.Root["dismissed"] is not JsonObject d)
            {
                d = new JsonObject();
                l.Root["dismissed"] = d;
            }
            d[key] = true;
            Write(l);
        }
    }

    public static List<string>? GetLastSelectedAgents() =>
        Read().Root["lastSelectedAgents"] is JsonArray a ? a.Select(Json.AsString).Where(x => x != null).Select(x => x!).ToList() : null;

    public static void SaveSelectedAgents(IEnumerable<string> agents)
    {
        lock (Gate)
        {
            var l = Read();
            l.Root["lastSelectedAgents"] = new JsonArray(agents.Select(a => (JsonNode)JsonValue.Create(a)!).ToArray());
            Write(l);
        }
    }
}
