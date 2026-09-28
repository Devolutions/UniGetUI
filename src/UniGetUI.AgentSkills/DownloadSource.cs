// Download a SKILL.md or archive URL into a temp directory (port of download-source.ts).

using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Skills;

internal enum DownloadKind
{
    SkillMd,
    Archive,
}

internal sealed record DownloadedSource(string RootDir, string TempDir, DownloadKind Kind);

internal sealed record DownloadOptions(long? DownloadMaxBytes = null, long? ExtractMaxBytes = null, long? ExtractMaxFiles = null);

internal sealed class DownloadException(string message) : Exception(message);

internal static class DownloadSource
{
    private const long DefaultDownloadMaxBytes = 10 * 1024 * 1024;
    private const long DefaultExtractMaxBytes = 25 * 1024 * 1024;
    private const long DefaultExtractMaxFiles = 1000;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(30);

    internal sealed record Limits(long DownloadMaxBytes, long ExtractMaxBytes, long ExtractMaxFiles);

    private static long EnvLimit(string name, long fallback) =>
        Sys.Env(name) is { } v && Git.ParseIntPrefix(v) is { } n && n > 0 ? n : fallback;

    internal static Limits GetLimits(DownloadOptions o)
    {
        static long Or(long? v, long d) => v is > 0 ? v.Value : d;
        return new Limits(
            EnvLimit("SKILLS_DOWNLOAD_MAX_BYTES", Or(o.DownloadMaxBytes, DefaultDownloadMaxBytes)),
            EnvLimit("SKILLS_EXTRACT_MAX_BYTES", Or(o.ExtractMaxBytes, DefaultExtractMaxBytes)),
            EnvLimit("SKILLS_EXTRACT_MAX_FILES", Or(o.ExtractMaxFiles, DefaultExtractMaxFiles)));
    }

    internal static string? ValidateArchivePath(string p)
    {
        var n = p.Replace('\\', '/');
        if (n.StartsWith("./")) n = n[2..];
        if (n.Length == 0 || n.EndsWith('/')) return n;
        if (n.StartsWith('/')) return null;
        if (n.Length >= 3 && char.IsAsciiLetter(n[0]) && n[1] == ':' && n[2] == '/') return null;
        return n.Split('/').Contains("..") ? null : n;
    }

    private static bool IsValidSkillMarkdown(byte[] bytes)
    {
        try
        {
            var data = Frontmatter.Parse(new UTF8Encoding(false).GetString(bytes)).Data;
            return Json.Str(data, "name") != null && Json.Str(data, "description") != null;
        }
        catch (YamlParseException)
        {
            return false;
        }
    }

    private sealed class ExtractFailed : Exception;

    private static void ExtractZip(byte[] data, string dir, Limits l)
    {
        List<(string Path, byte[] Contents)> files;
        try
        {
            files = Archive.ReadZip(data, new ArchiveLimits(l.ExtractMaxBytes, l.ExtractMaxFiles));
        }
        catch (ArchiveInvalidException)
        {
            throw new ExtractFailed();
        }
        foreach (var (p, contents) in files)
        {
            var target = NodePath.Join(dir, p);
            if (!NodePath.IsPathSafe(dir, target)) throw new ArchiveValidationException($"Archive contains unsafe path: {p}");
            if (p.EndsWith('/')) continue;
            Directory.CreateDirectory(NodePath.Dirname(target));
            File.WriteAllBytes(target, contents);
        }
    }

    private static void ExtractTar(byte[] data, string dir, Limits l)
    {
        Stream input = new MemoryStream(data);
        if (data.Length >= 2 && data[0] == 0x1f && data[1] == 0x8b) input = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new TarReader(input);
        long count = 0, bytes = 0;
        try
        {
            while (reader.GetNextEntry() is { } entry)
            {
                var raw = entry.Name;
                var safe = ValidateArchivePath(raw) ?? throw new ArchiveValidationException($"Archive contains unsafe path: {raw}");
                var target = NodePath.Join(dir, safe);
                if (!NodePath.IsPathSafe(dir, target)) throw new ArchiveValidationException($"Archive contains unsafe path: {raw}");
                count++;
                if (count > l.ExtractMaxFiles)
                    throw new ArchiveValidationException($"Archive contains too many files ({count}). Maximum is {l.ExtractMaxFiles}. Set SKILLS_EXTRACT_MAX_FILES to override.");
                bytes += entry.Length;
                if (bytes > l.ExtractMaxBytes)
                    throw new ArchiveValidationException($"Archive extracts to more than {l.ExtractMaxBytes} bytes. Set SKILLS_EXTRACT_MAX_BYTES to override.");
                if (entry.EntryType is TarEntryType.RegularFile or TarEntryType.V7RegularFile or TarEntryType.ContiguousFile)
                {
                    Directory.CreateDirectory(NodePath.Dirname(target));
                    using var ms = new MemoryStream();
                    entry.DataStream?.CopyTo(ms);
                    File.WriteAllBytes(target, ms.ToArray());
                }
                else if (entry.EntryType == TarEntryType.Directory)
                {
                    Directory.CreateDirectory(target);
                }
            }
        }
        catch (Exception e) when (e is InvalidDataException or FormatException or EndOfStreamException or IOException)
        {
            throw new ExtractFailed();
        }
        if (count == 0) throw new ExtractFailed();
    }

    /// true when extracted; false when the data is not a supported archive.
    /// Validation failures propagate as <see cref="ArchiveValidationException"/>.
    internal static bool TryExtract(byte[] data, string dir, Limits l)
    {
        var isZip = data.Length >= 2 && data[0] == 0x50 && data[1] == 0x4b;
        try
        {
            if (isZip) ExtractZip(data, dir, l);
            else ExtractTar(data, dir, l);
            return true;
        }
        catch (Exception e) when (e is ExtractFailed or ArchiveValidationException)
        {
            try { Fs.RemoveAll(dir); } catch { /* ignore */ }
            Directory.CreateDirectory(dir);
            if (e is ArchiveValidationException) throw;
            return false;
        }
    }

    internal static string? SingleTopLevelDirectory(string dir)
    {
        var entries = Fs.TryReadDir(dir).Where(e => e.Name != "__MACOSX").ToList();
        return entries.Count == 1 && entries[0].IsDirectory ? NodePath.Join(dir, entries[0].Name) : null;
    }

    private static byte[] Download(string url, Limits l)
    {
        HttpResponse resp;
        try
        {
            resp = HttpRequest.Get(url).Timeout(FetchTimeout).MaxBytes(l.DownloadMaxBytes).Send();
        }
        catch (HttpTooLargeException)
        {
            throw new DownloadException($"Download is larger than {l.DownloadMaxBytes} bytes. Set SKILLS_DOWNLOAD_MAX_BYTES to override.");
        }
        catch (HttpException)
        {
            throw new DownloadException("fetch failed");
        }
        if (!resp.Ok) throw new DownloadException($"Download failed with HTTP {resp.Status}");
        return resp.Body;
    }

    /// Download `url` and prepare it as a local skill source.
    /// <exception cref="DownloadException"/>
    /// <exception cref="ArchiveValidationException"/>
    public static DownloadedSource Fetch(string url, DownloadOptions? options = null)
    {
        var l = GetLimits(options ?? new DownloadOptions());
        var tempDir = Sys.MkdTemp("skills-download-");
        try
        {
            var data = Download(url, l);
            File.WriteAllBytes(NodePath.Join(tempDir, "source.download"), data);
            if (data.Length == 0) throw new DownloadException("Downloaded URL is empty");
            if (IsValidSkillMarkdown(data))
            {
                var skillDir = NodePath.Join(tempDir, "skill");
                Directory.CreateDirectory(skillDir);
                File.WriteAllBytes(NodePath.Join(skillDir, "SKILL.md"), data);
                return new DownloadedSource(skillDir, tempDir, DownloadKind.SkillMd);
            }
            var extractDir = NodePath.Join(tempDir, "extract");
            Directory.CreateDirectory(extractDir);
            if (TryExtract(data, extractDir, l))
                return new DownloadedSource(SingleTopLevelDirectory(extractDir) ?? extractDir, tempDir, DownloadKind.Archive);
            throw new DownloadException("Downloaded URL is not a valid SKILL.md file or supported archive");
        }
        catch
        {
            try { Fs.RemoveAll(tempDir); } catch { /* ignore */ }
            throw;
        }
    }
}
