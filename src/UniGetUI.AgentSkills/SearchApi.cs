// skills.sh search API (from find.ts).

using System.Text.Json.Nodes;

namespace Skills;

internal sealed record SearchSkill(string Name, string Slug, string Source, double Installs);

internal static class SearchApi
{
    public const int DefaultLimit = 20;

    public static string ApiBase() => Sys.Env("SKILLS_API_URL") ?? "https://skills.sh";

    /// Search skills.sh; results sorted by install count. Any failure yields an empty list.
    public static List<SearchSkill> Search(string query, string? owner, int limit = DefaultLimit)
    {
        var pairs = new List<(string, string)> { ("q", query), ("limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture)) };
        if (owner != null) pairs.Add(("owner", owner));
        var url = $"{ApiBase()}/api/search?{UrlUtil.SearchParams([.. pairs])}";
        var resp = HttpRequest.Get(url).Timeout(TimeSpan.FromSeconds(60)).TrySend();
        if (resp is not { Ok: true } || resp.Json() is not { } data || data["skills"] is not JsonArray list) return [];
        var output = list.Select(s => new SearchSkill(
            Sanitize.Metadata(Json.Str(s, "name") ?? ""),
            Sanitize.Metadata(Json.Str(s, "id") ?? ""),
            Sanitize.Metadata(Json.Str(s, "source") ?? ""),
            Json.AsNumber(Json.Get(s, "installs")) ?? 0)).ToList();
        return Collate.StableSort(output, (a, b) => b.Installs.CompareTo(a.Installs));
    }
}
