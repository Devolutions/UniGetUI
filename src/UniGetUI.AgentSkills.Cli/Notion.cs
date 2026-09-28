// Notion skills integration via the `ntn` CLI (port of notion.ts).

using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Skills;

internal sealed record NotionPack(string Id, string Name, string Description, string VersionId);

/// <param name="PackCount">Set for pack installs; null for a single Notion page.</param>
internal sealed record NotionPrepared(string RootDir, string TempDir, int? PackCount);

internal sealed class NotionException(string message) : Exception(message);

internal static partial class Notion
{
    private const string ApiVersion = "2026-03-11";
    private static readonly TimeSpan NtnTimeout = TimeSpan.FromSeconds(30);
    private const int NtnMaxBufferBytes = 10 * 1024 * 1024;
    private static readonly DownloadOptions DownloadLimits = new(50 * 1024 * 1024, 100 * 1024 * 1024, 5000);

    private sealed record DirectoryInfo(string Id, string VersionId, string Url);

    [GeneratedRegex(@"(^|\.)notion\.(so|com)$")]
    private static partial Regex NotionHost();

    [GeneratedRegex("(?:^|-)([0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$")]
    private static partial Regex PageId();

    private static string AssertString(JsonNode? v, string field) =>
        Json.AsString(v) is { Length: > 0 } s ? s : throw new NotionException($"Notion Agent Plugins response is missing {field}");

    private static string OptionalMetadata(JsonObject o, string key, string field)
    {
        var v = o[key];
        if (v == null) return "";
        return Json.AsString(v) is { } s ? Sanitize.Metadata(s) : throw new NotionException($"Notion Agent Plugins response has an invalid {field}");
    }

    private static NotionPack? ParsePack(JsonNode? v)
    {
        if (v is not JsonObject o) throw new NotionException("Notion Agent Plugins response contains an invalid pack");
        if (Json.AsString(o["name"]) is not { } rawName) throw new NotionException("Notion Agent Plugins response is missing pack.name");
        var name = Sanitize.Metadata(rawName);
        if (name.Length == 0) return null;
        return new NotionPack(AssertString(o["id"], "pack.id"), name, OptionalMetadata(o, "description", "pack.description"), AssertString(o["version_id"], "pack.version_id"));
    }

    private static DirectoryInfo ParseDirectory(JsonNode? v, string label)
    {
        if (v is not JsonObject o) throw new NotionException($"Notion {label} directory response is invalid");
        return new DirectoryInfo(
            AssertString(o["id"], $"{label} directory id"),
            AssertString(o["version_id"], $"{label} directory version_id"),
            AssertString(o["url"], $"{label} directory url"));
    }

    private static string RunNtn(params string[] args)
    {
        if (Sys.EnvRaw("SKILLS_DEBUG") == "1") Term.ErrLine($"[notion] ntn {string.Join(" ", args)}");
        ProcOutput output;
        try
        {
            output = Proc.Command("ntn", args).Timeout(NtnTimeout).MaxOutput(NtnMaxBufferBytes).Output();
        }
        catch (ProcException e)
        {
            throw new NotionException(e.Kind switch
            {
                ProcErrorKind.NotFound => "Notion CLI (ntn) is required. Install it from:\nhttps://developers.notion.com/cli/get-started/overview\nThen run `ntn login`.",
                ProcErrorKind.Timeout => $"ntn api timed out after {(int)NtnTimeout.TotalSeconds} seconds",
                ProcErrorKind.TooMuchOutput => "ntn api output exceeded 10 MiB",
                _ => $"Unable to start ntn: {Sanitize.StripTerminalEscapes(e.Message)}",
            });
        }
        if (output.Success) return output.StdoutText;
        var stderr = Sanitize.JsTrim(Sanitize.StripTerminalEscapes(output.StderrText));
        throw new NotionException(stderr.Length == 0
            ? $"ntn api failed with exit code {(output.Status?.ToString() ?? "unknown")}"
            : $"ntn api failed: {stderr}");
    }

    private static JsonNode? FetchJson(string label, params string[] args)
    {
        var output = RunNtn(["api", .. args, "--notion-version", ApiVersion]);
        return Json.TryParse(output, out var v) ? v : throw new NotionException($"ntn returned invalid JSON for {label}");
    }

    public static List<NotionPack> FetchPacks()
    {
        var packs = new List<NotionPack>();
        var seen = new HashSet<string>();
        string? cursor = null;
        while (true)
        {
            var args = new List<string> { "/v1/ai/plugins", "page_size==100" };
            if (cursor != null) args.Add($"start_cursor=={cursor}");
            var value = FetchJson("the Notion packs list", [.. args]);
            if (value is not JsonObject obj || obj["results"] is not JsonArray results) throw new NotionException("Notion Agent Plugins list response is invalid");
            if (Json.AsBool(obj["has_more"]) is not { } hasMore) throw new NotionException("Notion Agent Plugins list response is missing has_more");
            if (!obj.TryGetPropertyValue("next_cursor", out var nextNode) || (nextNode != null && Json.AsString(nextNode) == null))
                throw new NotionException("Notion Agent Plugins list response has an invalid next_cursor");
            var next = Json.AsString(nextNode);
            foreach (var r in results)
                if (ParsePack(r) is { } p) packs.Add(p);
            if (!hasMore) break;
            if (string.IsNullOrEmpty(next) || !seen.Add(next)) throw new NotionException("Notion Agent Plugins pagination returned an invalid cursor");
            cursor = next;
        }
        return packs;
    }

    private static DirectoryInfo FetchPackDirectory(NotionPack pack)
    {
        var label = $"Notion pack {Json.Quote(pack.Name)}";
        var d = ParseDirectory(FetchJson(label, $"/v1/ai/plugins/{UrlUtil.EncodeUriComponent(pack.Id)}"), "pack");
        if (d.Id != pack.Id || d.VersionId != pack.VersionId)
            throw new NotionException($"Notion pack {Json.Quote(pack.Name)} changed while preparing installation");
        return d;
    }

    /// Workspace name; null on any failure so the probe never blocks installs.
    public static string? FetchWorkspaceName()
    {
        try
        {
            var output = RunNtn("api", "/v1/users/me", "--notion-version", ApiVersion);
            if (!Json.TryParse(output, out var v) || v?["bot"] is not JsonObject bot) return null;
            return Json.AsString(bot["workspace_name"]) is { Length: > 0 } name ? Sanitize.Metadata(name) : null;
        }
        catch (NotionException)
        {
            return null;
        }
    }

    private static string InWorkspace(string? ws) => ws != null ? $" in workspace {Pc.Cyan(ws)}" : "";

    public static bool IsNotionSource(string source) => source.ToLowerInvariant() == "notion";

    /// Page ID from a Notion page URL.
    public static string? ParseSkillUrl(string source)
    {
        var url = WebUrl.Parse(source);
        if (url is not { Scheme: "https" or "http" } || !NotionHost().IsMatch(url.Hostname.ToLowerInvariant())) return null;
        var last = url.Pathname.Split('/').LastOrDefault(s => s.Length > 0);
        if (last == null) return null;
        var decoded = UrlUtil.DecodeUriComponent(last) ?? last;
        var m = PageId().Match(decoded.ToLowerInvariant());
        if (!m.Success) return null;
        var raw = m.Groups[1].Value.Replace("-", "");
        return $"{raw[..8]}-{raw[8..12]}-{raw[12..16]}-{raw[16..20]}-{raw[20..]}";
    }

    private static DownloadedSource DownloadDirectory(string url)
    {
        try
        {
            return DownloadSource.Fetch(url, DownloadLimits);
        }
        catch (Exception e) when (e is DownloadException or ArchiveValidationException)
        {
            throw new NotionException(e.Message);
        }
    }

    public static NotionPrepared PrepareSkillSource(string pageId)
    {
        var spinner = new Ui.Spinner();
        spinner.Start("Fetching Notion skill with ntn…");
        try
        {
            var ws = Task.Run(FetchWorkspaceName);
            DirectoryInfo dir;
            try
            {
                dir = ParseDirectory(FetchJson($"Notion skill {pageId}", $"/v1/ai/skills/{UrlUtil.EncodeUriComponent(pageId)}"), "skill");
            }
            finally
            {
                ws.Wait();
            }
            var d = DownloadDirectory(dir.Url);
            spinner.Stop($"Downloaded skill from Notion{InWorkspace(ws.Result)}");
            return new NotionPrepared(d.RootDir, d.TempDir, null);
        }
        catch (NotionException)
        {
            spinner.Stop(Pc.Red("Failed to prepare Notion skill"));
            throw;
        }
    }

    internal static string NormalizeSelector(string v)
    {
        var sb = new StringBuilder();
        var inRun = false;
        foreach (var c in v.ToLowerInvariant())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                sb.Append(c);
                inRun = false;
            }
            else if (!inRun)
            {
                sb.Append('-');
                inRun = true;
            }
        }
        var s = sb.ToString();
        if (s.StartsWith('-')) s = s[1..];
        if (s.EndsWith('-')) s = s[..^1];
        return s;
    }

    private static List<NotionPack> SortedPacks(IEnumerable<NotionPack> packs) => Collate.StableSort(packs, (a, b) => Collate.LocaleCompare(a.Name, b.Name));

    private static void CopyDirAll(string src, string dest)
    {
        if (Fs.Exists(dest)) throw new IOException($"Target already exists: {dest}");
        Directory.CreateDirectory(dest);
        foreach (var e in Fs.ReadDir(src))
        {
            var d = NodePath.Join(dest, e.Name);
            if (e.IsDirectory) CopyDirAll(e.FullPath, d);
            else File.Copy(e.FullPath, d);
        }
    }

    /// Returns null when the user only listed packs or cancelled.
    public static NotionPrepared? PreparePackSource(bool yes, bool list, IReadOnlyList<string>? skill)
    {
        var spinner = new Ui.Spinner();
        spinner.Start("Fetching Notion packs with ntn…");
        var wsTask = Task.Run(FetchWorkspaceName);
        List<NotionPack> packs;
        try
        {
            packs = FetchPacks();
        }
        catch (NotionException)
        {
            wsTask.Wait();
            spinner.Stop(Pc.Red("Failed to load Notion packs"));
            throw;
        }
        var ws = wsTask.Result;
        spinner.Stop($"Found {Pc.Green(packs.Count.ToString())} Notion pack{(packs.Count == 1 ? "" : "s")}{InWorkspace(ws)}");
        if (packs.Count == 0) throw new NotionException("Notion returned no packs for the authenticated workspace");

        if (list)
        {
            Term.OutLine();
            Ui.Log.Step(Pc.Bold("Available Notion packs"));
            foreach (var p in SortedPacks(packs))
            {
                Ui.Log.Message(Pc.Cyan(p.Name));
                if (p.Description.Length > 0) Ui.Log.Message($"  {Pc.Dim(p.Description)}");
            }
            Ui.Outro(Pc.Dim("Select a pack by running the command without --list."));
            return null;
        }

        List<NotionPack> selected;
        if (skill is { Count: > 0 } sel)
        {
            List<NotionPack> chosen;
            if (sel.Contains("*"))
            {
                chosen = packs;
            }
            else
            {
                var normalized = sel.Select(NormalizeSelector).ToList();
                chosen = packs.Where(p => normalized.Contains(NormalizeSelector(p.Name)) || normalized.Contains(p.Id.ToLowerInvariant())).ToList();
            }
            if (chosen.Count == 0) throw new NotionException($"No matching Notion packs found for: {string.Join(", ", sel)}");
            selected = chosen;
        }
        else if (yes || !Term.StdinIsTty())
        {
            selected = packs;
        }
        else
        {
            var sorted = SortedPacks(packs);
            var items = sorted.Select((p, i) => new SearchItem<int>(i, "[object Object]", p.Name)
            {
                Detail = p.Description.Length == 0 ? "Notion plugin pack" : p.Description,
            }).ToList();
            var idx = SearchMultiselect.Run(new SearchMultiselectOptions<int>
            {
                Message = $"Select Notion packs to install {Pc.Dim("(space to toggle)")}",
                Items = items,
                Required = true,
                MaxVisible = 20,
                Searchable = false,
                ShowDetail = true,
                ShowSelectedSummary = false,
            });
            if (idx == null)
            {
                Ui.Cancel("Selection cancelled");
                return null;
            }
            selected = idx.Select(i => sorted[i]).ToList();
        }

        Ui.Note(string.Join("\n", selected.Select(p => Pc.Cyan(p.Name))), $"Selected {selected.Count} Notion pack{(selected.Count == 1 ? "" : "s")}");

        var staging = Sys.MkdTemp("skills-notion-");
        var packsDir = NodePath.Join(staging, "packs");
        spinner.Start("Preparing selected Notion packs…");
        try
        {
            Directory.CreateDirectory(packsDir);
            var plugins = new JsonArray();
            var skillCount = 0;
            for (var index = 0; index < selected.Count; index++)
            {
                var pack = selected[index];
                DownloadedSource? downloaded = null;
                try
                {
                    var dir = FetchPackDirectory(pack);
                    downloaded = DownloadDirectory(dir.Url);
                    var root = downloaded.RootDir;
                    var skills = SkillDiscovery.Discover(root, null, new DiscoverOptions(IncludeInternal: true, FullDepth: true));
                    if (skills.Count == 0) throw new NotionException("pack contains no valid skills");
                    var name = new string(Installer.SanitizeName(pack.Name).Take(100).ToArray());
                    var dirName = $"pack-{index + 1}-{name}";
                    CopyDirAll(root, NodePath.Join(packsDir, dirName));
                    var skillPaths = new JsonArray();
                    foreach (var s in skills)
                    {
                        var rel = string.Join("/", NodePath.Relative(root, s.Path).Split(NodePath.Sep));
                        skillPaths.Add((JsonNode)(rel.Length == 0 ? "./." : $"./{rel}"));
                    }
                    plugins.Add((JsonNode)new JsonObject { ["name"] = pack.Name, ["source"] = $"./{dirName}", ["skills"] = skillPaths });
                    skillCount += skills.Count;
                }
                catch (Exception e) when (e is NotionException or DiscoverException or IOException or UnauthorizedAccessException)
                {
                    throw new NotionException($"Failed to prepare Notion pack {Json.Quote(pack.Name)}: {e.Message}");
                }
                finally
                {
                    if (downloaded != null) Git.TryCleanup(downloaded.TempDir);
                }
            }
            var manifestDir = NodePath.Join(staging, ".claude-plugin");
            Directory.CreateDirectory(manifestDir);
            var manifest = new JsonObject { ["metadata"] = new JsonObject { ["pluginRoot"] = "./packs" }, ["plugins"] = plugins };
            File.WriteAllText(NodePath.Join(manifestDir, "marketplace.json"), Json.Stringify(manifest) + "\n");
            spinner.Stop($"Prepared {Pc.Green(selected.Count.ToString())} Notion pack{(selected.Count == 1 ? "" : "s")} with {Pc.Green(skillCount.ToString())} skill{(skillCount == 1 ? "" : "s")}");
            return new NotionPrepared(staging, staging, selected.Count);
        }
        catch (Exception e) when (e is NotionException or IOException or UnauthorizedAccessException)
        {
            spinner.Stop(Pc.Red("Failed to prepare Notion packs"));
            Git.TryCleanup(staging);
            if (e is NotionException) throw;
            throw new NotionException(e.Message);
        }
    }
}
