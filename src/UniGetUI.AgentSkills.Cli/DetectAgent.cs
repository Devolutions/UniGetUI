// Detect whether the CLI runs inside an AI agent (port of detect-agent.ts,
// including the @vercel/detect-agent 1.2.3 heuristics it wraps).

namespace Skills;

internal sealed record AgentResult(string? Agent)
{
    public bool IsAgent => Agent != null;
    public string Name => Agent ?? "";
}

internal static class DetectAgent
{
    /// `determineAgent()` from @vercel/detect-agent.
    private static string? DetermineAgent()
    {
        if (Sys.Env("AI_AGENT") is { } raw)
        {
            var name = raw.Trim();
            if (name.Length > 0) return name is "github-copilot" or "github-copilot-cli" ? "github-copilot" : name;
        }
        if (Sys.EnvTruthy("CURSOR_TRACE_ID")) return "cursor";
        if (Sys.EnvTruthy("CURSOR_AGENT") || Sys.EnvRaw("CURSOR_EXTENSION_HOST_ROLE") == "agent-exec") return "cursor-cli";
        if (Sys.EnvTruthy("GEMINI_CLI")) return "gemini";
        if (Sys.EnvTruthy("CODEX_SANDBOX") || Sys.EnvTruthy("CODEX_CI") || Sys.EnvTruthy("CODEX_THREAD_ID")) return "codex";
        if (Sys.EnvTruthy("ANTIGRAVITY_AGENT")) return "antigravity";
        if (Sys.EnvTruthy("AUGMENT_AGENT")) return "augment-cli";
        if (Sys.EnvTruthy("OPENCODE_CLIENT")) return "opencode";
        if (Sys.EnvTruthy("CLAUDECODE") || Sys.EnvTruthy("CLAUDE_CODE")) return Sys.EnvTruthy("CLAUDE_CODE_IS_COWORK") ? "cowork" : "claude";
        if (Sys.EnvTruthy("REPL_ID")) return "replit";
        if (Sys.EnvTruthy("COPILOT_MODEL") || Sys.EnvTruthy("COPILOT_ALLOW_ALL") || Sys.EnvTruthy("COPILOT_GITHUB_TOKEN")) return "github-copilot";
        if (Directory.Exists("/opt/.devin") || File.Exists("/opt/.devin")) return "devin";
        return null;
    }

    private static bool StrongCursorSignal() => Sys.EnvTrimmed("CURSOR_AGENT") != null || Sys.EnvRaw("CURSOR_EXTENSION_HOST_ROLE") == "agent-exec";

    private static readonly Lazy<AgentResult> Cached = new(() =>
    {
        var agent = DetermineAgent();
        if (agent is "cursor" or "cursor-cli") agent = StrongCursorSignal() ? "cursor-cli" : null;
        if (agent != null) Telemetry.SetDetectedAgent(agent);
        return new AgentResult(agent);
    });

    /// Cached detection; also records the agent name for telemetry.
    public static AgentResult Detect() => Cached.Value;

    public static bool IsRunningInAgent() => Detect().IsAgent;

    /// Map a detected agent name to a skills-cli agent type.
    public static string? GetAgentType(string name) => name switch
    {
        "cursor" or "cursor-cli" => "cursor",
        "claude" or "cowork" => "claude-code",
        "devin" => "universal",
        "replit" => "replit",
        "gemini" => "gemini-cli",
        "codex" => "codex",
        "antigravity" => "antigravity",
        "augment-cli" => "augment",
        "opencode" => "opencode",
        "github-copilot" => "github-copilot",
        _ => null,
    };
}
