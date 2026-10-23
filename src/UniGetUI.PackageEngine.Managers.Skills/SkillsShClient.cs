using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;

namespace UniGetUI.PackageEngine.Managers.SkillsManager;

internal sealed record SkillsShResult(string Name, SkillSourceLocator Source);

internal interface ISkillsShClient
{
    IReadOnlyList<SkillsShResult> Search(string query);
}

internal sealed partial class SkillsShClient(HttpMessageHandler? handler = null) : ISkillsShClient
{
    internal const int ResultLimit = 50;
    internal const long MaxResponseBytes = 1024 * 1024;
    internal static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    [GeneratedRegex(@"\A[\p{L}\p{N}][\p{L}\p{N} ._:-]{0,127}\z")]
    private static partial Regex SkillName();

    [GeneratedRegex(@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z")]
    private static partial Regex SkillSlug();

    [GeneratedRegex(@"\A(?=.{1,253}\z)(?:[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?\.)+[A-Za-z]{2,63}\z")]
    private static partial Regex WebsiteHost();

    public IReadOnlyList<SkillsShResult> Search(string query) =>
        SearchAsync(query).GetAwaiter().GetResult();

    private async Task<IReadOnlyList<SkillsShResult>> SearchAsync(string query)
    {
        query = query.Trim();
        if (query.Length < 2)
            return [];

        using var client = new HttpClient(handler ?? CoreTools.GenericHttpClientParameters, disposeHandler: handler is null)
        {
            Timeout = RequestTimeout,
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
        var uri = new Uri($"https://skills.sh/api/search?q={Uri.EscapeDataString(query)}&limit={ResultLimit}");
        using var response = await client.GetAsync(uri).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var payload = JsonSerializer.Deserialize(json, SkillsShJsonContext.Default.SkillsShResponse);
        if (payload?.Skills is null)
            throw new JsonException("The skills.sh response did not contain a skills list.");

        List<SkillsShResult> results = [];
        HashSet<(string Source, string Name)> seen = [];
        foreach (var skill in payload.Skills.Take(ResultLimit))
        {
            var source = ParseSource(skill?.Source);
            // The website slug is not the install selector: e.g. "reactcomponents" vs "react:components".
            string? slug = skill?.SkillId ?? skill?.Id?.Split('/').LastOrDefault();
            if (skill?.Name is not { } name || !SkillName().IsMatch(name)
                || source is null || slug is null || !SkillSlug().IsMatch(slug)
                || !string.Equals(skill.Id, $"{skill.Source}/{slug}", StringComparison.OrdinalIgnoreCase))
            {
                Logger.Warn("Ignoring an invalid skills.sh search result");
                continue;
            }

            if (seen.Add((source.Name, name)))
                results.Add(new(name, source));
        }
        return results;
    }

    private static SkillSourceLocator? ParseSource(string? value)
    {
        if (value is null || value.Length > 253)
            return null;

        if (value.Split('/') is [_, _])
        {
            var repository = SkillSourceLocator.Parse(value);
            return repository is { IsGitHub: true }
                && value.Equals(repository.Name, StringComparison.OrdinalIgnoreCase) ? repository : null;
        }

        if (!WebsiteHost().IsMatch(value)
            || new[] { ".localhost", ".local", ".localdomain", ".internal", ".test", ".invalid", ".example" }
                .Any(suffix => value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            return null;

        return SkillSourceLocator.Parse($"https://{value}") is { Kind: SkillSourceKind.Index } index
            ? index : null;
    }
}

internal sealed record SkillsShResponse
{
    [JsonPropertyName("skills")]
    public SkillsShEntry?[]? Skills { get; init; }
}

internal sealed record SkillsShEntry
{
    [JsonPropertyName("id")]
    public string? Id { get; init; }
    [JsonPropertyName("name")]
    public string? Name { get; init; }
    [JsonPropertyName("source")]
    public string? Source { get; init; }
    [JsonPropertyName("skillId")]
    public string? SkillId { get; init; }
}

[JsonSerializable(typeof(SkillsShResponse))]
internal sealed partial class SkillsShJsonContext : JsonSerializerContext;
