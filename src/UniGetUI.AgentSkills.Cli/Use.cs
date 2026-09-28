// `skills use` — generate a prompt for one skill, or launch an agent with it
// (port of use.ts).

using System.Text;

namespace Skills;

internal sealed class UseOptions
{
    public string? Skill { get; set; }
    public List<string>? Agent { get; set; }
    public bool FullDepth { get; set; }
    public bool Help { get; set; }
}

internal sealed class UseFailure(string message) : Exception(message);

internal static class UseCommand
{
    private static readonly (string Agent, string Command)[] SupportedUseAgents =
    [
        ("claude-code", "claude"),
        ("codex", "codex"),
        ("sarvam-code", "sarvam-code"),
    ];

    /// A skill to materialize: either in-memory files or a directory on disk.
    private sealed record UseSkill(string Name, string Description, string DirectoryName, string? RawContent, List<SnapshotFile>? Files, string? Path);

    /// A resolved skill copied into a temp directory. `Name`/`Description` are
    /// the selected skill's (as parsed from SKILL.md); the caller owns `TempRoot`.
    internal sealed record Materialized(string Name, string Description, string TempRoot, string SkillDir, string SkillMd, bool HasSupportingFiles);

    public static (List<string> Source, UseOptions Options, List<string> Errors) ParseOptions(IReadOnlyList<string> args)
    {
        var source = new List<string>();
        var o = new UseOptions();
        var errors = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a.Length == 0) continue;
            switch (a)
            {
                case "--help" or "-h":
                    o.Help = true;
                    break;
                case "--full-depth":
                    o.FullDepth = true;
                    break;
                case "--skill" or "-s":
                    if (i + 1 < args.Count && args[i + 1].Length > 0 && !args[i + 1].StartsWith('-'))
                    {
                        if (o.Skill != null) errors.Add("Only one --skill value can be provided");
                        else o.Skill = args[i + 1];
                        i++;
                    }
                    else
                    {
                        errors.Add($"{a} requires a skill name");
                    }
                    break;
                case "--agent" or "-a":
                    o.Agent ??= [];
                    var start = o.Agent.Count;
                    while (i + 1 < args.Count && args[i + 1].Length > 0 && !args[i + 1].StartsWith('-')) o.Agent.Add(args[++i]);
                    if (o.Agent.Count == start) errors.Add($"{a} requires an agent name");
                    break;
                default:
                    if (a.StartsWith('-')) errors.Add($"Unknown option: {a}");
                    else source.Add(a);
                    break;
            }
        }
        errors.AddRange(ValidateAgentOption(o.Agent));
        return (source, o, errors);
    }

    private static List<string> ValidateAgentOption(List<string>? values)
    {
        var errors = new List<string>();
        if (values is not { Count: > 0 }) return errors;
        var invalid = values.Where(a => a != "*" && Agents.Find(a) == null).ToList();
        if (values.Contains("*")) errors.Add("skills use --agent does not support '*'; specify exactly one agent.");
        if (values.Count > 1) errors.Add("skills use --agent accepts exactly one agent.");
        if (invalid.Count > 0) errors.Add($"Invalid agents: {string.Join(", ", invalid)}\nValid agents: {string.Join(", ", Agents.AllNames())}");
        return errors;
    }

    public static string BuildPrompt(string skillMd, string? supportDir, bool hasSupportingFiles)
    {
        var sections = new List<string>
        {
            "You are being given a Skill to execute for the user's next request.",
            "Use the following SKILL.md as your instructions:",
            $"<SKILL.md>\n{skillMd}\n</SKILL.md>",
        };
        if (hasSupportingFiles && supportDir != null)
            sections.Add($"Supporting files for this skill were downloaded to:\n{supportDir}\n\nWhen the SKILL.md references relative paths, read them from that directory.");
        return string.Join("\n\n", sections) + "\n";
    }

    private static string SupportedList => string.Join(", ", SupportedUseAgents.Select(x => x.Agent));

    private static string Help() =>
        $"Usage: skills use <source>[@<skill>] [options]\n\nGenerate a prompt for using one skill without installing it.\n\nOptions:\n  -s, --skill <skill>   Select the skill to use\n  -a, --agent <agent>   Start one supported agent interactively ({SupportedList})\n  --full-depth          Search nested directories like skills add --full-depth\n  -h, --help            Show this help message\n\nExamples:\n  skills use vercel-labs/agent-skills@web-design-guidelines | claude\n  skills use vercel-labs/agent-skills --skill web-design-guidelines --agent claude-code\n  skills use vercel-labs/agent-skills@web-design-guidelines --agent codex";

    private static string UnsupportedAgentError(string a) =>
        $"Running {Agents.Get(a).DisplayName} is not supported yet.\nSupported agents for skills use --agent: {SupportedList}";

    private static string MultipleError(string command, string source, IReadOnlyList<string> names)
    {
        var first = names.Count > 0 ? names[0] : "<skill>";
        var lines = new List<string> { "This source contains multiple skills. Specify exactly one skill:" };
        lines.AddRange(names.Select(n => $"  - {n}"));
        lines.Add("");
        lines.Add($"Examples:\n  skills {command} {source}@{first}\n  skills {command} {source} --skill {first}");
        return string.Join("\n", lines);
    }

    private static string NoMatchError(string selector, IEnumerable<string> names)
    {
        var lines = new List<string> { $"No matching skill found for: {selector}", "Available skills:" };
        lines.AddRange(names.Select(n => $"  - {n}"));
        return string.Join("\n", lines);
    }

    internal static string? ResolveSelector(string? sourceSel, string? optSel)
    {
        if (sourceSel != null && optSel != null)
        {
            if (sourceSel.ToLowerInvariant() != optSel.ToLowerInvariant())
                throw new UseFailure($"Conflicting skill selectors: source selects \"{sourceSel}\" but --skill selects \"{optSel}\". Provide one selector.");
            return optSel;
        }
        return optSel ?? sourceSel;
    }

    private static Skill SelectSkill(List<Skill> skills, string? selector, string source, string command)
    {
        if (skills.Count == 0) throw new UseFailure("No valid skills found. Skills require a SKILL.md with name and description.");
        var names = skills.Select(SkillDiscovery.DisplayName).ToList();
        if (selector == null) return skills.Count == 1 ? skills[0] : throw new UseFailure(MultipleError(command, source, names));
        var matched = SkillDiscovery.Filter(skills, [selector]);
        if (matched.Count == 0) throw new UseFailure(NoMatchError(selector, names));
        if (matched.Count > 1) throw new UseFailure($"Skill selector \"{selector}\" matched multiple skills.");
        return matched[0];
    }

    private static UseSkill SelectWellKnown(List<WellKnownSkill> skills, string? selector, string source, string command)
    {
        if (skills.Count == 0)
            throw new UseFailure("No skills found at this URL. Make sure the server has a /.well-known/agent-skills/index.json or /.well-known/skills/index.json file.");
        var names = skills.Select(s => s.InstallName).ToList();
        WellKnownSkill s;
        if (selector == null)
        {
            if (skills.Count != 1) throw new UseFailure(MultipleError(command, source, names));
            s = skills[0];
        }
        else
        {
            var l = selector.ToLowerInvariant();
            var m = skills.Where(x => x.InstallName.ToLowerInvariant() == l || x.Name.ToLowerInvariant() == l).ToList();
            if (m.Count == 0) throw new UseFailure(NoMatchError(selector, names));
            if (m.Count > 1) throw new UseFailure($"Skill selector \"{selector}\" matched multiple skills.");
            s = m[0];
        }
        return new UseSkill(s.Name, s.Description, s.InstallName, s.Content, s.Files, null);
    }

    private static void WriteSafeFile(string dir, string rel, byte[] contents)
    {
        var full = NodePath.Join(dir, rel);
        if (!NodePath.IsPathSafe(dir, full)) return;
        Directory.CreateDirectory(NodePath.Dirname(full));
        File.WriteAllBytes(full, contents);
    }

    private static void CopySkillDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var e in Fs.ReadDir(src))
        {
            if (e.Name == "metadata.json" || (e.IsDirectory && e.Name is ".git" or "__pycache__" or "__pypackages__")) continue;
            var s = e.FullPath;
            var d = NodePath.Join(dest, e.Name);
            if (!NodePath.IsPathSafe(dest, d)) continue;
            if (e.IsDirectory)
            {
                CopySkillDirectory(s, d);
                continue;
            }
            try
            {
                if (Fs.IsDir(s)) CopySkillDirectory(s, d);
                else if (Fs.IsFile(s)) Fs.CopyFile(s, d);
                else throw new FileNotFoundException($"ENOENT: no such file or directory, stat '{s}'");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException && e.IsSymlink)
            {
                Term.ErrLine($"Skipping broken symlink: {s}");
            }
        }
    }

    private static bool ContainsSupportingFiles(string root, string current)
    {
        foreach (var e in Fs.TryReadDir(current))
        {
            var rel = string.Join("/", NodePath.Relative(root, e.FullPath).Split(NodePath.Sep));
            if (e.IsDirectory)
            {
                if (ContainsSupportingFiles(root, e.FullPath)) return true;
            }
            else if (rel.ToLowerInvariant() != "skill.md")
            {
                return true;
            }
        }
        return false;
    }

    private static Materialized Materialize(UseSkill skill)
    {
        var tempRoot = Sys.MkdTemp("skills-use-");
        try
        {
            return MaterializeInto(tempRoot, skill);
        }
        catch
        {
            Git.TryCleanup(tempRoot);
            throw;
        }
    }

    private static Materialized MaterializeInto(string tempRoot, UseSkill skill)
    {
        var skillDir = NodePath.Join(tempRoot, Installer.SanitizeName(skill.DirectoryName.Length == 0 ? skill.Name : skill.DirectoryName));
        if (!NodePath.IsPathSafe(tempRoot, skillDir)) throw new UseFailure("Invalid skill name: potential path traversal detected");
        Directory.CreateDirectory(skillDir);
        string? raw;
        if (skill.Files != null)
        {
            foreach (var f in skill.Files) WriteSafeFile(skillDir, f.Path, f.Contents);
            raw = skill.RawContent;
        }
        else
        {
            CopySkillDirectory(skill.Path!, skillDir);
            raw = skill.RawContent;
        }
        var skillMd = raw ?? SkillDiscovery.ReadUtf8(NodePath.Join(skillDir, "SKILL.md"));
        return new Materialized(skill.Name, skill.Description, tempRoot, skillDir, skillMd, ContainsSupportingFiles(skillDir, skillDir));
    }

    /// Resolve `source` (plus an optional `--skill` selector) to exactly one
    /// skill and materialize it into a fresh temp directory. Shared by `skills use`
    /// and `skills preview`; `command` names the subcommand in the "multiple
    /// skills" error examples. Any clone/download temp directory is removed before
    /// returning; the caller owns `Materialized.TempRoot`.
    /// <exception cref="UseFailure">Any resolution error (message as printed).</exception>
    internal static Materialized ResolveSkill(string source, string? skill, bool fullDepth, string command)
    {
        string? cloneTemp = null;
        try
        {
            var parsed = SourceParser.Parse(source);
            var selector = ResolveSelector(parsed.SkillFilter, skill);
            var includeInternal = selector != null;
            var opts = new DiscoverOptions(includeInternal, fullDepth);
            UseSkill selected;
            if (parsed.Kind == "well-known")
            {
                List<WellKnownSkill> wk;
                try
                {
                    wk = WellKnown.FetchAllSkills(parsed.Url, includeInternal);
                }
                catch (ScopeNotFoundException e)
                {
                    throw new UseFailure(e.Message);
                }
                if (wk.Count > 0)
                {
                    selected = SelectWellKnown(wk, selector, source, command);
                }
                else
                {
                    var d = DownloadSource.Fetch(parsed.Url);
                    cloneTemp = d.TempDir;
                    var s = SelectSkill(SkillDiscovery.Discover(d.RootDir, null, opts), selector, source, command);
                    selected = new UseSkill(s.Name, s.Description, s.Name, s.RawContent, null, s.Path);
                }
            }
            else
            {
                var blobUsed = false;
                List<Skill> skills;
                if (parsed.Kind == "download")
                {
                    var d = DownloadSource.Fetch(parsed.Url);
                    cloneTemp = d.TempDir;
                    skills = SkillDiscovery.Discover(d.RootDir, null, opts);
                }
                else if (parsed.Kind == "local")
                {
                    var local = parsed.LocalPath ?? "";
                    if (!Fs.Exists(local)) throw new UseFailure($"Local path does not exist: {local}");
                    skills = SkillDiscovery.Discover(local, parsed.Subpath, opts);
                }
                else
                {
                    BlobInstallResult? blob = null;
                    if (parsed.Kind == "github" && !fullDepth && SourceParser.GetOwnerRepo(parsed) is { } or
                        && InstallRecords.BlobAllowedOwners.Contains(or.Split('/')[0].ToLowerInvariant()))
                        blob = Blob.TryBlobInstall(or, new BlobOptions(parsed.Subpath, selector, parsed.Ref, true, includeInternal));
                    if (blob != null)
                    {
                        blobUsed = true;
                        skills = blob.Skills;
                    }
                    else
                    {
                        cloneTemp = Git.CloneRepo(parsed.Url, parsed.Ref);
                        skills = SkillDiscovery.Discover(cloneTemp, parsed.Subpath, opts);
                    }
                }
                selected = ToUseSkill(SelectSkill(skills, selector, source, command), blobUsed);
            }
            return Materialize(selected);
        }
        catch (Exception e) when (e is SourceParseException or DiscoverException or DownloadException or ArchiveValidationException
                                      or GitCloneException or IOException or UnauthorizedAccessException)
        {
            throw new UseFailure(e.Message);
        }
        finally
        {
            Git.TryCleanup(cloneTemp);
        }
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string message)
    {
        Term.ErrLine(message);
        Term.Exit(1);
    }

    private static int LaunchAgent(string useAgent, string prompt)
    {
        var command = SupportedUseAgents.FirstOrDefault(x => x.Agent == useAgent).Command ?? throw new UseFailure(UnsupportedAgentError(useAgent));
        try
        {
            return Proc.Command(command, prompt).StatusInherit();
        }
        catch (ProcException e) when (e.Kind == ProcErrorKind.NotFound)
        {
            throw new UseFailure($"Could not launch {Agents.Get(useAgent).DisplayName}: command not found: {command}");
        }
        catch (ProcException e)
        {
            throw new UseFailure(e.Message);
        }
    }

    private static UseSkill ToUseSkill(Skill s, bool blobUsed)
    {
        if (s.Blob is { } b && blobUsed)
        {
            var md = b.Files.FirstOrDefault(f => f.Path.ToLowerInvariant() == "skill.md");
            var raw = s.RawContent ?? (md != null ? Encoding.UTF8.GetString(md.Contents) : "");
            return new UseSkill(s.Name, s.Description, s.Name, raw, b.Files, null);
        }
        return new UseSkill(s.Name, s.Description, s.Name, s.RawContent, null, s.Path);
    }

    public static void Run(List<string> sourceArgs, UseOptions options, List<string> parseErrors)
    {
        if (options.Help)
        {
            Term.OutLine(Help());
            return;
        }
        if (parseErrors.Count > 0) Fail(string.Join("\n", parseErrors));
        if (sourceArgs.Count == 0) Fail($"Missing required argument: source\n\n{Help()}");
        if (sourceArgs.Count > 1) Fail($"Expected one source, received {sourceArgs.Count}: {string.Join(", ", sourceArgs)}");
        var useAgent = options.Agent?.FirstOrDefault();
        if (useAgent != null && SupportedUseAgents.All(x => x.Agent != useAgent)) Fail(UnsupportedAgentError(useAgent));

        Materialized m;
        try
        {
            m = ResolveSkill(sourceArgs[0], options.Skill, options.FullDepth, "use");
        }
        catch (UseFailure e)
        {
            Fail(e.Message);
            return;
        }

        var prompt = BuildPrompt(m.SkillMd, m.SkillDir, m.HasSupportingFiles);
        if (useAgent != null)
        {
            int code;
            try
            {
                code = LaunchAgent(useAgent, prompt);
            }
            catch (UseFailure e)
            {
                Fail(e.Message);
                return;
            }
            if (code == 0) return;
            Term.Exit(code);
        }
        Term.Out(prompt);
    }
}
