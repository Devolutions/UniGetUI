// Agent definitions and detection (port of agents.ts).
//
// Agents are kept in the same order as the TS `agents` record, because that
// order is observable (Object.keys(agents) drives prompts, --agent '*', error
// messages and lock-file content).

using System.Text.Json.Nodes;

namespace Skills;

internal sealed record AgentConfig(
    string Name,
    string DisplayName,
    string SkillsDir,
    string? GlobalSkillsDir,
    bool ShowInUniversalList = true,
    bool ShowInUniversalPrompt = true,
    bool CreateProjectSkillsDirByDefault = false);

internal static class Agents
{
    private sealed class Homes
    {
        public string Home = "";
        public string ConfigHome = "";
        public string CodexHome = "";
        public string ClaudeHome = "";
        public string VibeHome = "";
        public string HermesHome = "";
        public string AutohandHome = "";
        public string GrokHome = "";
        public string SarvamHome = "";
        public string? ZedAppData;
        public string? ZedFlatpakConfig;
    }

    private static Homes BuildHomes()
    {
        var home = Sys.HomeDir();
        string Or(string v, string dir) => Sys.EnvTrimmed(v) ?? NodePath.Join(home, dir);
        return new Homes
        {
            Home = home,
            ConfigHome = Sys.Env("XDG_CONFIG_HOME") ?? NodePath.Join(home, ".config"),
            CodexHome = Or("CODEX_HOME", ".codex"),
            ClaudeHome = Or("CLAUDE_CONFIG_DIR", ".claude"),
            VibeHome = Or("VIBE_HOME", ".vibe"),
            HermesHome = Or("HERMES_HOME", ".hermes"),
            AutohandHome = Or("AUTOHAND_HOME", ".autohand"),
            GrokHome = Or("GROK_HOME", ".grok"),
            SarvamHome = Or("SARVAM_HOME", ".sarvam"),
            ZedAppData = Sys.EnvTrimmed("APPDATA"),
            ZedFlatpakConfig = Sys.EnvTrimmed("FLATPAK_XDG_CONFIG_HOME"),
        };
    }

    /// Home directories and the agent table built from them. The CLI builds it
    /// once per process; a public API call builds it once per call, so a
    /// long-running host sees environment and home changes.
    private sealed record State(Homes H, List<AgentConfig> All);

    private static readonly Lazy<State> ProcessState = new(BuildState);

    private static State Current => Sys.Context is { } c
        ? (State)LazyInitializer.EnsureInitialized(ref c.AgentsCache, BuildState)
        : ProcessState.Value;

    private static State BuildState()
    {
        var h = BuildHomes();
        return new State(h, BuildAgents(h));
    }

    private static Homes H => Current.H;

    public static string Home => H.Home;

    private static bool Exists(string p) => Directory.Exists(p) || File.Exists(p);
    private static string Hm(string rel) => NodePath.Join(H.Home, rel);
    private static string Cfg(string rel) => NodePath.Join(H.ConfigHome, rel);
    private static string CwdPath(string rel) => NodePath.Join(Sys.Cwd(), rel);

    public static string GetOpenClawGlobalSkillsDir(string homeDir, Func<string, bool> pathExists)
    {
        if (pathExists(NodePath.Join(homeDir, ".openclaw"))) return NodePath.Join(homeDir, ".openclaw/skills");
        if (pathExists(NodePath.Join(homeDir, ".clawdbot"))) return NodePath.Join(homeDir, ".clawdbot/skills");
        if (pathExists(NodePath.Join(homeDir, ".moltbot"))) return NodePath.Join(homeDir, ".moltbot/skills");
        return NodePath.Join(homeDir, ".openclaw/skills");
    }

    public static bool IsZCodeInstalled(string homeDir, Func<string, bool> pathExists) =>
        pathExists(NodePath.Join(homeDir, ".zcode")) || pathExists("/Applications/ZCode.app");

    public static bool IsKimchiInstalled(string homeDir, Func<string, bool> pathExists) =>
        pathExists(NodePath.Join(homeDir, ".config", "kimchi"));

    public static bool IsMiniMaxCodeInstalled(string homeDir, Func<string, bool> pathExists) =>
        pathExists(NodePath.Join(homeDir, ".minimax")) || pathExists("/Applications/MiniMax Code.app");

    public static bool IsPositAssistantInstalled(string homeDir, Func<string, bool> pathExists) =>
        pathExists(NodePath.Join(homeDir, ".posit/assistant")) || pathExists(NodePath.Join(homeDir, ".positai"));

    private static bool PackageJsonHasDependency(string packageJsonPath, string dep)
    {
        try
        {
            var v = Json.Parse(File.ReadAllText(packageJsonPath));
            return Json.Truthy(Json.Get(Json.Get(v, "dependencies"), dep), Json.Has(Json.Get(v, "dependencies"), dep))
                || Json.Truthy(Json.Get(Json.Get(v, "devDependencies"), dep), Json.Has(Json.Get(v, "devDependencies"), dep));
        }
        catch
        {
            return false;
        }
    }

    private static List<AgentConfig> BuildAgents(Homes h)
    {
        string Hm(string rel) => NodePath.Join(h.Home, rel);
        string Cfg(string rel) => NodePath.Join(h.ConfigHome, rel);
        return
        [
            new("aider-desk", "AiderDesk", ".aider-desk/skills", Hm(".aider-desk/skills")),
            new("amp", "Amp", ".agents/skills", Cfg("agents/skills")),
            new("antigravity", "Antigravity", ".agents/skills", Hm(".gemini/antigravity/skills"), ShowInUniversalPrompt: false),
            new("antigravity-cli", "Antigravity CLI", ".agents/skills", Hm(".gemini/antigravity-cli/skills"), ShowInUniversalPrompt: false),
            new("astrbot", "AstrBot", "data/skills", Hm(".astrbot/data/skills")),
            new("autohand-code", "Autohand Code CLI", ".autohand/skills", NodePath.Join(h.AutohandHome, "skills")),
            new("augment", "Augment", ".augment/skills", Hm(".augment/skills")),
            new("bob", "IBM Bob", ".bob/skills", Hm(".bob/skills")),
            new("claude-code", "Claude Code", ".claude/skills", NodePath.Join(h.ClaudeHome, "skills"), CreateProjectSkillsDirByDefault: true),
            new("openclaw", "OpenClaw", "skills", GetOpenClawGlobalSkillsDir(h.Home, Exists)),
            new("cline", "Cline", ".agents/skills", NodePath.Join(h.Home, ".agents", "skills")),
            new("codearts-agent", "CodeArts Agent", ".codeartsdoer/skills", Hm(".codeartsdoer/skills")),
            new("codebuddy", "CodeBuddy", ".codebuddy/skills", Hm(".codebuddy/skills")),
            new("codemaker", "Codemaker", ".codemaker/skills", Hm(".codemaker/skills")),
            new("codestudio", "Code Studio", ".codestudio/skills", Hm(".codestudio/skills")),
            new("codex", "Codex", ".agents/skills", NodePath.Join(h.CodexHome, "skills")),
            new("command-code", "Command Code", ".commandcode/skills", Hm(".commandcode/skills")),
            new("continue", "Continue", ".continue/skills", Hm(".continue/skills")),
            new("cortex", "Cortex Code", ".cortex/skills", Hm(".snowflake/cortex/skills")),
            new("crush", "Crush", ".crush/skills", Hm(".config/crush/skills")),
            new("cursor", "Cursor", ".agents/skills", Hm(".cursor/skills")),
            new("deepagents", "Deep Agents", ".agents/skills", Hm(".deepagents/agent/skills"), ShowInUniversalPrompt: false),
            new("devin", "Devin for Terminal", ".devin/skills", Cfg("devin/skills")),
            new("dexto", "Dexto", ".agents/skills", Hm(".agents/skills"), ShowInUniversalPrompt: false),
            new("droid", "Droid", ".agents/skills", Hm(".factory/skills")),
            new("eve", "Eve", "agent/skills", null),
            new("firebender", "Firebender", ".agents/skills", Hm(".firebender/skills"), ShowInUniversalPrompt: false),
            new("forgecode", "ForgeCode", ".forge/skills", Hm(".forge/skills")),
            new("fx", "fx", ".fx/skills", Hm(".fx/skills")),
            new("gemini-cli", "Gemini CLI", ".agents/skills", Hm(".gemini/skills")),
            new("github-copilot", "GitHub Copilot", ".agents/skills", Hm(".copilot/skills")),
            new("goose", "Goose", ".goose/skills", Cfg("goose/skills")),
            new("grok", "Grok Build", ".grok/skills", NodePath.Join(h.GrokHome, "skills")),
            new("hermes-agent", "Hermes Agent", ".hermes/skills", NodePath.Join(h.HermesHome, "skills")),
            new("inference-sh", "inference.sh", ".inferencesh/skills", Hm(".inferencesh/skills")),
            new("jazz", "Jazz", ".jazz/skills", Hm(".jazz/skills")),
            new("junie", "Junie", ".junie/skills", Hm(".junie/skills")),
            new("iflow-cli", "iFlow CLI", ".iflow/skills", Hm(".iflow/skills")),
            new("kilo", "Kilo Code", ".agents/skills", Hm(".kilo/skills")),
            new("kimchi", "Kimchi", ".kimchi/skills", NodePath.Join(h.Home, ".config", "kimchi", "harness", "skills")),
            new("kimi-code-cli", "Kimi Code CLI", ".agents/skills", Hm(".agents/skills")),
            new("kiro-cli", "Kiro CLI", ".kiro/skills", Hm(".kiro/skills")),
            new("kode", "Kode", ".kode/skills", Hm(".kode/skills")),
            new("lingma", "Lingma", ".lingma/skills", Hm(".lingma/skills")),
            new("loaf", "Loaf", ".agents/skills", Hm(".agents/skills"), ShowInUniversalPrompt: false),
            new("mcpjam", "MCPJam", ".mcpjam/skills", Hm(".mcpjam/skills")),
            new("minimax-code", "MiniMax Code", ".minimax/skills", Hm(".minimax/skills")),
            new("mistral-vibe", "Mistral Vibe", ".vibe/skills", NodePath.Join(h.VibeHome, "skills")),
            new("moxby", "Moxby", ".moxby/skills", Hm(".moxby/skills")),
            new("mux", "Mux", ".mux/skills", Hm(".mux/skills")),
            new("opencode", "OpenCode", ".agents/skills", Cfg("opencode/skills")),
            new("openhands", "OpenHands", ".openhands/skills", Hm(".openhands/skills")),
            new("ona", "Ona", ".ona/skills", Hm(".ona/skills")),
            new("pi", "Pi", ".pi/skills", Hm(".pi/agent/skills")),
            new("posit-assistant", "Posit Assistant", ".posit/assistant/skills", Hm(".posit/assistant/skills")),
            new("qoder", "Qoder", ".qoder/skills", Hm(".qoder/skills")),
            new("qoder-cn", "Qoder CN", ".qoder/skills", Hm(".qoder-cn/skills")),
            new("qwen-code", "Qwen Code", ".qwen/skills", Hm(".qwen/skills")),
            new("replit", "Replit", ".agents/skills", Cfg("agents/skills"), ShowInUniversalList: false),
            new("reasonix", "Reasonix", ".reasonix/skills", Hm(".reasonix/skills")),
            new("rovodev", "Rovo Dev", ".rovodev/skills", Hm(".rovodev/skills")),
            new("roo", "Roo Code", ".roo/skills", Hm(".roo/skills")),
            new("sarvam-code", "Sarvam Code", ".agents/skills", Hm(".agents/skills"), ShowInUniversalPrompt: false),
            new("tabnine-cli", "Tabnine CLI", ".tabnine/agent/skills", Hm(".tabnine/agent/skills")),
            new("terramind", "Terramind", ".terramind/skills", Hm(".terramind/skills")),
            new("tinycloud", "Tinycloud", ".tinycloud/skills", Hm(".tinycloud/skills")),
            new("trae", "Trae", ".trae/skills", Hm(".trae/skills")),
            new("trae-cn", "Trae CN", ".trae/skills", Hm(".trae-cn/skills")),
            new("warp", "Warp", ".agents/skills", Hm(".agents/skills")),
            new("windsurf", "Windsurf", ".windsurf/skills", Hm(".codeium/windsurf/skills")),
            new("zed", "Zed", ".agents/skills", Hm(".agents/skills")),
            new("zcode", "ZCode", ".zcode/skills", Hm(".zcode/skills")),
            new("zencoder", "Zencoder", ".zencoder/skills", Hm(".zencoder/skills")),
            new("zenflow", "Zenflow", ".zencoder/skills", Hm(".zencoder/skills")),
            new("neovate", "Neovate", ".neovate/skills", Hm(".neovate/skills")),
            new("pochi", "Pochi", ".pochi/skills", Hm(".pochi/skills")),
            new("promptscript", "PromptScript", ".agents/skills", null, ShowInUniversalPrompt: false),
            new("adal", "AdaL", ".adal/skills", Hm(".adal/skills")),
            new("universal", "Universal", ".agents/skills", Cfg("agents/skills"), ShowInUniversalList: false),
        ];
    }

    public static IReadOnlyList<AgentConfig> List => Current.All;

    public static AgentConfig? Find(string name) => Current.All.FirstOrDefault(a => a.Name == name);

    /// An agent known to exist.
    public static AgentConfig Get(string name) => Find(name) ?? throw new ArgumentException($"unknown agent: {name}");

    public static List<string> AllNames() => Current.All.Select(a => a.Name).ToList();

    /// `config.detectInstalled()`
    public static bool DetectInstalled(string name)
    {
        var h = H;
        return name switch
        {
            "aider-desk" => Exists(Hm(".aider-desk")),
            "amp" => Exists(Cfg("amp")),
            "antigravity" => Exists(Hm(".gemini/antigravity")),
            "antigravity-cli" => Exists(Hm(".gemini/antigravity-cli")),
            "astrbot" => Exists(CwdPath("data/skills")) || Exists(Hm(".astrbot")),
            "autohand-code" => Exists(h.AutohandHome),
            "augment" => Exists(Hm(".augment")),
            "bob" => Exists(Hm(".bob")),
            "claude-code" => Exists(h.ClaudeHome),
            "openclaw" => Exists(Hm(".openclaw")) || Exists(Hm(".clawdbot")) || Exists(Hm(".moltbot")),
            "cline" => Exists(Hm(".cline")),
            "codearts-agent" => Exists(Hm(".codeartsdoer")),
            "codebuddy" => Exists(CwdPath(".codebuddy")) || Exists(Hm(".codebuddy")),
            "codemaker" => Exists(Hm(".codemaker")),
            "codestudio" => Exists(Hm(".codestudio")),
            "codex" => Exists(h.CodexHome) || Exists("/etc/codex"),
            "command-code" => Exists(Hm(".commandcode")),
            "continue" => Exists(CwdPath(".continue")) || Exists(Hm(".continue")),
            "cortex" => Exists(Hm(".snowflake/cortex")),
            "crush" => Exists(Hm(".config/crush")),
            "cursor" => Exists(Hm(".cursor")),
            "deepagents" => Exists(Hm(".deepagents")),
            "devin" => Exists(Cfg("devin")),
            "dexto" => Exists(Hm(".dexto")),
            "droid" => Exists(Hm(".factory")),
            "eve" => Exists(CwdPath("agent")) && PackageJsonHasDependency(CwdPath("package.json"), "eve"),
            "firebender" => Exists(Hm(".firebender")),
            "forgecode" => Exists(Hm(".forge")),
            "fx" => Exists(Hm(".fx")),
            "gemini-cli" => Exists(Hm(".gemini")),
            "github-copilot" => Exists(Hm(".copilot")),
            "goose" => Exists(Cfg("goose")),
            "grok" => Exists(h.GrokHome),
            "hermes-agent" => Exists(h.HermesHome),
            "inference-sh" => Exists(Hm(".inferencesh")),
            "jazz" => Exists(Hm(".jazz")) || Exists(CwdPath(".jazz")),
            "junie" => Exists(Hm(".junie")),
            "iflow-cli" => Exists(Hm(".iflow")),
            "kilo" => Exists(Hm(".kilo")) || Exists(Hm(".kilocode")),
            "kimchi" => IsKimchiInstalled(h.Home, Exists),
            "kimi-code-cli" => Exists(Hm(".kimi-code")) || Exists(Hm(".kimi")),
            "kiro-cli" => Exists(Hm(".kiro")),
            "kode" => Exists(Hm(".kode")),
            "lingma" => Exists(Hm(".lingma")),
            "loaf" => Exists(Hm(".loaf")),
            "mcpjam" => Exists(Hm(".mcpjam")),
            "minimax-code" => IsMiniMaxCodeInstalled(h.Home, Exists),
            "mistral-vibe" => Exists(h.VibeHome),
            "moxby" => Exists(Hm(".moxby")),
            "mux" => Exists(Hm(".mux")),
            "opencode" => Exists(Cfg("opencode")),
            "openhands" => Exists(Hm(".openhands")),
            "ona" => Exists(Hm(".ona")),
            "pi" => Exists(Hm(".pi/agent")),
            "posit-assistant" => IsPositAssistantInstalled(h.Home, Exists),
            "qoder" => Exists(Hm(".qoder")),
            "qoder-cn" => Exists(Hm(".qoder-cn")),
            "qwen-code" => Exists(Hm(".qwen")),
            "replit" => Exists(CwdPath(".replit")),
            "reasonix" => Exists(Hm(".reasonix")),
            "rovodev" => Exists(Hm(".rovodev")),
            "roo" => Exists(Hm(".roo")),
            "sarvam-code" => Exists(h.SarvamHome),
            "tabnine-cli" => Exists(Hm(".tabnine")),
            "terramind" => Exists(Hm(".terramind")),
            "tinycloud" => Exists(Hm(".tinycloud")),
            "trae" => Exists(Hm(".trae")),
            "trae-cn" => Exists(Hm(".trae-cn")),
            "warp" => Exists(Hm(".warp")),
            "windsurf" => Exists(Hm(".codeium/windsurf")),
            "zed" => Exists(Cfg("zed"))
                || (h.ZedAppData != null && Exists(NodePath.Join(h.ZedAppData, "Zed")))
                || (h.ZedFlatpakConfig != null && Exists(NodePath.Join(h.ZedFlatpakConfig, "zed"))),
            "zcode" => IsZCodeInstalled(h.Home, Exists),
            "zencoder" or "zenflow" => Exists(Hm(".zencoder")),
            "neovate" => Exists(Hm(".neovate")),
            "pochi" => Exists(Hm(".pochi")),
            "promptscript" => Exists(CwdPath(".promptscript")) || Exists(CwdPath("promptscript.yaml")),
            "adal" => Exists(Hm(".adal")),
            _ => false,
        };
    }

    public static List<string> DetectInstalledAgents() => Current.All.Where(a => DetectInstalled(a.Name)).Select(a => a.Name).ToList();

    /// `join('agent', 'subagents')`
    public static string EveSubagentsDir => NodePath.Join("agent", "subagents");

    /// Names of Eve subagent directories under agent/subagents/, sorted.
    public static List<string> GetEveSubagents(string cwd)
    {
        var dir = NodePath.Join(cwd, EveSubagentsDir);
        if (!Directory.Exists(dir)) return [];
        try
        {
            var names = new DirectoryInfo(dir).EnumerateDirectories().Select(d => d.Name).ToList();
            names.Sort(string.CompareOrdinal);
            return names;
        }
        catch
        {
            return [];
        }
    }

    public static List<string> GetUniversalAgents() =>
        Current.All.Where(a => a.SkillsDir == ".agents/skills" && a.ShowInUniversalList).Select(a => a.Name).ToList();

    public static List<string> GetVisibleUniversalAgents() =>
        Current.All.Where(a => a.SkillsDir == ".agents/skills" && a.ShowInUniversalList && a.ShowInUniversalPrompt).Select(a => a.Name).ToList();

    public static List<string> GetNonUniversalAgents() =>
        Current.All.Where(a => a.SkillsDir != ".agents/skills").Select(a => a.Name).ToList();

    public static bool IsUniversalAgent(string name) => Find(name)?.SkillsDir == ".agents/skills";
}
