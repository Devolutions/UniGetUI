// Well-known skills provider (RFC 8615), port of providers/wellknown.ts.
//
// Supports the v0.2.0 $schema + type/url/digest artifact index and the legacy
// v0.1.0 name/description/files directory index, at /.well-known/agent-skills/
// (preferred) or /.well-known/skills/.

using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Skills;

internal abstract record NormalizedEntry(string Name, string Description, JsonNode IndexEntry);

internal sealed record V1Entry(string Name, string Description, List<string> Files, string BaseUrl, string WellKnownPath, JsonNode IndexEntry)
    : NormalizedEntry(Name, Description, IndexEntry);

internal sealed record V2Entry(string Name, string Description, string Kind, string ArtifactUrl, string Digest, JsonNode IndexEntry)
    : NormalizedEntry(Name, Description, IndexEntry);

internal sealed record IndexResult(List<NormalizedEntry> Entries, string ResolvedBaseUrl, string ResolvedWellKnownPath, string IndexUrl);

internal sealed class WellKnownSkill
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required string Content { get; init; }
    public required string InstallName { get; init; }
    public required string SourceUrl { get; init; }
    public JsonObject? Metadata { get; init; }

    /// Files keyed by relative path, in insertion order.
    public required List<SnapshotFile> Files { get; init; }
    public required JsonNode IndexEntry { get; init; }
}

internal sealed class ScopeNotFoundException(string scopePath, string rootUrl)
    : Exception($"No skills found for the scoped path '{scopePath}' on {rootUrl}. Not falling back to the root skills index because that would install every skill the host publishes. Check the URL, or run 'skills add {rootUrl}' to install from the root index.");

internal static class WellKnown
{
    private const string DiscoverySchemaV2 = "https://schemas.agentskills.io/discovery/0.2.0/schema.json";
    private const long MaxArchiveUnpackedBytes = 50 * 1024 * 1024;
    private const int MaxArchiveFiles = 1000;
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(10);
    private static readonly string[] WellKnownPaths = [".well-known/agent-skills", ".well-known/skills"];
    private const string IndexFile = "index.json";

    internal static bool IsValidSkillName(JsonNode? v) =>
        Json.AsString(v) is { } name && name.Length is >= 1 and <= 64
        && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-')
        && !name.StartsWith('-') && !name.EndsWith('-') && !name.Contains("--");

    private static bool IsSafeLegacyFilePath(JsonNode? v) =>
        Json.AsString(v) is { Length: > 0 } p && !p.StartsWith('/') && !p.StartsWith('\\') && !p.Contains("..") && !p.Contains('\0');

    private static bool IsValidV1(JsonNode? e)
    {
        if (e is not JsonObject || !IsValidSkillName(e["name"])) return false;
        if (Json.NonEmpty(e, "description") == null) return false;
        if (e["files"] is not JsonArray { Count: > 0 } files || !files.All(IsSafeLegacyFilePath)) return false;
        return files.Any(f => Json.AsString(f)?.ToLowerInvariant() == "skill.md");
    }

    private static bool IsValidV2(JsonNode? e)
    {
        if (e is not JsonObject || !IsValidSkillName(e["name"])) return false;
        if (Json.NonEmpty(e, "description") is not { Length: <= 1024 }) return false;
        if (Json.Str(e, "type") is not ("skill-md" or "archive")) return false;
        if (Json.NonEmpty(e, "url") is not { } url || WebUrl.Parse(url, "https://example.com/.well-known/agent-skills/index.json") == null) return false;
        return Json.Str(e, "digest") is { Length: 71 } d && d.StartsWith("sha256:") && d[7..].All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));
    }

    private static string LegacySkillBaseUrl(string indexUrl, string wellKnownPath)
    {
        var u = WebUrl.Parse(indexUrl);
        if (u == null) return "";
        var marker = $"/{wellKnownPath}/{IndexFile}";
        var path = u.Pathname;
        var b = path.Length >= marker.Length ? path[..^marker.Length] : "";
        return $"{u.Protocol}//{u.Host}{b}";
    }

    internal static List<NormalizedEntry>? NormalizeIndex(JsonNode? raw, string indexUrl, string wellKnownPath)
    {
        if (raw is not JsonObject record || record["skills"] is not JsonArray skills) return null;
        if (!record.ContainsKey("$schema"))
        {
            var entries = new List<NormalizedEntry>();
            foreach (var e in skills)
            {
                if (!IsValidV1(e)) return null;
                entries.Add(new V1Entry(Json.Str(e, "name")!, Json.Str(e, "description")!, ((JsonArray)e!["files"]!).Select(f => Json.AsString(f)!).ToList(),
                    LegacySkillBaseUrl(indexUrl, wellKnownPath), wellKnownPath, e.DeepClone()));
            }
            return entries;
        }
        if (Json.Str(record, "$schema") != DiscoverySchemaV2) return null;
        var v2 = new List<NormalizedEntry>();
        foreach (var e in skills)
        {
            if (!IsValidV2(e)) continue;
            var artifact = WebUrl.Parse(Json.Str(e, "url")!, indexUrl);
            if (artifact == null) return null;
            v2.Add(new V2Entry(Json.Str(e, "name")!, Json.Str(e, "description")!, Json.Str(e, "type")!, artifact.Href, Json.Str(e, "digest")!, e!.DeepClone()));
        }
        return v2.Count == 0 ? null : v2;
    }

    private static List<IndexResult> FetchIndexCandidates(string baseUrl, bool updateCheck)
    {
        var parsed = WebUrl.Parse(baseUrl);
        if (parsed == null) return [];
        var basePath = parsed.Pathname.EndsWith('/') ? parsed.Pathname[..^1] : parsed.Pathname;
        var origin = $"{parsed.Protocol}//{parsed.Host}";
        var deadline = DateTime.UtcNow + DiscoveryTimeout;
        var urls = new List<(string IndexUrl, string Base, string Wk)>();
        foreach (var wk in WellKnownPaths)
        {
            urls.Add(($"{origin}{basePath}/{wk}/{IndexFile}", $"{origin}{basePath}", wk));
            if (basePath.Length > 0) urls.Add(($"{origin}/{wk}/{IndexFile}", origin, wk));
        }
        var output = new List<IndexResult>();
        foreach (var (indexUrl, resolvedBase, wk) in urls)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) continue;
            var req = HttpRequest.Get(indexUrl).Timeout(remaining);
            if (updateCheck) req.Header("X-Skills-Update-Check", "1");
            var resp = req.TrySend();
            if (resp is not { Ok: true } || !resp.TryJson(out var raw)) continue;
            if (NormalizeIndex(raw, indexUrl, wk) is not { } entries) continue;
            output.Add(new IndexResult(entries, resolvedBase, wk, indexUrl));
        }
        return output;
    }

    /// First index found for `baseUrl`.
    public static IndexResult? FetchIndex(string baseUrl, bool updateCheck) => FetchIndexCandidates(baseUrl, updateCheck).FirstOrDefault();

    private static WellKnownSkill Create(string name, string description, string content, string installName, string sourceUrl, JsonNode? metadata, List<SnapshotFile> files, JsonNode indexEntry) =>
        new()
        {
            Name = Sanitize.Metadata(name),
            Description = Sanitize.Metadata(description),
            Content = content,
            InstallName = installName,
            SourceUrl = sourceUrl,
            Metadata = metadata as JsonObject,
            Files = files,
            IndexEntry = indexEntry,
        };

    private static void SetFile(List<SnapshotFile> files, string path, byte[] contents)
    {
        var i = files.FindIndex(f => f.Path == path);
        if (i >= 0) files[i] = new SnapshotFile(path, contents);
        else files.Add(new SnapshotFile(path, contents));
    }

    private static (string Name, string Description, JsonNode? Metadata)? SkillFields(string content)
    {
        try
        {
            var data = Frontmatter.Parse(content).Data;
            return Json.Str(data, "name") is { } n && Json.Str(data, "description") is { } d ? (n, d, data["metadata"]) : null;
        }
        catch (YamlParseException)
        {
            return null;
        }
    }

    private static WellKnownSkill? FetchLegacy(V1Entry e)
    {
        var skillBase = $"{(e.BaseUrl.EndsWith('/') ? e.BaseUrl[..^1] : e.BaseUrl)}/{e.WellKnownPath}/{e.Name}";
        var skillMdUrl = $"{skillBase}/SKILL.md";
        var resp = HttpRequest.Get(skillMdUrl).TrySend();
        if (resp is not { Ok: true }) return null;
        var content = resp.Text();
        if (SkillFields(content) is not var (name, desc, metadata)) return null;
        var files = new List<SnapshotFile> { new("SKILL.md", Encoding.UTF8.GetBytes(content)) };
        var others = e.Files.Where(f => f.ToLowerInvariant() != "skill.md").ToList();
        var fetched = Blob.ParallelMap(others, p => HttpRequest.Get($"{skillBase}/{p}").TrySend() is { Ok: true } r ? (p, r.Body) : ((string, byte[])?)null);
        foreach (var f in fetched)
            if (f is var (p, body)) SetFile(files, p, body);
        return Create(name, desc, content, e.Name, skillMdUrl, metadata, files, e.IndexEntry);
    }

    private static string ComputeDigest(byte[] bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string? NormalizeTarPath(string raw)
    {
        if (raw.Length == 0 || raw.Contains('\0') || raw.StartsWith('/') || raw.StartsWith('\\') || raw.Contains('\\')) return null;
        if (raw.Length >= 2 && char.IsAsciiLetter(raw[0]) && raw[1] == ':') return null;
        var parts = raw.Split('/').Where(p => p.Length > 0).ToList();
        if (parts.Count == 0 || parts.Any(p => p is "." or "..")) return null;
        return string.Join("/", parts);
    }

    private static string ReadTarString(byte[] buf, int offset, int len)
    {
        var span = buf.AsSpan(offset, len);
        var nul = span.IndexOf((byte)0);
        return HttpResponse.DecodeUtf8((nul >= 0 ? span[..nul] : span).ToArray());
    }

    private static List<SnapshotFile> ExtractTarGz(byte[] bytes)
    {
        byte[] tar;
        using (var gz = new GZipStream(new MemoryStream(bytes), CompressionMode.Decompress))
        using (var ms = new MemoryStream())
        {
            gz.CopyTo(ms);
            tar = ms.ToArray();
        }
        var files = new List<SnapshotFile>();
        long total = 0;
        var offset = 0;
        while (offset + 512 <= tar.Length)
        {
            var header = tar.AsSpan(offset, 512);
            if (!header.ContainsAnyExcept((byte)0)) break;
            var name = ReadTarString(tar, offset, 100);
            var sizeText = ReadTarString(tar, offset + 124, 12).Trim();
            var typeFlag = tar[offset + 156];
            var prefix = ReadTarString(tar, offset + 345, 155);
            var path = prefix.Length > 0 ? $"{prefix}/{name}" : name;
            var digits = new string(sizeText.TakeWhile(c => c is >= '0' and <= '7').ToArray());
            if (sizeText.Length > 0 && digits.Length == 0) throw new InvalidDataException("Invalid tar entry size");
            var size = digits.Length == 0 ? 0 : (int)Convert.ToInt64(digits, 8);
            offset += 512;
            if (typeFlag is (byte)'2' or (byte)'1') throw new InvalidDataException("Archive links are not supported");
            if (typeFlag is 0 or (byte)'0')
            {
                var end = Math.Min(offset + size, tar.Length);
                var content = tar[offset..end];
                var normalized = NormalizeTarPath(path) ?? throw new InvalidDataException($"Unsafe archive path: {path}");
                total += content.Length;
                if (total > MaxArchiveUnpackedBytes) throw new InvalidDataException("Archive exceeds maximum unpacked size");
                if (files.Count >= MaxArchiveFiles) throw new InvalidDataException("Archive contains too many files");
                SetFile(files, normalized, content);
            }
            offset += (size + 511) / 512 * 512;
        }
        if (files.All(f => f.Path != "SKILL.md")) throw new InvalidDataException("Archive missing root SKILL.md");
        return files;
    }

    private static List<SnapshotFile> ExtractArchive(byte[] bytes, string url, string contentType)
    {
        var lower = url.ToLowerInvariant();
        if (contentType.Contains("application/zip") || lower.EndsWith(".zip") || (bytes.Length >= 2 && bytes[0] == 0x50 && bytes[1] == 0x4b))
            return Archive.ReadZip(bytes, new ArchiveLimits(MaxArchiveUnpackedBytes, MaxArchiveFiles)).Select(f => new SnapshotFile(f.Path, f.Contents)).ToList();
        if (contentType.Contains("application/gzip") || contentType.Contains("application/x-gzip") || lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz")
            || (bytes.Length >= 2 && bytes[0] == 0x1f && bytes[1] == 0x8b))
            return ExtractTarGz(bytes);
        throw new InvalidDataException("Unsupported archive format");
    }

    private static WellKnownSkill? FetchArtifact(V2Entry e)
    {
        var resp = HttpRequest.Get(e.ArtifactUrl).TrySend();
        if (resp is not { Ok: true }) return null;
        var bytes = resp.Body;
        if (ComputeDigest(bytes) != e.Digest) return null;
        if (e.Kind == "skill-md")
        {
            var content = HttpResponse.DecodeUtf8(bytes);
            if (SkillFields(content) is not var (n, d, m)) return null;
            return Create(n, d, content, e.Name, e.ArtifactUrl, m, [new SnapshotFile("SKILL.md", Encoding.UTF8.GetBytes(content))], e.IndexEntry);
        }
        List<SnapshotFile> files;
        try
        {
            files = ExtractArchive(bytes, e.ArtifactUrl, resp.Header("content-type") ?? "");
        }
        catch (Exception ex) when (ex is InvalidDataException or ArchiveInvalidException or ArchiveValidationException)
        {
            return null;
        }
        var skillMd = files.FirstOrDefault(f => f.Path == "SKILL.md");
        if (skillMd == null) return null;
        var text = HttpResponse.DecodeUtf8(skillMd.Contents);
        SetFile(files, "SKILL.md", Encoding.UTF8.GetBytes(text));
        if (SkillFields(text) is not var (name, desc, meta)) return null;
        return Create(name, desc, text, e.Name, e.ArtifactUrl, meta, files, e.IndexEntry);
    }

    public static WellKnownSkill? FetchSkillByEntry(NormalizedEntry entry) => entry switch
    {
        V1Entry v1 => FetchLegacy(v1),
        V2Entry v2 => FetchArtifact(v2),
        _ => null,
    };

    private static (string ScopePath, string RootBaseUrl)? GetScope(string url)
    {
        var u = WebUrl.Parse(url);
        if (u == null) return null;
        var scope = u.Pathname.EndsWith('/') ? u.Pathname[..^1] : u.Pathname;
        return scope.Length == 0 ? null : (scope, $"{u.Protocol}//{u.Host}");
    }

    /// Fetch all skills. Scoped URLs never widen to the host's root index.
    /// <exception cref="ScopeNotFoundException"/>
    public static List<WellKnownSkill> FetchAllSkills(string url, bool includeInternal)
    {
        var candidates = FetchIndexCandidates(url, false);
        var scope = GetScope(url);
        var scoped = scope is { } s ? candidates.Where(c => c.ResolvedBaseUrl != s.RootBaseUrl).ToList() : candidates;
        includeInternal = includeInternal || SkillDiscovery.ShouldInstallInternalSkills();
        foreach (var result in scoped)
        {
            var skills = Blob.ParallelMap(result.Entries, FetchSkillByEntry).Where(x => x != null).Select(x => x!)
                .Where(x => includeInternal || Json.AsBool(Json.Get(x.Metadata, "internal")) != true).ToList();
            if (skills.Count > 0) return skills;
        }
        if (scope is { } sc && scoped.Count < candidates.Count) throw new ScopeNotFoundException(sc.ScopePath, sc.RootBaseUrl);
        return [];
    }

    /// Source identifier: hostname without `www.`.
    public static string GetSourceIdentifier(string url) =>
        WebUrl.Parse(url) is { } u ? u.Hostname.StartsWith("www.") ? u.Hostname[4..] : u.Hostname : "unknown";

    public static string ComputeSkillDigest(WellKnownSkill skill)
    {
        if (Json.NonEmpty(skill.IndexEntry, "digest") is { } d) return d;
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var f in skill.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            h.AppendData(Encoding.UTF8.GetBytes(f.Path));
            h.AppendData([0]);
            h.AppendData(f.Contents);
            h.AppendData([0]);
        }
        return "sha256:" + Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }
}
