// `skills add` (port of add.ts): options, shared helpers, security advisory,
// agent/scope/mode prompts and summary formatting.

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Skills;

internal sealed class AddOptions
{
    /// null means "not specified" (the scope prompt may be shown).
    public bool? Global { get; set; }
    public List<string>? Agent { get; set; }
    public bool Yes { get; set; }
    public List<string>? Skill { get; set; }
    public string? Metadata { get; set; }
    public bool List { get; set; }
    public bool All { get; set; }
    public bool FullDepth { get; set; }
    public bool Copy { get; set; }
    public List<string>? Subagent { get; set; }
    public bool Json { get; set; }

    /// `--pin <ref>` (extension): install from a ref and record it as pinned.
    public string? Pin { get; set; }

    public AddOptions Clone() => (AddOptions)MemberwiseClone();
}

internal static partial class AddCommand
{
    private const string EveAgentLabel = "eve agent";
    public static string? GetLockSource(string parsedUrl, string? normalized) => InstallRecords.GetLockSource(parsedUrl, normalized);

    public static string? GetProjectLockSourceUrl(string sourceType, string sourceUrl) => InstallRecords.GetProjectLockSourceUrl(sourceType, sourceUrl);

    // ─── Security advisory ───

    private static string RiskLabel(string? risk) => risk switch
    {
        "critical" => Pc.Red(Pc.Bold("Critical Risk")),
        "high" => Pc.Red("High Risk"),
        "medium" => Pc.Yellow("Med Risk"),
        "low" => Pc.Green("Low Risk"),
        "safe" => Pc.Green("Safe"),
        _ => Pc.Dim("--"),
    };

    private static long AlertCount(JsonNode? audit) => (long)(Json.AsNumber(Json.Get(audit, "alerts")) ?? 0);

    private static string SocketLabel(JsonNode? audit)
    {
        var count = AlertCount(audit);
        return count > 0 ? Pc.Red($"{count} alert{(count != 1 ? "s" : "")}") : Pc.Green("0 alerts");
    }

    private static string PadEnd(string s, int width)
    {
        var visible = Sanitize.StripTerminalEscapes(s).Length;
        return s + new string(' ', Math.Max(0, width - visible));
    }

    private static JsonNode? Partner(JsonNode? data, string key) => Json.Get(data, key) is { } v && Json.Truthy(v) ? v : null;

    private static List<string> BuildSecurityLines(JsonObject? audit, List<string> skills, string source)
    {
        if (audit == null) return [];
        if (!skills.Any(s => audit[s] is JsonObject { Count: > 0 })) return [];
        var nameWidth = Math.Min(skills.Count == 0 ? 0 : skills.Max(s => s.Length), 36);
        var lines = new List<string> { $"{PadEnd("", nameWidth + 2)}{PadEnd(Pc.Dim("Gen"), 18)}{PadEnd(Pc.Dim("Socket"), 18)}{Pc.Dim("Snyk")}" };
        foreach (var skill in skills)
        {
            var data = audit[skill];
            var name = skill.Length > nameWidth ? $"{skill[..Math.Max(0, nameWidth - 1)]}…" : skill;
            string Risk(string k) => Partner(data, k) is { } p ? RiskLabel(Json.Str(p, "risk")) : Pc.Dim("--");
            var ath = Risk("ath");
            var socket = Partner(data, "socket") is { } sock ? SocketLabel(sock) : Pc.Dim("--");
            var snyk = Risk("snyk");
            lines.Add($"{PadEnd(Pc.Cyan(name), nameWidth + 2)}{PadEnd(ath, 18)}{PadEnd(socket, 18)}{snyk}");
        }
        lines.Add("");
        lines.Add($"{Pc.Dim("Details:")} {Pc.Dim($"https://skills.sh/{source}")}");
        return lines;
    }

    private static JsonNode? BuildJsonSecurity(JsonObject? audit, string skill, string? source)
    {
        if (audit?[skill] is not JsonObject { Count: > 0 } data) return null;
        var output = new JsonObject();
        if (Partner(data, "ath") is { } a) output["gen"] = Json.Get(a, "risk")?.DeepClone();
        if (Partner(data, "socket") is { } s)
        {
            var n = AlertCount(s);
            output["socket"] = $"{n} alert{(n != 1 ? "s" : "")}";
        }
        if (Partner(data, "snyk") is { } k) output["snyk"] = Json.Get(k, "risk")?.DeepClone();
        if (!string.IsNullOrEmpty(source)) output["details"] = $"https://skills.sh/{source}";
        return output;
    }

    // ─── Formatting ───

    /// Replace homedir with `~` and cwd with `.` on whole path segments.
    public static string ShortenPath(string full, string cwd) => SyncCommand.ShortenPath(full, cwd);

    public static string FormatList(IReadOnlyList<string> items, int maxShow) => ListCommand.FormatList(items, maxShow);

    private static string FormatSkillPromptSubject(IReadOnlyList<Skill> skills)
    {
        var subject = FormatList(skills.Select(s => Pc.Cyan(SkillDiscovery.DisplayName(s))).ToList(), 3);
        return Sanitize.StripTerminalEscapes(subject).Length <= 80 ? subject : $"{skills.Count} selected skills";
    }

    public static string FormatEveInstallPromptMessage(IReadOnlyList<Skill> skills) =>
        $"Detected an eve project. Install {FormatSkillPromptSubject(skills)} for your {EveAgentLabel} to use?";

    private static (List<string> Universal, List<string> Symlinked) SplitAgentsByType(IEnumerable<string> list)
    {
        var universal = new List<string>();
        var symlinked = new List<string>();
        foreach (var a in list)
        {
            if (Agents.IsUniversalAgent(a)) universal.Add(Agents.Get(a).DisplayName);
            else symlinked.Add(Agents.Get(a).DisplayName);
        }
        return (universal, symlinked);
    }

    private static List<string> BuildAgentSummaryLines(List<string> targets, InstallMode mode)
    {
        var lines = new List<string>();
        var (universal, symlinked) = SplitAgentsByType(targets);
        if (mode == InstallMode.Symlink)
        {
            if (universal.Count > 0) lines.Add($"  {Pc.Green("universal:")} {FormatList(universal, 5)}");
            if (symlinked.Count > 0) lines.Add($"  {Pc.Dim("symlink →")} {FormatList(symlinked, 5)}");
        }
        else
        {
            lines.Add($"  {Pc.Dim("copy →")} {FormatList(targets.Select(a => Agents.Get(a).DisplayName).ToList(), 5)}");
        }
        return lines;
    }

    private sealed record InstallTarget(string Agent, string? Subagent);

    private static string TargetDisplayName(InstallTarget t)
    {
        var b = Agents.Get(t.Agent).DisplayName;
        return t.Subagent != null ? $"{b} ({t.Subagent})" : b;
    }

    private static List<InstallTarget> BuildInstallTargets(List<string> targets, List<string?> eve)
    {
        var output = new List<InstallTarget>();
        foreach (var a in targets)
        {
            if (a == "eve") output.AddRange(eve.Select(s => new InstallTarget(a, s)));
            else output.Add(new InstallTarget(a, null));
        }
        return output;
    }

    private static List<string> BuildTargetSummaryLines(List<InstallTarget> targets, InstallMode mode)
    {
        var lines = new List<string>();
        var root = targets.Where(t => t.Subagent == null).Select(t => t.Agent).ToList();
        var subs = targets.Where(t => t.Subagent != null).Select(TargetDisplayName).ToList();
        var (universal, symlinked) = SplitAgentsByType(root);
        if (mode == InstallMode.Symlink)
        {
            if (universal.Count > 0) lines.Add($"  {Pc.Green("universal:")} {FormatList(universal, 5)}");
            if (symlinked.Count > 0) lines.Add($"  {Pc.Dim("symlink →")} {FormatList(symlinked, 5)}");
            if (subs.Count > 0) lines.Add($"  {Pc.Dim("copy →")} {FormatList(subs, 5)}");
        }
        else
        {
            lines.Add($"  {Pc.Dim("copy →")} {FormatList(targets.Select(TargetDisplayName).ToList(), 5)}");
        }
        return lines;
    }

    private static List<string> EnsureUniversalAgents(IEnumerable<string> targets)
    {
        var output = targets.ToList();
        foreach (var ua in Agents.GetUniversalAgents())
            if (!output.Contains(ua)) output.Add(ua);
        return output;
    }

    private sealed record AddResult(string Skill, string Agent, string? PluginName, InstallResult R);

    private static List<string> BuildResultLines(IReadOnlyList<AddResult> results, List<string> targets)
    {
        var lines = new List<string>();
        var (universal, symlinkAgents) = SplitAgentsByType(targets);
        var ok = results.Where(r => !r.R.SymlinkFailed && !r.R.Skipped && !universal.Contains(r.Agent)).Select(r => r.Agent).ToList();
        var failed = results.Where(r => r.R.SymlinkFailed && !r.R.Skipped).Select(r => r.Agent).ToList();
        var skipped = results.Where(r => r.R.Skipped && r.R.SkipReason == "missing-agent-project-directory" && symlinkAgents.Contains(r.Agent)).Select(r => r.Agent).ToList();
        if (universal.Count > 0) lines.Add($"  {Pc.Green("universal:")} {FormatList(universal, 5)}");
        if (ok.Count > 0) lines.Add($"  {Pc.Dim("symlinked:")} {FormatList(ok, 5)}");
        if (failed.Count > 0) lines.Add($"  {Pc.Yellow("copied:")} {FormatList(failed, 5)}");
        if (skipped.Count > 0) lines.Add($"  {Pc.Yellow("skipped:")} {FormatList(skipped, 5)} {Pc.Dim("(project directory not found)")}");
        return lines;
    }

    /// Exit after a prompt was cancelled before anything was installed.
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void ExitInstallationCancelled()
    {
        Ui.Cancel("Installation cancelled");
        if (!Term.StdinIsTty())
        {
            Term.ErrLine("Interactive prompt required but stdin is not a TTY. Nothing was installed. Use --agent <name> (or --agent '*') and -y to run non-interactively.");
            Term.Exit(1);
        }
        Term.Exit(0);
    }

    private static T OrCancelled<T>(T? v) where T : class
    {
        if (v == null) ExitInstallationCancelled();
        return v;
    }

    private static SearchItem<string> AgentItem(string a, string? hint = null) => new(a, a, Agents.Get(a).DisplayName) { Hint = hint };

    /// Search prompt over `choices`, pre-selecting the last used agents.
    public static List<string>? PromptForAgents(string message, List<string> choices)
    {
        var last = SkillLock.GetLastSelectedAgents();
        var defaults = new[] { "claude-code", "opencode", "codex" }.Where(choices.Contains).ToList();
        var initial = last is { Count: > 0 } ? last.Where(choices.Contains).ToList() : [];
        if (initial.Count == 0) initial = defaults;
        var selected = SearchMultiselect.Run(new SearchMultiselectOptions<string>
        {
            Message = message,
            Items = choices.Select(a => AgentItem(a)).ToList(),
            InitialSelected = initial.Select(a => choices.IndexOf(a)).Where(i => i >= 0).ToList(),
            Required = true,
        });
        if (selected == null) return null;
        TrySaveSelectedAgents(selected);
        return selected;
    }

    private static void TrySaveSelectedAgents(List<string> selected)
    {
        try
        {
            SkillLock.SaveSelectedAgents(selected);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // best effort
        }
    }

    private static List<string>? SelectAgentsInteractive(bool global)
    {
        bool Supports(string a) => !global || Agents.Get(a).GlobalSkillsDir != null;
        var universal = Agents.GetUniversalAgents().Where(Supports).ToList();
        var visible = Agents.GetVisibleUniversalAgents().Where(Supports).ToList();
        var others = Agents.GetNonUniversalAgents().Where(a => a != "eve" && Supports(a)).ToList();
        var items = others.Select(a => AgentItem(a, global ? Agents.Get(a).GlobalSkillsDir ?? "" : Agents.Get(a).SkillsDir)).ToList();
        var last = SkillLock.GetLastSelectedAgents();
        var initial = last?.Where(a => others.Contains(a) && !universal.Contains(a)).Select(a => others.IndexOf(a)).Where(i => i >= 0).ToList() ?? [];
        var selected = SearchMultiselect.Run(new SearchMultiselectOptions<string>
        {
            Message = "Which agents do you want to install to?",
            Items = items,
            InitialSelected = initial,
            Locked = new LockedSection<string>
            {
                Title = "Universal (.agents/skills)",
                Items = visible.Select(a => AgentItem(a)).ToList(),
                HiddenCount = universal.Count - visible.Count,
            },
        });
        if (selected == null) return null;
        TrySaveSelectedAgents(selected);
        return selected;
    }

    [GeneratedRegex("^/p/[^/]+")]
    private static partial Regex PackPath();

    private static bool IsSkillsShPackUrl(string url)
    {
        if (WebUrl.Parse(url) is not { } u) return false;
        var host = u.Hostname.StartsWith("www.") ? u.Hostname[4..] : u.Hostname;
        return host == "skills.sh" && PackPath().IsMatch(u.Pathname);
    }

    private static void LogAutoSelectedSkills(List<(string Name, string Description)> entries)
    {
        if (entries.Count != 1)
        {
            Ui.Log.Info($"Installing all {entries.Count} skills");
            return;
        }
        Ui.Log.Info($"Skill: {Pc.Cyan(entries[0].Name)}");
        if (entries[0].Description.Length > 0) Ui.Log.Message(Pc.Dim(entries[0].Description));
    }

    /// Validated agent list, or exit(1) after logging the invalid names.
    private static List<string> ValidateAgentsOrExit(List<string> list)
    {
        var invalid = list.Where(a => Agents.Find(a) == null).ToList();
        if (invalid.Count == 0) return list;
        Ui.Log.Error($"Invalid agents: {string.Join(", ", invalid)}");
        Ui.Log.Info($"Valid agents: {string.Join(", ", Agents.AllNames())}");
        Term.Exit(1);
        return null!;
    }

    private static bool? IsSourcePrivate(string source) =>
        SourceParser.ParseOwnerRepo(source) is var (o, r) ? SourceParser.IsRepoPrivate(o, r) : false;

    private static bool SelectScope()
    {
        var options = new List<SelectOption<bool>>
        {
            new(false, "Project", "Install in current directory (committed with your project)"),
            new(true, "Global", "Install in home directory (available across all projects)"),
        };
        if (!Ui.Select("Installation scope", options, 0, out var v)) ExitInstallationCancelled();
        return v;
    }

    private static InstallMode? SelectMode()
    {
        var options = new List<SelectOption<InstallMode>>
        {
            new(InstallMode.Symlink, "Symlink (Recommended)", "Single source of truth, easy updates"),
            new(InstallMode.Copy, "Copy to all agents", "Independent copies for each agent"),
        };
        return Ui.Select("Installation method", options, 0, out var v) ? v : null;
    }

    private static bool IsWildcard(List<string>? list) => list?.Contains("*") == true;

    /// Resolve target agents for the well-known flow.
    private static List<string> ResolveTargetAgents(AddOptions options, Ui.Spinner spinner)
    {
        if (IsWildcard(options.Agent))
        {
            var all = Agents.AllNames();
            Ui.Log.Info($"Installing to all {all.Count} agents");
            return all;
        }
        if (options.Agent is { Count: > 0 } list) return ValidateAgentsOrExit(list);
        spinner.Start("Loading agents…");
        var installed = Agents.DetectInstalledAgents();
        spinner.Stop($"{Agents.List.Count} agents");
        if (installed.Count == 0)
        {
            if (options.Yes)
            {
                Ui.Log.Info("Installing to all agents");
                return Agents.AllNames();
            }
            Ui.Log.Info("Select agents to install skills to");
            return OrCancelled(PromptForAgents("Which agents do you want to install to?", Agents.AllNames()));
        }
        if (installed.Count == 1 || options.Yes)
        {
            Ui.Log.Info($"Installing to: {string.Join(", ", installed.Select(a => Pc.Cyan(Agents.Get(a).DisplayName)))}");
            return EnsureUniversalAgents(installed);
        }
        return OrCancelled(SelectAgentsInteractive(options.Global ?? false));
    }

    private static string DoneOutro() => $"{Pc.Green("Done!")}{Pc.Dim("  Review skills before use; they run with full agent permissions.")}";

    private static string Plural(int n) => n != 1 ? "s" : "";
}
