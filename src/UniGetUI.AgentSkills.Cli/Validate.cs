// `skills validate` — check skills against the Agent Skills specification
// (https://agentskills.io/specification) before publishing them
// (extension; not in the reference CLI). Runs offline.

using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static Skills.Ansi;

namespace Skills;

internal sealed class ValidateOptions
{
    public bool Fix { get; set; }
    public bool Strict { get; set; }
    public bool Json { get; set; }
    public bool Help { get; set; }
}

internal enum Severity
{
    Info,
    Warning,
    Error,
}

internal sealed record Diagnostic(Severity Severity, string Code, string Message);

internal sealed class SkillReport
{
    /// The frontmatter `name` when it is a string.
    public string? Name { get; init; }

    /// SKILL.md path relative to the validated root, `/`-separated.
    public required string Rel { get; init; }

    public required List<Diagnostic> Diagnostics { get; init; }
}

internal sealed class PathReport
{
    public required string DisplayRoot { get; init; }
    public required string Root { get; init; }
    public required List<SkillReport> Skills { get; init; }
    public required List<Diagnostic> Repository { get; init; }
    public required List<string> Fixed { get; init; }

    private IEnumerable<Diagnostic> AllDiagnostics => Skills.SelectMany(s => s.Diagnostics).Concat(Repository);

    public int Errors => AllDiagnostics.Count(d => d.Severity == Severity.Error);

    public int Warnings => AllDiagnostics.Count(d => d.Severity == Severity.Warning);

    public bool Failed(bool strict) => Skills.Count == 0 || Errors > 0 || (strict && Warnings > 0);
}

internal sealed class ValidateFailure(string message) : Exception(message);

internal static partial class ValidateCommand
{
    /// Install tracking keys written under `metadata` by `gh skill install` (and
    /// similar tools), in the order they are reported.
    public static readonly string[] InstallMetadataKeys =
    [
        "github-owner",
        "github-repo",
        "github-ref",
        "github-sha",
        "github-tree-sha",
        "github-path",
        "github-pinned",
        "local-path",
    ];

    private static readonly string[] KnownFields = ["name", "description", "license", "compatibility", "metadata", "allowed-tools"];

    private const int MaxNameLen = 64;
    private const int MaxDescriptionLen = 1024;
    private const int MaxCompatibilityLen = 500;
    private const int MaxLines = 500;
    private const int MaxDiscoveryDepth = 8;

    public static string Help() =>
        "Usage: skills validate [path...] [options]\n\nCheck skills against the Agent Skills specification (https://agentskills.io/specification).\n\nOptions:\n  --fix                 Remove install tracking metadata from SKILL.md files\n  --strict              Exit with status 1 on warnings as well as errors\n  --json                Output as JSON\n  -h, --help            Show this help message\n\nExamples:\n  skills validate\n  skills validate skills/my-skill --strict";

    public static void PrintHelp() => Term.OutLine(Help());

    public static (List<string> Paths, ValidateOptions Options, List<string> Errors) ParseOptions(IReadOnlyList<string> args)
    {
        var paths = new List<string>();
        var o = new ValidateOptions();
        var errors = new List<string>();
        foreach (var a in args)
        {
            switch (a)
            {
                case "":
                    break;
                case "--fix":
                    o.Fix = true;
                    break;
                case "--strict":
                    o.Strict = true;
                    break;
                case "--json":
                    o.Json = true;
                    break;
                case "--help" or "-h":
                    o.Help = true;
                    break;
                default:
                    if (a.StartsWith('-')) errors.Add($"Unknown option: {a}");
                    else paths.Add(a);
                    break;
            }
        }
        return (paths, o, errors);
    }

    /// Number of Unicode scalar values (Rust `chars().count()`).
    private static int CharCount(string s) => s.EnumerateRunes().Count();

    /// `^[a-z0-9]+(-[a-z0-9]+)*$`, at most 64 characters.
    public static bool IsValidSkillName(string name)
    {
        if (name.Length == 0 || CharCount(name) > MaxNameLen) return false;
        if (name.StartsWith('-') || name.EndsWith('-') || name.Contains("--", StringComparison.Ordinal)) return false;
        return name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
    }

    private static string StripBom(string text) => text.StartsWith('﻿') ? text[1..] : text;

    /// Content of a line without its `\n` / `\r\n` terminator.
    private static string LineContent(string line)
    {
        var l = line.EndsWith('\n') ? line[..^1] : line;
        return l.EndsWith('\r') ? l[..^1] : l;
    }

    /// Rust `str::split_inclusive('\n')`.
    private static List<string> SplitInclusive(string text)
    {
        var lines = new List<string>();
        var start = 0;
        while (start < text.Length)
        {
            var nl = text.IndexOf('\n', start);
            if (nl < 0)
            {
                lines.Add(text[start..]);
                break;
            }
            lines.Add(text[start..(nl + 1)]);
            start = nl + 1;
        }
        return lines;
    }

    /// Rust `str::trim_end` / `trim` (Unicode White_Space).
    private static string RTrimEnd(string s) => s.TrimEnd();

    private static bool IsBlank(string s) => s.Trim().Length == 0;

    /// Split SKILL.md text (BOM already stripped) into (frontmatter YAML, body).
    /// The file must start with a `---` line; the first later line that is `---`
    /// (trailing whitespace allowed) closes the block.
    public static (string Yaml, string Body)? SplitFrontmatter(string text)
    {
        var offset = 0;
        int? yamlStart = null;
        var i = 0;
        foreach (var line in SplitInclusive(text))
        {
            var content = LineContent(line);
            if (i == 0)
            {
                if (content != "---" || !line.EndsWith('\n')) return null;
                yamlStart = line.Length;
            }
            else if (RTrimEnd(content) == "---")
            {
                var start = yamlStart!.Value;
                return (text[start..offset], text[(offset + line.Length)..]);
            }
            offset += line.Length;
            i++;
        }
        return null;
    }

    private static int LineCount(string text)
    {
        if (text.Length == 0) return 0;
        var n = text.Count(c => c == '\n');
        return text.EndsWith('\n') ? n : n + 1;
    }

    [GeneratedRegex(@"!?\[[^\]]*\]\(([^)\s]+)(?:\s+""[^""]*"")?\)")]
    private static partial Regex LinkRegex();

    [GeneratedRegex("`[^`]*`")]
    private static partial Regex InlineCodeRegex();

    private static string? FenceMarker(string line)
    {
        var t = line.TrimStart();
        if (t.StartsWith("```", StringComparison.Ordinal)) return "```";
        if (t.StartsWith("~~~", StringComparison.Ordinal)) return "~~~";
        return null;
    }

    /// Distinct inline link/image targets in a Markdown body, in order of first
    /// appearance, ignoring fenced code blocks and inline code spans.
    public static List<string> ExtractLinkTargets(string body)
    {
        var output = new List<string>();
        string? fence = null;
        foreach (var raw in body.Split('\n'))
        {
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            if (fence != null)
            {
                if (line.TrimStart().StartsWith(fence, StringComparison.Ordinal)) fence = null;
                continue;
            }
            if (FenceMarker(line) is { } marker)
            {
                fence = marker;
                continue;
            }
            var stripped = InlineCodeRegex().Replace(line, "");
            foreach (Match m in LinkRegex().Matches(stripped))
            {
                var target = m.Groups[1].Value;
                if (!output.Contains(target)) output.Add(target);
            }
        }
        return output;
    }

    /// The local path a link target refers to, or null for anchors, absolute
    /// paths, URLs and other targets that are not checked.
    public static string? LocalLinkPath(string target)
    {
        var lower = target.ToLowerInvariant();
        if (target.StartsWith('#') || target.StartsWith('/') || target.StartsWith('<') || target.Contains("://", StringComparison.Ordinal)
            || lower.StartsWith("mailto:", StringComparison.Ordinal) || lower.StartsWith("tel:", StringComparison.Ordinal)
            || lower.StartsWith("data:", StringComparison.Ordinal))
            return null;
        var cut = target.IndexOfAny(['#', '?']);
        var path = cut >= 0 ? target[..cut] : target;
        var decoded = UrlUtil.DecodeUriComponent(path) ?? path;
        return decoded.Length == 0 ? null : decoded;
    }

    private static List<Diagnostic> CheckLinks(string body, string skillDir)
    {
        var output = new List<Diagnostic>();
        foreach (var target in ExtractLinkTargets(body))
        {
            if (LocalLinkPath(target) is not { } path) continue;
            var resolved = NodePath.Resolve(skillDir, path);
            if (!NodePath.IsPathSafe(skillDir, resolved))
                output.Add(new Diagnostic(Severity.Warning, "link-outside-skill", $"link to \"{Sanitize.Metadata(target)}\" points outside the skill directory"));
            else if (!Fs.Exists(resolved))
                output.Add(new Diagnostic(Severity.Warning, "broken-link", $"link to \"{Sanitize.Metadata(target)}\" points to a file that does not exist"));
        }
        return output;
    }

    /// Install tracking keys present under a `metadata` mapping, in report order.
    public static List<string> InstallKeysPresent(JsonObject fm) =>
        Json.Get(fm, "metadata") is JsonObject m ? InstallMetadataKeys.Where(m.ContainsKey).ToList() : [];

    /// serde_yaml → serde_json maps non-finite floats to null.
    private static JsonNode? Normalize(JsonNode? v) =>
        v is JsonValue jv && Json.AsString(jv) == null && Json.AsNumber(jv) is { } d && !double.IsFinite(d) ? null : v;

    private static bool IsStringValue(JsonNode? v) => Json.AsString(v) != null;

    /// Parse YAML the way the Rust port does (insertion-ordered mappings).
    /// Returns null when the YAML is invalid or not a mapping.
    private static JsonObject? ParseMapping(string yaml)
    {
        try
        {
            return Frontmatter.ParseYaml(yaml, YamlFlavor.Serde) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    /// Validate one SKILL.md. `dirName` is the name of its directory, `atRoot`
    /// whether it sits directly in the validated root, `skillDir` its directory
    /// on disk (for link checks).
    public static (string? Name, List<Diagnostic> Diagnostics) CheckSkill(string raw, string dirName, bool atRoot, string skillDir)
    {
        var text = StripBom(raw);
        var d = new List<Diagnostic>();
        if (SplitFrontmatter(text) is not (var yaml, var body))
        {
            d.Add(new Diagnostic(Severity.Error, "frontmatter-missing", "SKILL.md has no YAML frontmatter"));
            return (null, d);
        }
        if (ParseMapping(yaml) is not { } fm)
        {
            d.Add(new Diagnostic(Severity.Error, "frontmatter-invalid", "frontmatter is not valid YAML"));
            return (null, d);
        }

        string? name = null;
        var nameNode = Normalize(Json.Get(fm, "name"));
        if (nameNode == null)
        {
            d.Add(new Diagnostic(Severity.Error, "name-missing", "required field \"name\" is missing"));
        }
        else if (Json.AsString(nameNode) is { } n)
        {
            var safe = Sanitize.Metadata(n);
            if (!IsValidSkillName(n))
                d.Add(new Diagnostic(Severity.Error, "name-format", $"name \"{safe}\" must be 1-64 lowercase letters, digits and hyphens, with no leading, trailing or consecutive hyphens"));
            if (n != dirName)
                d.Add(new Diagnostic(atRoot ? Severity.Warning : Severity.Error, "name-mismatch", $"name \"{safe}\" does not match its directory name \"{Sanitize.Metadata(dirName)}\""));
            name = n;
        }
        else
        {
            d.Add(new Diagnostic(Severity.Error, "name-type", "\"name\" must be a string"));
        }

        var descNode = Normalize(Json.Get(fm, "description"));
        if (descNode == null)
        {
            d.Add(new Diagnostic(Severity.Error, "description-missing", "required field \"description\" is missing"));
        }
        else if (Json.AsString(descNode) is { } desc)
        {
            var len = CharCount(desc);
            if (IsBlank(desc))
                d.Add(new Diagnostic(Severity.Error, "description-empty", "\"description\" must not be empty"));
            else if (len > MaxDescriptionLen)
                d.Add(new Diagnostic(Severity.Error, "description-length", $"description is {len} characters; the maximum is {MaxDescriptionLen}"));
        }
        else
        {
            d.Add(new Diagnostic(Severity.Error, "description-type", "\"description\" must be a string"));
        }

        var tools = Normalize(Json.Get(fm, "allowed-tools"));
        if (tools is JsonArray)
            d.Add(new Diagnostic(Severity.Error, "allowed-tools-type", "\"allowed-tools\" must be a space-separated string, not a list"));
        else if (tools != null && !IsStringValue(tools))
            d.Add(new Diagnostic(Severity.Error, "allowed-tools-type", "\"allowed-tools\" must be a string"));

        var compat = Normalize(Json.Get(fm, "compatibility"));
        if (compat != null)
        {
            if (Json.AsString(compat) is { } cs)
            {
                var len = CharCount(cs);
                if (len > MaxCompatibilityLen)
                    d.Add(new Diagnostic(Severity.Warning, "compatibility-length", $"compatibility is {len} characters; the maximum is {MaxCompatibilityLen}"));
            }
            else
            {
                d.Add(new Diagnostic(Severity.Warning, "compatibility-length", "\"compatibility\" must be a string"));
            }
        }

        var metadata = Normalize(Json.Get(fm, "metadata"));
        var metadataOk = metadata switch
        {
            null => true,
            JsonObject m => m.All(kv => IsStringValue(kv.Value)),
            _ => false,
        };
        if (!metadataOk)
            d.Add(new Diagnostic(Severity.Warning, "metadata-type", "\"metadata\" should map string keys to string values"));

        var install = InstallKeysPresent(fm);
        if (install.Count > 0)
            d.Add(new Diagnostic(Severity.Warning, "install-metadata", $"metadata contains install tracking keys ({string.Join(", ", install)}); run \"skills validate --fix\" to remove them"));

        if (IsBlank(body))
            d.Add(new Diagnostic(Severity.Warning, "body-empty", "SKILL.md has no instructions after the frontmatter"));

        d.AddRange(CheckLinks(body, skillDir));

        foreach (var (k, _) in fm)
        {
            if (!KnownFields.Contains(k))
                d.Add(new Diagnostic(Severity.Info, "unknown-field", $"unknown frontmatter field \"{Sanitize.Metadata(k)}\""));
        }

        var lines = LineCount(raw);
        if (lines > MaxLines)
            d.Add(new Diagnostic(Severity.Info, "body-long", $"SKILL.md is {lines} lines; the recommended maximum is {MaxLines}"));

        return (name, d);
    }

    private static int LeadingSpaces(string s)
    {
        var n = 0;
        while (n < s.Length && s[n] is ' ' or '\t') n++;
        return n;
    }

    private static string YamlKeyOf(string content)
    {
        var t = content.Trim();
        var i = t.IndexOf(':');
        var key = i >= 0 ? t[..i].Trim() : t;
        if (key.Length >= 2 && ((key[0] == '"' && key[^1] == '"') || (key[0] == '\'' && key[^1] == '\''))) return key[1..^1];
        return key;
    }

    /// Remove install tracking keys from a block-style `metadata:` mapping in the
    /// frontmatter, touching only those lines (line endings and everything else
    /// preserved). Returns null when nothing changes.
    public static string? StripInstallMetadata(string raw)
    {
        var bom = raw.StartsWith('﻿') ? "﻿" : "";
        var text = raw[bom.Length..];
        var lines = SplitInclusive(text);
        if (lines.Count == 0 || LineContent(lines[0]) != "---") return null;
        var close = -1;
        for (var i = 1; i < lines.Count; i++)
        {
            if (RTrimEnd(LineContent(lines[i])) == "---")
            {
                close = i;
                break;
            }
        }
        if (close < 0) return null;
        var meta = -1;
        for (var i = 1; i < close; i++)
        {
            if (RTrimEnd(LineContent(lines[i])) == "metadata:")
            {
                meta = i;
                break;
            }
        }
        if (meta < 0) return null;
        var blockEnd = meta + 1;
        while (blockEnd < close)
        {
            var c = LineContent(lines[blockEnd]);
            if (!IsBlank(c) && LeadingSpaces(c) == 0) break;
            blockEnd++;
        }
        int blockStart = meta + 1, first = -1;
        for (var i = blockStart; i < blockEnd; i++)
        {
            if (!IsBlank(LineContent(lines[i])))
            {
                first = i;
                break;
            }
        }
        if (first < 0) return null;
        var childIndent = LeadingSpaces(LineContent(lines[first]));

        // Child entries: (start, end) line ranges; trailing blank lines are not
        // part of an entry.
        var entries = new List<(int Start, int End)>();
        var idx = blockStart;
        while (idx < blockEnd)
        {
            var c = LineContent(lines[idx]);
            if (IsBlank(c) || LeadingSpaces(c) != childIndent)
            {
                idx++;
                continue;
            }
            var start = idx;
            var end = idx + 1;
            var j = idx + 1;
            while (j < blockEnd)
            {
                var cj = LineContent(lines[j]);
                if (IsBlank(cj))
                {
                    j++;
                    continue;
                }
                if (LeadingSpaces(cj) > childIndent)
                {
                    j++;
                    end = j;
                    continue;
                }
                break;
            }
            entries.Add((start, end));
            idx = end;
        }

        var remove = new bool[lines.Count];
        var removedAny = false;
        foreach (var (s, e) in entries)
        {
            if (!InstallMetadataKeys.Contains(YamlKeyOf(LineContent(lines[s])))) continue;
            for (var r = s; r < e; r++) remove[r] = true;
            removedAny = true;
        }
        if (!removedAny) return null;
        var remaining = false;
        for (var i = blockStart; i < blockEnd; i++)
        {
            if (!remove[i] && !IsBlank(LineContent(lines[i])))
            {
                remaining = true;
                break;
            }
        }
        if (!remaining)
        {
            remove[meta] = true;
            for (var r = blockStart; r < blockEnd; r++) remove[r] = true;
        }
        var sb = new StringBuilder(bom);
        for (var i = 0; i < lines.Count; i++)
            if (!remove[i]) sb.Append(lines[i]);
        return sb.ToString();
    }

    /// SKILL.md files under `root` (relative, `/`-separated, ordinal order).
    /// Hidden directories, `.git` and `node_modules` are skipped; a directory
    /// with a SKILL.md is recorded and not descended into.
    public static List<string> DiscoverSkillFiles(string root)
    {
        var output = new List<string>();
        void Walk(string dir, string rel, int depth)
        {
            if (SkillDiscovery.HasSkillMd(dir))
            {
                output.Add(rel.Length == 0 ? "SKILL.md" : $"{rel}/SKILL.md");
                return;
            }
            if (depth >= MaxDiscoveryDepth) return;
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
                if (e.Name.StartsWith('.') || e.Name == "node_modules") continue;
                Walk(NodePath.Join(dir, e.Name), rel.Length == 0 ? e.Name : $"{rel}/{e.Name}", depth + 1);
            }
        }
        Walk(root, "", 0);
        return Collate.StableSort(output, string.CompareOrdinal);
    }

    /// Project agent install directories (`.agents/skills`, `.claude/skills`, …)
    /// checked for being gitignored. Only hidden directories count: a plain
    /// `skills/` directory is the usual publishing layout.
    private static List<string> InstallDirCandidates()
    {
        var output = new List<string>();
        foreach (var a in Agents.List)
            if (a.SkillsDir.StartsWith('.') && !output.Contains(a.SkillsDir)) output.Add(a.SkillsDir);
        return output;
    }

    private static bool ContainsInstalledSkill(string dir) =>
        Fs.TryReadDir(dir).Any(e =>
        {
            var p = NodePath.Join(dir, e.Name);
            return Fs.IsDir(p) && SkillDiscovery.HasSkillMd(p);
        });

    private static ProcOutput? RunGit(string root, params string[] args)
    {
        try
        {
            return Proc.Command("git", new[] { "-C", root }.Concat(args))
                .Env("GIT_TERMINAL_PROMPT", "0")
                .Env("GIT_OPTIONAL_LOCKS", "0")
                .Timeout(TimeSpan.FromSeconds(30))
                .Output();
        }
        catch (ProcException)
        {
            return null;
        }
    }

    private static List<Diagnostic> RepositoryChecks(string root)
    {
        var output = new List<Diagnostic>();
        bool? inWorkTree = null;
        foreach (var dir in InstallDirCandidates())
        {
            if (!ContainsInstalledSkill(NodePath.Join(root, dir))) continue;
            inWorkTree ??= RunGit(root, "rev-parse", "--is-inside-work-tree") is { } o && o.Success && o.StdoutText.Trim() == "true";
            if (inWorkTree != true) break;
            var ignored = RunGit(root, "check-ignore", "-q", dir) is not { } c || c.Success;
            if (!ignored)
                output.Add(new Diagnostic(Severity.Warning, "installed-skills-not-ignored",
                    $"{dir}/ contains installed skills but is not gitignored; add it to .gitignore so other authors' skills are not published"));
        }
        return output;
    }

    private static string? ReadUtf8Lossy(string path)
    {
        try
        {
            return SkillDiscovery.ReadUtf8(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// Validate one path argument (after `--fix`, when requested).
    /// <exception cref="ValidateFailure">The path is missing or not a skill.</exception>
    public static PathReport ValidatePath(string display, bool fix)
    {
        var abs = NodePath.Resolve(display);
        if (!Fs.Exists(abs)) throw new ValidateFailure($"Path does not exist: {display}");
        string root;
        List<string> files;
        bool isDir;
        if (Fs.IsDir(abs))
        {
            root = abs;
            files = DiscoverSkillFiles(abs);
            isDir = true;
        }
        else if (PreviewCommand.AsciiEqualsIgnoreCase(NodePath.Basename(abs), "SKILL.md"))
        {
            root = NodePath.Dirname(abs);
            files = [NodePath.Basename(abs)];
            isDir = false;
        }
        else
        {
            throw new ValidateFailure($"Not a skill directory or SKILL.md file: {display}");
        }

        var fixedFiles = new List<string>();
        if (fix)
        {
            foreach (var rel in files)
            {
                var full = NodePath.Join(root, rel);
                if (ReadUtf8Lossy(full) is not { } raw) continue;
                var hasKeys = SplitFrontmatter(StripBom(raw)) is (var yaml, _) && ParseMapping(yaml) is { } m && InstallKeysPresent(m).Count > 0;
                if (!hasKeys) continue;
                if (StripInstallMetadata(raw) is { } newText && newText != raw)
                {
                    try
                    {
                        File.WriteAllBytes(full, new UTF8Encoding(false).GetBytes(newText));
                        fixedFiles.Add(rel);
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // leave the file as is
                    }
                }
            }
        }

        var rootName = NodePath.Basename(root);
        var skills = new List<SkillReport>();
        foreach (var rel in files)
        {
            var full = NodePath.Join(root, rel);
            var skillDir = NodePath.Dirname(full);
            var atRoot = !rel.Contains('/');
            string dirName;
            if (atRoot)
            {
                dirName = rootName;
            }
            else
            {
                var parts = rel.Split('/');
                dirName = parts[^2];
            }
            var raw = ReadUtf8Lossy(full) ?? "";
            var (name, diagnostics) = CheckSkill(raw, dirName, atRoot, skillDir);
            skills.Add(new SkillReport { Name = name, Rel = rel, Diagnostics = diagnostics });
        }
        AddDuplicateNameDiagnostics(skills);

        var repository = isDir && skills.Count > 0 ? RepositoryChecks(root) : [];
        return new PathReport { DisplayRoot = display, Root = root, Skills = skills, Repository = repository, Fixed = fixedFiles };
    }

    public static void AddDuplicateNameDiagnostics(List<SkillReport> skills)
    {
        var names = skills.Select(s => (s.Name, s.Rel)).ToList();
        foreach (var s in skills)
        {
            if (s.Name is not { } n) continue;
            var others = names.Where(x => x.Name == n && x.Rel != s.Rel).Select(x => x.Rel).ToList();
            if (others.Count > 0)
                s.Diagnostics.Add(new Diagnostic(Severity.Error, "duplicate-name",
                    $"name \"{Sanitize.Metadata(n)}\" is also used by {string.Join(", ", others.Select(Sanitize.Metadata))}"));
        }
    }

    private static string SeverityName(Severity s) => s switch
    {
        Severity.Info => "info",
        Severity.Warning => "warning",
        _ => "error",
    };

    private static string SeverityLabel(Severity s) => s switch
    {
        Severity.Error => Pc.Red("error"),
        Severity.Warning => $"{Yellow}warning{Reset}",
        _ => $"{Dim}info{Reset}",
    };

    private static string StatusSymbol(Severity? worst) => worst switch
    {
        Severity.Error => Pc.Red("✗"),
        Severity.Warning => $"{Yellow}⚠{Reset}",
        _ => $"{Text}✓{Reset}",
    };

    private static string DiagnosticLine(Diagnostic d) => $"    {SeverityLabel(d.Severity)} {d.Code}: {d.Message}";

    public static string RenderText(PathReport r)
    {
        var lines = new List<string>();
        if (r.Fixed.Count > 0)
        {
            foreach (var f in r.Fixed) lines.Add($"Removed install metadata from {Sanitize.Metadata(f)}");
            lines.Add("");
        }
        lines.Add($"Validating skills in {r.DisplayRoot}");
        lines.Add("");
        if (r.Skills.Count == 0)
        {
            lines.Add("No skills found.");
            return string.Join("\n", lines) + "\n";
        }
        foreach (var s in r.Skills)
        {
            Severity? worst = s.Diagnostics.Count > 0 ? s.Diagnostics.Max(d => d.Severity) : null;
            var name = s.Name != null ? Sanitize.Metadata(s.Name) : "<no name>";
            lines.Add($"{StatusSymbol(worst)} {name} {Dim}{Sanitize.Metadata(s.Rel)}{Reset}");
            lines.AddRange(s.Diagnostics.Select(DiagnosticLine));
        }
        lines.Add("");
        if (r.Repository.Count > 0)
        {
            lines.Add("Repository");
            lines.AddRange(r.Repository.Select(DiagnosticLine));
            lines.Add("");
        }
        lines.Add($"Checked {r.Skills.Count} skill(s): {r.Errors} error(s), {r.Warnings} warning(s)");
        return string.Join("\n", lines) + "\n";
    }

    private static JsonObject DiagnosticJson(Diagnostic d) => new()
    {
        ["severity"] = SeverityName(d.Severity),
        ["code"] = d.Code,
        ["message"] = d.Message,
    };

    public static JsonObject RenderJsonValue(PathReport r)
    {
        var skills = new JsonArray();
        foreach (var s in r.Skills)
        {
            skills.Add((JsonNode)new JsonObject
            {
                ["name"] = s.Name,
                ["path"] = s.Rel,
                ["diagnostics"] = new JsonArray(s.Diagnostics.Select(d => (JsonNode?)DiagnosticJson(d)).ToArray()),
            });
        }
        return new JsonObject
        {
            ["root"] = r.Root,
            ["skills"] = skills,
            ["repository"] = new JsonArray(r.Repository.Select(d => (JsonNode?)DiagnosticJson(d)).ToArray()),
            ["fixed"] = new JsonArray(r.Fixed.Select(f => (JsonNode?)JsonValue.Create(f)).ToArray()),
            ["errors"] = r.Errors,
            ["warnings"] = r.Warnings,
        };
    }

    public static void Run(IReadOnlyList<string> args)
    {
        var (targets, o, errors) = ParseOptions(args);
        if (o.Help)
        {
            PrintHelp();
            return;
        }
        if (errors.Count > 0)
        {
            Term.ErrLine(string.Join("\n", errors));
            Term.Exit(1);
        }
        if (targets.Count == 0) targets.Add(".");
        foreach (var t in targets)
        {
            if (!Fs.Exists(NodePath.Resolve(t)))
            {
                Term.ErrLine($"Path does not exist: {t}");
                Term.Exit(1);
            }
        }
        var reports = new List<PathReport>();
        foreach (var t in targets)
        {
            try
            {
                reports.Add(ValidatePath(t, o.Fix));
            }
            catch (ValidateFailure e)
            {
                Term.ErrLine(e.Message);
                Term.Exit(1);
            }
        }
        if (o.Json)
        {
            JsonNode value = reports.Count == 1
                ? RenderJsonValue(reports[0])
                : new JsonArray(reports.Select(r => (JsonNode?)RenderJsonValue(r)).ToArray());
            Term.OutLine(Json.Stringify(value));
        }
        else
        {
            Term.Out(string.Join("\n", reports.Select(RenderText)));
        }
        if (reports.Any(r => r.Failed(o.Strict))) Term.ExitCode = 1;
    }
}
