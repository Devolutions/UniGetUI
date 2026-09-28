// Notion skills databases and skill pages as sources. A skills database lists
// its skills as pages; each page downloads as a skill folder (Notion's Agent
// Skills API), with a version id that changes when the skill does. Installs are
// tracked in the global lock, which the reference CLI does not do for Notion,
// so that updates can be found (see docs/EXTENSIONS.md).

using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Devolutions.AgentSkills;

namespace Skills;

/// A skill page of a Notion source: its title, and the name Notion gives the
/// skill when it downloads it.
internal sealed record NotionPage(string Id, string Title, string Description, string? DatabaseId)
{
    public string Name => NotionFeed.Slug(Title);

    public bool Matches(string requested) =>
        requested.Equals(Name, StringComparison.OrdinalIgnoreCase) || requested.Equals(Title, StringComparison.OrdinalIgnoreCase);
}

/// Where an installed Notion skill came from, for its lock entry: its page,
/// the version downloaded, and the database (or, for a page outside one, the
/// page) it is listed in.
internal sealed record NotionOrigin(NotionPage Page, string VersionId, string Source)
{
    public string PageUrl => NotionFeed.CanonicalUrl(Page.Id);
}

internal static partial class NotionFeed
{
    private static readonly DownloadOptions DownloadLimits = new(50 * 1024 * 1024, 100 * 1024 * 1024, 5000);

    [GeneratedRegex("(?:^|-)([0-9a-f]{32}|[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})$")]
    private static partial Regex TrailingId();

    /// The page or database id of a Notion address (app.notion.com/p/…,
    /// notion.so/…-id, a notion.site page), dashed; null for anything else.
    public static string? ParseId(string source)
    {
        var url = WebUrl.Parse(source.Trim());
        if (url is not { Scheme: "https" } || url.Username.Length > 0 || url.Password.Length > 0 || !NotionCli.IsNotionHost(url.Hostname))
            return null;
        var last = url.Pathname.Split('/').LastOrDefault(s => s.Length > 0);
        if (last == null) return null;
        var m = TrailingId().Match((UrlUtil.DecodeUriComponent(last) ?? last).ToLowerInvariant());
        return m.Success ? Dashed(m.Groups[1].Value.Replace("-", "")) : null;
    }

    public static bool IsNotionUrl(string source) =>
        WebUrl.Parse(source.Trim()) is { Scheme: "https" or "http" } u && NotionCli.IsNotionHost(u.Hostname);

    private static string Dashed(string raw) => $"{raw[..8]}-{raw[8..12]}-{raw[12..16]}-{raw[16..20]}-{raw[20..]}";

    /// The address a Notion source is recorded under.
    public static string CanonicalUrl(string id) => $"https://app.notion.com/p/{id.Replace("-", "")}";

    /// The skill name Notion derives from a page title when it downloads the
    /// skill: lower case, apostrophes dropped, and every other run of
    /// characters outside a-z and 0-9 turned into one hyphen ("What's New
    /// release notes" → whats-new-release-notes, "Cliché Finder" →
    /// clich-finder).
    public static string Slug(string title)
    {
        var sb = new StringBuilder();
        var separated = false;
        foreach (var c in title.ToLowerInvariant())
        {
            if (c is '\'' or '‘' or '’') continue;
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                sb.Append(c);
                separated = false;
            }
            else if (!separated && sb.Length > 0)
            {
                sb.Append('-');
                separated = true;
            }
        }
        return sb.ToString().TrimEnd('-');
    }

    // ─── API ───

    private static string PlainText(JsonNode? richText) =>
        richText is JsonArray parts ? string.Concat(parts.Select(p => Json.Str(p, "plain_text") ?? "")) : "";

    private static NotionPage? ParsePage(JsonNode? page)
    {
        if (page is not JsonObject o || Json.Str(o, "id") is not { Length: > 0 } id) return null;
        if (Json.AsBool(o["in_trash"]) == true || Json.AsBool(o["archived"]) == true) return null;
        string title = "", description = "";
        if (o["properties"] is JsonObject properties)
        {
            foreach (var (key, property) in properties)
            {
                var type = Json.Str(property, "type");
                if (type == "title") title = PlainText(Json.Get(property, "title"));
                else if (type == "rich_text" && key.Equals("Description", StringComparison.OrdinalIgnoreCase))
                    description = PlainText(Json.Get(property, "rich_text"));
            }
        }
        title = Sanitize.Metadata(title);
        if (title.Length == 0 || Slug(title).Length == 0) return null;
        return new NotionPage(id, title, Sanitize.Metadata(description), Json.NonEmpty(Json.Get(o, "parent"), "database_id"));
    }

    /// The data sources of a database; null when the id is not a database.
    public static List<string>? TryGetDataSources(string id)
    {
        JsonNode database;
        try
        {
            database = NotionCli.Api("the Notion database", $"v1/databases/{id}");
        }
        catch (NotionCliException e) when (e.Failure is NotionCliFailure.NotFound or NotionCliFailure.Invalid)
        {
            return null;
        }
        return Json.Get(database, "data_sources") is JsonArray sources
            ? sources.Select(s => Json.NonEmpty(s, "id")).OfType<string>().ToList()
            : [id];
    }

    /// Every skill page of the database's data sources.
    public static List<NotionPage> ListPages(IEnumerable<string> dataSources)
    {
        var pages = new List<NotionPage>();
        foreach (var dataSource in dataSources)
        {
            string? cursor = null;
            var seen = new HashSet<string>();
            while (true)
            {
                Sys.ThrowIfCancelled();
                var body = new JsonObject { ["page_size"] = 100 };
                if (cursor != null) body["start_cursor"] = cursor;
                var result = NotionCli.Api("the Notion database", $"v1/data_sources/{dataSource}/query", "POST", body);
                if (Json.Get(result, "results") is JsonArray results)
                    pages.AddRange(results.Select(ParsePage).OfType<NotionPage>());
                if (Json.AsBool(Json.Get(result, "has_more")) != true) break;
                cursor = Json.NonEmpty(result, "next_cursor");
                if (cursor == null || !seen.Add(cursor))
                    throw new NotionCliException(NotionCliFailure.Other, "The Notion database returned an invalid page cursor");
            }
        }
        return pages;
    }

    public static NotionPage GetPage(string id) =>
        ParsePage(NotionCli.Api("the Notion page", $"v1/pages/{id}"))
        ?? throw new NotionCliException(NotionCliFailure.NotFound, "The Notion page has no title, or is in the trash");

    /// The current version of a skill page, and a short-lived address of its folder.
    public static (string VersionId, string Url) GetSkillDirectory(string pageId)
    {
        var directory = NotionCli.Api("the Notion skill", $"v1/ai/skills/{pageId}");
        if (Json.NonEmpty(directory, "version_id") is not { } version || Json.NonEmpty(directory, "url") is not { } url)
            throw new NotionCliException(NotionCliFailure.Other, "Notion returned a skill without a version or download address");
        return (version, url);
    }

    public static DownloadedSource Download(string url)
    {
        if (Sys.Context?.NotionDownloader is { } fake) return fake(url);
        if (!url.StartsWith("https://", StringComparison.Ordinal))
            throw new SkillsException("Notion returned a skill download address that is not encrypted");
        try
        {
            return DownloadSource.Fetch(url, DownloadLimits);
        }
        catch (Exception e) when (e is DownloadException or ArchiveValidationException)
        {
            throw new SkillsException($"Could not download the skill from Notion: {e.Message}", e);
        }
    }

    // ─── Sources ───

    /// The skills a Notion database (or a single skill page) offers, without
    /// downloading them.
    public static List<AvailableSkill> ListAvailable(string id, Action<string> log)
    {
        try
        {
            log("Reading the Notion source…");
            var pages = TryGetDataSources(id) is { } dataSources ? ListPages(dataSources) : [GetPage(id)];
            log($"Found {pages.Count} skill{(pages.Count == 1 ? "" : "s")}");
            return pages.Select(p => new AvailableSkill(p.Name, p.Description, null)).ToList();
        }
        catch (NotionCliException e)
        {
            throw e.ToSkillsException();
        }
    }

    /// Download the requested skills of a Notion source (all of them when
    /// `requested` is empty or `*`) into one temp directory.
    public static ResolvedSource Resolve(string id, IReadOnlyList<string> requested, Action<string> log)
    {
        List<NotionPage> pages;
        string source;
        try
        {
            log("Reading the Notion source…");
            if (TryGetDataSources(id) is { } dataSources)
            {
                source = CanonicalUrl(id);
                var all = ListPages(dataSources);
                var everything = requested.Count == 0 || requested.Contains("*");
                pages = everything ? all : all.Where(p => requested.Any(p.Matches)).ToList();
                if (pages.Count == 0)
                {
                    throw new SkillsException(all.Count == 0
                        ? "The Notion database has no skills."
                        : $"No matching skills found for: {string.Join(", ", requested)}. Available skills: {string.Join(", ", all.Select(p => p.Name))}");
                }
            }
            else
            {
                var page = GetPage(id);
                source = CanonicalUrl(page.DatabaseId ?? page.Id);
                pages = [page];
            }
        }
        catch (NotionCliException e)
        {
            throw e.ToSkillsException();
        }

        var staging = Sys.MkdTemp("skills-notion-");
        try
        {
            var origins = new Dictionary<string, NotionOrigin>(StringComparer.Ordinal);
            foreach (var page in pages)
            {
                Sys.ThrowIfCancelled();
                log($"Downloading {page.Name} from Notion…");
                string version, url;
                try
                {
                    (version, url) = GetSkillDirectory(page.Id);
                }
                catch (NotionCliException e)
                {
                    throw e.ToSkillsException();
                }
                var downloaded = Download(url);
                try
                {
                    var folder = NodePath.Join(staging, page.Id.Replace("-", ""));
                    CopyDirectory(downloaded.RootDir, NodePath.Join(folder, NodePath.Basename(downloaded.RootDir)));
                    origins[folder] = new NotionOrigin(page, version, source);
                }
                finally
                {
                    Git.TryCleanup(downloaded.TempDir);
                }
            }

            List<Skill> skills;
            try
            {
                skills = SkillDiscovery.Discover(staging, null, new DiscoverOptions(IncludeInternal: true, FullDepth: true));
            }
            catch (DiscoverException e)
            {
                throw new SkillsException(e.Message, e);
            }
            var bySkill = new Dictionary<string, NotionOrigin>(StringComparer.Ordinal);
            foreach (var s in skills)
            {
                var origin = origins.FirstOrDefault(o => s.Path == o.Key || s.Path.StartsWith(o.Key + NodePath.Sep, StringComparison.Ordinal));
                if (origin.Value != null) bySkill[s.Path] = origin.Value;
            }
            if (skills.Count == 0) throw new SkillsException("The skills Notion returned have no valid SKILL.md.");
            return new ResolvedSource
            {
                Parsed = new ParsedSource("notion", source),
                DirectDownload = true,
                TempDir = staging,
                Skills = skills,
                Notion = bySkill,
            };
        }
        catch
        {
            Git.TryCleanup(staging);
            throw;
        }
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var e in Fs.ReadDir(src))
        {
            var d = NodePath.Join(dest, e.Name);
            if (e.IsDirectory) CopyDirectory(e.FullPath, d);
            else if (e.IsFile) File.Copy(e.FullPath, d);
        }
    }
}
