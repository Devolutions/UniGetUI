// `skills preview` / `skills show` — show one skill's files and SKILL.md
// without installing it (extension; not in the reference CLI).
//
// Source resolution and skill selection are shared with `skills use`
// (UseCommand.ResolveSkill).

using System.Text;
using System.Text.Json.Nodes;
using static Skills.Ansi;

namespace Skills;

internal sealed class PreviewOptions
{
    public string? Skill { get; set; }
    public string? File { get; set; }
    public bool FullDepth { get; set; }
    public bool Json { get; set; }
    public bool NoPager { get; set; }
    public bool Help { get; set; }
}

/// A file of the previewed skill; `Path` is relative with `/` separators.
internal sealed record PreviewFile(string Path, long Size, bool Script);

internal static class PreviewCommand
{
    private static readonly string[] ScriptExtensions =
    [
        ".sh", ".bash", ".zsh", ".fish", ".ps1", ".psm1", ".bat", ".cmd", ".py", ".js", ".mjs", ".cjs",
        ".ts", ".rb", ".pl", ".php", ".lua", ".exe", ".dll", ".so", ".dylib",
    ];

    /// Bytes inspected for a NUL byte when deciding whether a file is binary.
    private const int BinarySniffBytes = 8000;

    public static string Help() =>
        "Usage: skills preview <source>[@<skill>] [options]\n\nShow a skill's files and SKILL.md without installing it.\n\nOptions:\n  -s, --skill <skill>   Select the skill to preview\n  --file <path>         Print one file of the skill instead of the overview\n  --full-depth          Search nested directories like skills add --full-depth\n  --json                Output as JSON\n  --no-pager            Do not page the output\n  -h, --help            Show this help message\n\nExamples:\n  skills preview vercel-labs/agent-skills@web-design-guidelines\n  skills preview ./my-skills --skill my-skill --file scripts/run.sh";

    public static void PrintHelp() => Term.OutLine(Help());

    public static (List<string> Sources, PreviewOptions Options, List<string> Errors) ParseOptions(IReadOnlyList<string> args)
    {
        var sources = new List<string>();
        var o = new PreviewOptions();
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
                case "--json":
                    o.Json = true;
                    break;
                case "--no-pager":
                    o.NoPager = true;
                    break;
                case "--skill" or "-s" or "--file":
                {
                    var isFile = a == "--file";
                    if (i + 1 < args.Count && args[i + 1].Length > 0 && !args[i + 1].StartsWith('-'))
                    {
                        var current = isFile ? o.File : o.Skill;
                        if (current != null) errors.Add($"Only one {(isFile ? "--file" : "--skill")} value can be provided");
                        else if (isFile) o.File = args[i + 1];
                        else o.Skill = args[i + 1];
                        i++;
                    }
                    else
                    {
                        errors.Add(isFile ? "--file requires a path" : $"{a} requires a skill name");
                    }
                    break;
                }
                default:
                    if (a.StartsWith('-')) errors.Add($"Unknown option: {a}");
                    else sources.Add(a);
                    break;
            }
        }
        return (sources, o, errors);
    }

    /// `N B` below 1 KiB, then one decimal `X.Y KB` / `X.Y MB` (half away from zero).
    public static string FormatSize(long bytes)
    {
        const long kb = 1024, mb = 1024 * 1024;
        if (bytes < kb) return $"{bytes} B";
        var (unit, div) = bytes < mb ? ("KB", kb) : ("MB", mb);
        var tenths = ((UInt128)(ulong)bytes * 10 + (UInt128)(ulong)(div / 2)) / (UInt128)(ulong)div;
        return $"{tenths / 10}.{tenths % 10} {unit}";
    }

    /// Whether a file can run code, judged by its (lowercased) extension.
    public static bool IsScriptPath(string path)
    {
        var slash = path.LastIndexOf('/');
        var name = (slash >= 0 ? path[(slash + 1)..] : path).ToLowerInvariant();
        var dot = name.LastIndexOf('.');
        return dot > 0 && ScriptExtensions.Contains(name[dot..]);
    }

    /// ASCII-only case-insensitive equality (Rust `eq_ignore_ascii_case`).
    internal static bool AsciiEqualsIgnoreCase(string a, string b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x is >= 'A' and <= 'Z') x = (char)(x + 32);
            if (y is >= 'A' and <= 'Z') y = (char)(y + 32);
            if (x != y) return false;
        }
        return true;
    }

    private static bool IsRootSkillMd(string path) => AsciiEqualsIgnoreCase(path, "SKILL.md");

    private static int FileOrder(string a, string b)
    {
        var c = IsRootSkillMd(b).CompareTo(IsRootSkillMd(a));
        return c != 0 ? c : string.CompareOrdinal(a, b);
    }

    /// Flat order: the root SKILL.md first, then ordinal by path.
    public static List<PreviewFile> SortFiles(IEnumerable<PreviewFile> files) =>
        Collate.StableSort(files, (a, b) => FileOrder(a.Path, b.Path));

    /// All regular files under `dir`, recursively, in SortFiles order.
    public static List<PreviewFile> ListFiles(string dir)
    {
        var output = new List<PreviewFile>();
        void Walk(string d, string prefix)
        {
            List<DirEntry> entries;
            try
            {
                entries = Fs.ReadDir(d);
            }
            catch
            {
                return;
            }
            foreach (var e in entries)
            {
                var full = NodePath.Join(d, e.Name);
                var rel = prefix.Length == 0 ? e.Name : $"{prefix}/{e.Name}";
                if (Fs.IsDir(full))
                {
                    Walk(full, rel);
                }
                else if (Fs.IsFile(full))
                {
                    long size;
                    try
                    {
                        var fi = new FileInfo(full);
                        size = fi.LinkTarget != null && fi.ResolveLinkTarget(true) is FileInfo target ? target.Length : fi.Length;
                    }
                    catch
                    {
                        continue;
                    }
                    output.Add(new PreviewFile(rel, size, IsScriptPath(rel)));
                }
            }
        }
        Walk(dir, "");
        return SortFiles(output);
    }

    private sealed class TreeNode
    {
        public readonly List<(string Name, PreviewFile File)> Files = [];
        public readonly List<(string Name, TreeNode Node)> Dirs = [];

        public void Insert(string[] parts, int index, PreviewFile file)
        {
            if (index == parts.Length - 1)
            {
                Files.Add((parts[index], file));
                return;
            }
            var i = Dirs.FindIndex(d => d.Name == parts[index]);
            if (i < 0)
            {
                Dirs.Add((parts[index], new TreeNode()));
                i = Dirs.Count - 1;
            }
            Dirs[i].Node.Insert(parts, index + 1, file);
        }

        public void Render(int depth, List<string> output)
        {
            var root = depth == 0;
            var files = Collate.StableSort(Files, (a, b) =>
            {
                var c = (root && IsRootSkillMd(b.Name)).CompareTo(root && IsRootSkillMd(a.Name));
                return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
            });
            var dirs = Collate.StableSort(Dirs, (a, b) => string.CompareOrdinal(a.Name, b.Name));
            var indent = string.Concat(Enumerable.Repeat("  ", depth + 1));
            foreach (var (name, f) in files)
                output.Add($"{indent}{Sanitize.Metadata(name)} ({FormatSize(f.Size)}){(f.Script ? " [script]" : "")}");
            foreach (var (name, node) in dirs)
            {
                output.Add($"{indent}{Sanitize.Metadata(name)}/");
                node.Render(depth + 1, output);
            }
        }
    }

    /// Indented file tree: files before directories at each level, ordinal by
    /// name, with the root SKILL.md first.
    public static List<string> RenderTree(IReadOnlyList<PreviewFile> files)
    {
        var root = new TreeNode();
        foreach (var f in files) root.Insert(f.Path.Split('/'), 0, f);
        var output = new List<string>();
        root.Render(0, output);
        return output;
    }

    private static string? FenceMarker(string line)
    {
        var t = line.TrimStart();
        if (t.StartsWith("```", StringComparison.Ordinal)) return "```";
        if (t.StartsWith("~~~", StringComparison.Ordinal)) return "~~~";
        return null;
    }

    /// Light terminal highlighting for SKILL.md: frontmatter and fenced code DIM,
    /// headings BOLD+CYAN.
    public static string HighlightMarkdown(string text)
    {
        var sb = new StringBuilder();
        var lines = text.Split('\n');
        var inFrontmatter = lines.Length > 0 && lines[0].TrimEnd() == "---";
        string? fence = null;
        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            var line = raw.EndsWith('\r') ? raw[..^1] : raw;
            var last = i + 1 == lines.Length;
            if (last && line.Length == 0) break;
            string styled;
            if (inFrontmatter)
            {
                if (i > 0 && line.TrimEnd() == "---") inFrontmatter = false;
                styled = $"{Dim}{line}{Reset}";
            }
            else if (fence != null)
            {
                if (line.TrimStart().StartsWith(fence, StringComparison.Ordinal)) fence = null;
                styled = $"{Dim}{line}{Reset}";
            }
            else if (FenceMarker(line) is { } marker)
            {
                fence = marker;
                styled = $"{Dim}{line}{Reset}";
            }
            else if (line.StartsWith('#'))
            {
                styled = $"{Bold}{Cyan}{line}{Reset}";
            }
            else
            {
                styled = line;
            }
            sb.Append(styled).Append('\n');
        }
        return sb.ToString();
    }

    /// The overview printed by `skills preview` (without `--json`/`--file`).
    public static string RenderOverview(string name, string description, IReadOnlyList<PreviewFile> files, string skillMd, bool highlight)
    {
        var sb = new StringBuilder();
        sb.Append($"{Bold}{Sanitize.Metadata(name)}{Reset}\n");
        sb.Append($"{Dim}{Sanitize.Metadata(description)}{Reset}\n");
        sb.Append('\n');
        sb.Append("Files:\n");
        foreach (var line in RenderTree(files)) sb.Append(line).Append('\n');
        sb.Append('\n');
        var scripts = files.Count(f => f.Script);
        if (scripts > 0)
        {
            sb.Append($"{Yellow}⚠ This skill includes {scripts} script file(s). Review them before installing.{Reset}\n");
            sb.Append('\n');
        }
        sb.Append($"{Dim}--- SKILL.md ---{Reset}\n");
        if (highlight)
        {
            sb.Append(HighlightMarkdown(skillMd));
        }
        else
        {
            sb.Append(skillMd);
            if (!skillMd.EndsWith('\n')) sb.Append('\n');
        }
        return sb.ToString();
    }

    private static string RenderJson(UseCommand.Materialized m, string source, IReadOnlyList<PreviewFile> files)
    {
        var arr = new JsonArray();
        foreach (var f in files)
            arr.Add((JsonNode)new JsonObject { ["path"] = f.Path, ["size"] = f.Size, ["script"] = f.Script });
        var o = new JsonObject
        {
            ["name"] = Sanitize.Metadata(m.Name),
            ["description"] = Sanitize.Metadata(m.Description),
            ["source"] = source,
            ["files"] = arr,
            ["skillMd"] = m.SkillMd,
        };
        return Json.Stringify(o);
    }

    /// Normalize a `--file` argument: `\` → `/`, leading `./` removed.
    public static string NormalizeFileArg(string path)
    {
        var p = path.Replace('\\', '/');
        return p.StartsWith("./", StringComparison.Ordinal) ? p[2..] : p;
    }

    /// Exact match first, then case-insensitive.
    public static PreviewFile? FindFile(IReadOnlyList<PreviewFile> files, string wanted)
    {
        var exact = files.FirstOrDefault(f => f.Path == wanted);
        if (exact != null) return exact;
        var lower = wanted.ToLowerInvariant();
        return files.FirstOrDefault(f => f.Path.ToLowerInvariant() == lower);
    }

    private static bool IsBinary(byte[] bytes) => Array.IndexOf(bytes, (byte)0, 0, Math.Min(bytes.Length, BinarySniffBytes)) >= 0;

    private static bool FindOnPath(string program)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (path == null) return false;
        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [];
        foreach (var dir in path.Split(Path.PathSeparator))
        {
            if (dir.Length == 0) continue;
            try
            {
                if (File.Exists(Path.Combine(dir, program))) return true;
                if (exts.Any(e => File.Exists(Path.Combine(dir, program + e)))) return true;
            }
            catch
            {
                // invalid PATH entry
            }
        }
        return false;
    }

    private static (string Program, List<string> Args)? PagerCommand()
    {
        if (Sys.Env("PAGER") is { } p)
        {
            var parts = p.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0) return (parts[0], parts[1..].ToList());
        }
        if (FindOnPath("less")) return ("less", ["-R"]);
        return null;
    }

    private static bool RunPager(string text)
    {
        if (PagerCommand() is not { } cmd) return false;
        try
        {
            Proc.Command(cmd.Program, cmd.Args).RunWithStdin(Encoding.UTF8.GetBytes(text));
            return true;
        }
        catch (ProcException)
        {
            return false;
        }
    }

    /// Print `text`, through a pager when stdout is a terminal and the text is
    /// taller than the terminal (unless `--no-pager`).
    private static void PageOrPrint(string text, bool noPager)
    {
        if (!noPager && Term.StdoutIsTty())
        {
            var rows = Term.TerminalRows() ?? 0;
            var lines = text.Count(c => c == '\n');
            if (rows > 0 && lines > rows && RunPager(text)) return;
        }
        Term.Out(text);
    }

    private static void FilePicker(UseCommand.Materialized m, IReadOnlyList<PreviewFile> files, bool noPager)
    {
        var others = files.Where(f => !IsRootSkillMd(f.Path)).ToList();
        if (others.Count == 0 || !Term.StdinIsTty()) return;
        var options = others.Select((f, i) => new SelectOption<int?>(i, Sanitize.Metadata(f.Path), FormatSize(f.Size))).ToList();
        options.Add(new SelectOption<int?>(null, "Done"));
        var initial = 0;
        while (true)
        {
            if (!Ui.Select("Open a file", options, initial, out var choice) || choice is not { } i) return;
            initial = i;
            var f = others[i];
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(NodePath.Join(m.SkillDir, f.Path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                bytes = [];
            }
            if (IsBinary(bytes))
            {
                Term.OutLine($"Binary file, not shown ({FormatSize(f.Size)})");
                continue;
            }
            var text = Encoding.UTF8.GetString(bytes);
            if (!text.EndsWith('\n')) text += "\n";
            PageOrPrint(text, noPager);
        }
    }

    /// Returns an error message, or null on success.
    private static string? Show(UseCommand.Materialized m, string source, PreviewOptions o)
    {
        var files = ListFiles(m.SkillDir);
        if (o.File is { } wantedArg)
        {
            var wanted = NormalizeFileArg(wantedArg);
            if (FindFile(files, wanted) is not { } f)
            {
                var lines = new List<string> { $"File not found in skill: {wanted}", "Available files:" };
                lines.AddRange(files.Select(x => $"  - {Sanitize.Metadata(x.Path)}"));
                return string.Join("\n", lines);
            }
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(NodePath.Join(m.SkillDir, f.Path));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return e.Message;
            }
            if (IsBinary(bytes)) return $"Cannot display binary file: {wanted}";
            if (!o.NoPager && Term.StdoutIsTty()) PageOrPrint(Encoding.UTF8.GetString(bytes), false);
            else Term.OutBytes(bytes);
            return null;
        }
        if (o.Json)
        {
            Term.OutLine(RenderJson(m, source, files));
            return null;
        }
        var tty = Term.StdoutIsTty();
        var text = RenderOverview(m.Name, m.Description, files, m.SkillMd, tty);
        if (tty)
        {
            PageOrPrint(text, o.NoPager);
            FilePicker(m, files, o.NoPager);
        }
        else
        {
            Term.Out(text);
        }
        return null;
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Fail(string message)
    {
        Term.ErrLine(message);
        Term.Exit(1);
    }

    public static void Run(IReadOnlyList<string> args)
    {
        var (sources, o, errors) = ParseOptions(args);
        if (o.Help)
        {
            PrintHelp();
            return;
        }
        if (errors.Count > 0) Fail(string.Join("\n", errors));
        if (sources.Count == 0) Fail($"Missing required argument: source\n\n{Help()}");
        if (sources.Count > 1) Fail($"Expected one source, received {sources.Count}: {string.Join(", ", sources)}");
        var source = sources[0];
        UseCommand.Materialized m;
        try
        {
            m = UseCommand.ResolveSkill(source, o.Skill, o.FullDepth, "preview");
        }
        catch (UseFailure e)
        {
            Fail(e.Message);
            return;
        }
        string? error;
        try
        {
            error = Show(m, source, o);
        }
        finally
        {
            Git.TryCleanup(m.TempRoot);
        }
        if (error != null) Fail(error);
    }
}
