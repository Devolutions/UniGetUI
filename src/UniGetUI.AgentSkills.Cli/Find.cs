// `skills find` — search skills.sh (port of find.ts).

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using static Skills.Ansi;

namespace Skills;

internal static partial class FindCommand
{
    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{0,38})$", RegexOptions.IgnoreCase)]
    private static partial Regex OwnerRe();

    /// `(n).toFixed(1).replace(/\.0$/, '')`
    private static string Fixed1(double n)
    {
        var s = n.ToString("F1", CultureInfo.InvariantCulture);
        return s.EndsWith(".0") ? s[..^2] : s;
    }

    public static string FormatInstalls(double count)
    {
        if (double.IsNaN(count) || count <= 0) return "";
        if (count >= 1_000_000) return $"{Fixed1(count / 1_000_000)}M installs";
        if (count >= 1_000) return $"{Fixed1(count / 1_000)}K installs";
        return $"{Json.NumberToString(count)} install{(count == 1 ? "" : "s")}";
    }

    public static (string Query, string? Owner, List<string> Errors) ParseOptions(IReadOnlyList<string> args)
    {
        var query = new List<string>();
        string? owner = null;
        var errors = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (a.Length == 0) continue;
            string value;
            if (a == "--owner")
            {
                if (i + 1 < args.Count && args[i + 1].Length > 0 && !args[i + 1].StartsWith('-'))
                {
                    value = args[++i];
                }
                else
                {
                    errors.Add("--owner requires a GitHub owner");
                    continue;
                }
            }
            else if (a.StartsWith("--owner="))
            {
                value = a["--owner=".Length..];
                if (value.Length == 0)
                {
                    errors.Add("--owner requires a GitHub owner");
                    continue;
                }
            }
            else
            {
                query.Add(a);
                continue;
            }
            var o = Sanitize.JsTrim(value).ToLowerInvariant();
            if (!OwnerRe().IsMatch(o)) errors.Add("--owner must be a valid GitHub owner");
            else owner = o;
        }
        return (string.Join(" ", query), owner, errors);
    }

    public static List<SearchSkill> SearchApi(string query, string? owner) => Skills.SearchApi.Search(query, owner);

    /// fzf-style interactive search. Returns the chosen skill or null.
    private static SearchSkill? RunSearchPrompt(string? owner)
    {
        if (!Ui.EnterRawMode()) return null;
        var results = new List<SearchSkill>();
        var selected = 0;
        var query = "";
        var loading = false;
        var lastLines = 0;
        (DateTime At, string Query)? debounce = null;
        long generation = 0;
        var inbox = new System.Collections.Concurrent.ConcurrentQueue<(long Gen, List<SearchSkill> Results)>();

        Term.Out("\x1b[?25l");
        void Render()
        {
            var sb = new StringBuilder();
            if (lastLines > 0) sb.Append($"\x1b[{lastLines}A\x1b[1G");
            sb.Append("\x1b[J");
            var lines = new List<string> { $"{Text}Search skills:{Reset} {query}{Bold}_{Reset}", "" };
            if (query.Length < 2) lines.Add($"{Dim}Start typing to search (min 2 chars){Reset}");
            else if (results.Count == 0 && loading) lines.Add($"{Dim}Searching…{Reset}");
            else if (results.Count == 0) lines.Add($"{Dim}No skills found{Reset}");
            else
            {
                for (var i = 0; i < Math.Min(8, results.Count); i++)
                {
                    var s = results[i];
                    var sel = i == selected;
                    var arrow = sel ? $"{Bold}>{Reset}" : " ";
                    var name = sel ? $"{Bold}{s.Name}{Reset}" : $"{Text}{s.Name}{Reset}";
                    var source = s.Source.Length == 0 ? "" : $" {Dim}{s.Source}{Reset}";
                    var installs = FormatInstalls(s.Installs);
                    var badge = installs.Length == 0 ? "" : $" {Cyan}{installs}{Reset}";
                    var loadingInd = loading && i == 0 ? $" {Dim}…{Reset}" : "";
                    lines.Add($"  {arrow} {name}{source}{badge}{loadingInd}");
                }
            }
            lines.Add("");
            lines.Add($"{Dim}up/down navigate | enter select | esc cancel{Reset}");
            foreach (var l in lines) sb.Append(l).Append("\r\n");
            Term.Out(sb.ToString());
            lastLines = lines.Count;
        }

        Render();
        SearchSkill? chosen;
        while (true)
        {
            while (inbox.TryDequeue(out var msg))
            {
                if (msg.Gen != Interlocked.Read(ref generation)) continue;
                results = msg.Results;
                selected = 0;
                loading = false;
                Render();
            }
            if (debounce is { } d && DateTime.UtcNow >= d.At)
            {
                var gen = generation;
                var q = d.Query;
                Task.Run(() => inbox.Enqueue((gen, SearchApi(q, owner))));
                debounce = null;
            }
            if (Ui.PollKey(TimeSpan.FromMilliseconds(30)) is not { } key) continue;
            var trigger = false;
            if (key.Key is Key.Escape or Key.CtrlC)
            {
                chosen = null;
                break;
            }
            if (key.Key == Key.Enter)
            {
                chosen = selected < results.Count ? results[selected] : null;
                break;
            }
            switch (key.Key)
            {
                case Key.Up:
                    selected = Math.Max(0, selected - 1);
                    Render();
                    break;
                case Key.Down:
                    selected = Math.Min(selected + 1, Math.Max(0, results.Count - 1));
                    Render();
                    break;
                case Key.Backspace when query.Length > 0:
                    query = query[..^1];
                    trigger = true;
                    break;
                case Key.Char when key.Ch is >= ' ' and <= '~':
                    query += key.Ch;
                    trigger = true;
                    break;
                case Key.Space:
                    query += ' ';
                    trigger = true;
                    break;
            }
            if (!trigger) continue;
            Interlocked.Increment(ref generation);
            debounce = null;
            loading = false;
            if (query.Length < 2)
            {
                results = [];
                selected = 0;
            }
            else
            {
                loading = true;
                var ms = Math.Max(150, 350 - query.Length * 50);
                debounce = (DateTime.UtcNow + TimeSpan.FromMilliseconds(ms), query);
            }
            Render();
        }
        Ui.LeaveRawMode();
        Term.Out("\x1b[?25h");
        return chosen;
    }

    private static (string, string)? OwnerRepoFromString(string pkg)
    {
        var at = pkg.LastIndexOf('@');
        return SourceParser.ParseOwnerRepo(at > 0 ? pkg[..at] : pkg);
    }

    public static void Run(IReadOnlyList<string> args)
    {
        var (query, owner, errors) = ParseOptions(args);
        var nonInteractive = !Term.StdinIsTty();
        if (errors.Count > 0)
        {
            foreach (var e in errors) Term.ErrLine(e);
            Term.ErrLine("Usage: skills find <query> [--owner <owner>]");
            return;
        }

        if (query.Length > 0)
        {
            var results = SearchApi(query, owner);
            Telemetry.Track(("event", "find"), ("query", query), ("resultCount", results.Count.ToString()));
            if (results.Count == 0)
            {
                var suffix = owner != null ? $" from owner \"{owner}\"" : "";
                Term.OutLine($"{Dim}No skills found for \"{query}\"{suffix}{Reset}");
                return;
            }
            Term.OutLine($"{Dim}Install with{Reset} skills add <owner/repo@skill>");
            Term.OutLine();
            foreach (var s in results)
            {
                var pkg = s.Source.Length == 0 ? s.Slug : s.Source;
                var installs = FormatInstalls(s.Installs);
                Term.OutLine($"{Text}{pkg}@{s.Name}{Reset}{(installs.Length == 0 ? "" : $" {Cyan}{installs}{Reset}")}");
                Term.OutLine($"{Dim}└ https://skills.sh/{s.Slug}{Reset}");
                Term.OutLine();
            }
            return;
        }

        if (nonInteractive || DetectAgent.IsRunningInAgent())
        {
            Term.OutLine($"{Dim}Tip: if running in a coding agent, follow these steps:{Reset}");
            Term.OutLine($"{Dim}  1) skills find [query] [--owner <owner>]{Reset}");
            Term.OutLine($"{Dim}  2) skills add <owner/repo@skill>{Reset}");
            Term.OutLine();
            Term.OutLine($"{Dim}Usage: skills find <query> [--owner <owner>]{Reset}");
            return;
        }

        var chosen = RunSearchPrompt(owner);
        Telemetry.Track(("event", "find"), ("query", ""), ("resultCount", chosen != null ? "1" : "0"), ("interactive", "1"));
        if (chosen == null)
        {
            Term.OutLine($"{Dim}Search cancelled{Reset}");
            Term.OutLine();
            return;
        }
        var package = chosen.Source.Length == 0 ? chosen.Slug : chosen.Source;
        Term.OutLine();
        Term.OutLine($"{Text}Installing {Bold}{chosen.Name}{Reset} from {Dim}{package}{Reset}…");
        Term.OutLine();
        var (sources, opts, _) = AddCommand.ParseOptions([package, "--skill", chosen.Name]);
        AddCommand.Run(sources, opts);
        Term.OutLine();
        var isPublic = OwnerRepoFromString(package) is var (o, r) && SourceParser.IsRepoPrivate(o, r) == false;
        Term.OutLine(isPublic
            ? $"{Dim}View the skill at{Reset} {Text}https://skills.sh/{chosen.Slug}{Reset}"
            : $"{Dim}Discover more skills at{Reset} {Text}https://skills.sh{Reset}");
        Term.OutLine();
    }
}
