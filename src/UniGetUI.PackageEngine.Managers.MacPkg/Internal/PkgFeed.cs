using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;

namespace UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;

/// <summary>One installable .pkg or .dmg of a feed, with the app it installs when known.</summary>
internal sealed record PkgFeedItem(
    string Id,
    string Name,
    string Version,
    Uri Location,
    string? Sha256,
    long Size,
    IReadOnlyList<string> Architectures,
    Uri? Icon,
    AppBundleInfo? App = null)
{
    public bool IsDiskImage => DiskImage.IsDiskImage(Location.AbsolutePath);
}

/// <summary>
/// Lists the packages of a feed. A <c>file:</c> location (a local folder or a mounted share) is
/// scanned directly; an <c>https:</c> location is read through the index.json beside its packages.
/// </summary>
internal static class PkgFeed
{
    internal const string IndexFileName = "index.json";
    private const int MaxScanDepth = 3;
    private const int MaxIndexBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan IndexLifetime = TimeSpan.FromMinutes(5);
    private static readonly string[] IconExtensions = [".png", ".jpg", ".jpeg"];
    private static readonly string[] PackageExtensions = [".pkg", ".mpkg", ".dmg"];

    private readonly record struct ParsedFile(long Size, DateTime WriteTimeUtc, PkgMetadata Metadata);
    private readonly record struct CachedIndex(DateTime FetchedUtc, IReadOnlyList<PkgFeedItem> Items);

    private static readonly ConcurrentDictionary<string, ParsedFile> ParsedFiles = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<Uri, CachedIndex> Indexes = new();

    /// <summary>
    /// Turns the location an administrator wrote (an https URL, a file URL or an absolute path)
    /// into a feed address. Plain http is refused except on loopback, since the index pins hashes.
    /// </summary>
    public static bool TryParseLocation(string? text, out Uri location)
    {
        location = null!;
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            return false;
        if (Path.IsPathRooted(text) && !text.Contains("://", StringComparison.Ordinal))
        {
            location = new Uri(Path.GetFullPath(text));
            return true;
        }
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri))
            return false;
        if (uri.IsFile || uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        {
            location = uri;
            return true;
        }
        return false;
    }

    public static void ForgetCachedIndexes() => Indexes.Clear();

    private static readonly ConcurrentDictionary<Uri, IReadOnlyList<PkgFeedItem>> LastKnown = new();

    /// <summary>
    /// Lists a feed. When it cannot be reached (off the VPN, share not mounted) the last listing is
    /// reused, so installed packages keep their state; a web feed's last index also survives restarts.
    /// </summary>
    public static IReadOnlyList<PkgFeedItem> List(Uri location, CancellationToken cancellationToken = default)
    {
        try
        {
            var items = location.IsFile ? ScanFolder(location.LocalPath, cancellationToken) : ReadIndex(location, cancellationToken);
            LastKnown[location] = items;
            return items;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            if (LastKnown.TryGetValue(location, out var known) || TryReadSavedIndex(location, out known))
            {
                Logger.Warn($"The package feed {location} is unreachable ({ex.Message}); using its last known contents.");
                return known;
            }
            throw;
        }
    }

    private static string SavedIndexPath(Uri indexUrl) => Path.Combine(CoreData.UniGetUICacheDirectory_Data, "PkgFeeds",
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(indexUrl.AbsoluteUri))) + ".json");

    private static void SaveIndex(Uri indexUrl, byte[] json)
    {
        try
        {
            string path = SavedIndexPath(indexUrl);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Could not keep a copy of the package feed index {indexUrl}: {ex.Message}");
        }
    }

    private static bool TryReadSavedIndex(Uri location, out IReadOnlyList<PkgFeedItem> items)
    {
        items = [];
        if (location.IsFile)
            return false;
        try
        {
            Uri indexUrl = IndexUrl(location);
            string path = SavedIndexPath(indexUrl);
            if (!File.Exists(path))
                return false;
            items = ParseIndex(File.ReadAllBytes(path), indexUrl);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>
    /// The newest package of each identifier that this Mac can run, preferring a native build over
    /// one that needs Rosetta when both carry the same version.
    /// </summary>
    public static IReadOnlyList<PkgFeedItem> Latest(IEnumerable<PkgFeedItem> items, Func<string, string, int?> compare,
        Architecture machine)
    {
        var latest = new List<PkgFeedItem>();
        foreach (var group in items.GroupBy(i => i.Id, StringComparer.Ordinal))
        {
            PkgFeedItem? best = null;
            int bestFit = 0;
            foreach (var item in group)
            {
                int fit = PkgMetadata.ArchitectureFit(item.Architectures, machine);
                if (fit == 0)
                    continue;
                int order = best is null ? 1 : compare(item.Version, best.Version) ?? 0;
                if (order > 0 || (order == 0 && fit > bestFit))
                {
                    best = item;
                    bestFit = fit;
                }
            }
            if (best is not null)
                latest.Add(best);
        }
        return latest;
    }

    // ── Folder feeds ───────────────────────────────────────────────────────

    internal static IReadOnlyList<PkgFeedItem> ScanFolder(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"The package feed folder {directory} does not exist or is not mounted.");

        var items = new List<PkgFeedItem>();
        foreach (string file in EnumeratePackages(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadFile(file) is { } metadata)
                items.Add(ToItem(metadata, file, sha256: null));
        }
        return items;
    }

    internal static IEnumerable<string> EnumeratePackages(string directory)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = MaxScanDepth,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };
        // Bundle-style packages are directories; only flat packages and disk images (files) are read.
        return Directory.EnumerateFiles(directory, "*", options)
            .Where(f => PackageExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal);
    }

    // Listings run in parallel at startup, and a second mount of a disk image that is already
    // attached comes back empty, so each file is read by one listing at a time.
    private static readonly ConcurrentDictionary<string, object> FileLocks = new(StringComparer.Ordinal);

    internal static void ForgetReadFiles() => ParsedFiles.Clear();

    internal static PkgMetadata? TryReadFile(string file)
    {
        try
        {
            lock (FileLocks.GetOrAdd(file, _ => new object()))
            {
                var info = new FileInfo(file);
                if (ParsedFiles.TryGetValue(file, out var cached)
                    && cached.Size == info.Length && cached.WriteTimeUtc == info.LastWriteTimeUtc)
                    return cached.Metadata;
                // What was read from a file, its icon included, is kept on disk per file version, so a
                // file is read once; one without an icon is not searched again at every launch.
                string icon = ExtractedIconPath(info);
                string saved = Path.ChangeExtension(icon, ".json");
                var metadata = TryLoadMetadata(saved);
                if (metadata is null)
                {
                    metadata = PkgMetadata.Read(file, File.Exists(icon) ? null : icon);
                    TryWriteFile(saved, JsonSerializer.SerializeToUtf8Bytes(metadata, PkgMetadataJsonContext.Default.PkgMetadata));
                }
                ParsedFiles[file] = new ParsedFile(info.Length, info.LastWriteTimeUtc, metadata);
                return metadata;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Xml.XmlException or EndOfStreamException)
        {
            Logger.Warn($"Skipping {file}: it could not be read as an installer package ({ex.Message})");
            return null;
        }
    }

    private static PkgFeedItem ToItem(PkgMetadata metadata, string file, string? sha256) => new(
        metadata.Id,
        metadata.Title ?? Path.GetFileNameWithoutExtension(file),
        metadata.Version,
        new Uri(file),
        sha256,
        new FileInfo(file).Length,
        metadata.Architectures,
        FindIcon(file) is { } icon ? new Uri(icon) : null,
        metadata.App);

    /// <summary>An image next to the package with the same name, else the icon taken from the package's app.</summary>
    internal static string? FindIcon(string packageFile)
    {
        string stem = Path.Combine(Path.GetDirectoryName(packageFile)!, Path.GetFileNameWithoutExtension(packageFile));
        if (IconExtensions.Select(e => stem + e).FirstOrDefault(File.Exists) is { } sibling)
            return sibling;
        string extracted = ExtractedIconPath(new FileInfo(packageFile));
        return File.Exists(extracted) ? extracted : null;
    }

    /// <summary>Where extracted icons are kept. Replaceable so tests stay out of the user's cache.</summary>
    internal static string? TestIconDirectoryOverride { get; set; }

    // Bump when what is read from a file changes, so files are read again instead of trusting what
    // an older reader made of them (its metadata, or an icon it could not find).
    private const string ReaderVersion = "3";

    // Named from the reader version and the file's path, size and write time, so a replaced
    // package gets its new icon.
    private static string ExtractedIconPath(FileInfo package) => Path.Combine(
        TestIconDirectoryOverride ?? Path.Combine(CoreData.UniGetUICacheDirectory_Icons, "PkgFeeds"),
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{ReaderVersion}|{package.FullName}|{package.Length}|{package.LastWriteTimeUtc.Ticks}")))[..32] + ".png");

    private static PkgMetadata? TryLoadMetadata(string saved)
    {
        try
        {
            return File.Exists(saved)
                ? JsonSerializer.Deserialize(File.ReadAllBytes(saved), PkgMetadataJsonContext.Default.PkgMetadata)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void TryWriteFile(string path, byte[] contents)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, contents);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Debug($"Could not write the package feed cache file {path}: {ex.Message}");
        }
    }

    // ── Web feeds ──────────────────────────────────────────────────────────

    internal static Uri IndexUrl(Uri location) =>
        location.AbsolutePath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? location
            : new Uri(location.AbsoluteUri.TrimEnd('/') + "/" + IndexFileName);

    private static IReadOnlyList<PkgFeedItem> ReadIndex(Uri location, CancellationToken cancellationToken)
    {
        Uri indexUrl = IndexUrl(location);
        if (Indexes.TryGetValue(indexUrl, out var cached) && DateTime.UtcNow - cached.FetchedUtc < IndexLifetime)
            return cached.Items;

        using var client = new HttpClient(CoreTools.GenericHttpClientParameters);
        client.Timeout = TimeSpan.FromSeconds(30);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
        using var response = client.GetAsync(indexUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .GetAwaiter().GetResult();
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new FileNotFoundException($"The package feed has no index at {indexUrl}. Run 'unigetui pkg-index' on its folder.");
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxIndexBytes)
            throw new InvalidDataException($"The package feed index at {indexUrl} is larger than {MaxIndexBytes} bytes.");

        using var body = response.Content.ReadAsStream(cancellationToken);
        using var limited = new MemoryStream();
        byte[] buffer = new byte[81920];
        int read;
        while ((read = body.Read(buffer)) > 0)
        {
            if (limited.Length + read > MaxIndexBytes)
                throw new InvalidDataException($"The package feed index at {indexUrl} is larger than {MaxIndexBytes} bytes.");
            limited.Write(buffer, 0, read);
        }

        byte[] json = limited.ToArray();
        var items = ParseIndex(json, indexUrl);
        Indexes[indexUrl] = new CachedIndex(DateTime.UtcNow, items);
        SaveIndex(indexUrl, json);
        return items;
    }

    internal static IReadOnlyList<PkgFeedItem> ParseIndex(byte[] json, Uri indexUrl)
    {
        var index = JsonSerializer.Deserialize(json, PkgFeedJsonContext.Default.PkgFeedIndex)
            ?? throw new InvalidDataException("The package feed index is empty.");
        if (index.Version != PkgFeedIndex.CurrentVersion)
            throw new InvalidDataException($"Unsupported package feed index version: {index.Version}.");

        var items = new List<PkgFeedItem>();
        foreach (var entry in index.Packages ?? [])
        {
            if (entry is null || string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Version)
                || !IsSha256(entry.Sha256) || ResolveWithin(indexUrl, entry.File) is not { } file)
            {
                Logger.Warn($"Skipping an invalid entry ({entry?.Id ?? "null"}) in the package feed index {indexUrl}.");
                continue;
            }
            items.Add(new PkgFeedItem(
                entry.Id.Trim(),
                string.IsNullOrWhiteSpace(entry.Name) ? entry.Id.Trim() : entry.Name.Trim(),
                entry.Version.Trim(),
                file,
                entry.Sha256.ToLowerInvariant(),
                entry.Size,
                entry.Architectures ?? [],
                ResolveWithin(indexUrl, entry.Icon),
                entry.App?.ToInfo()));
        }
        return items;
    }

    // Index paths are relative and may not climb out of the feed, so an index cannot point
    // installs at another host or scheme.
    internal static Uri? ResolveWithin(Uri indexUrl, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || relative.Contains('\\') || relative.StartsWith('/')
            || relative.Contains("://", StringComparison.Ordinal)
            || relative.Split('/').Any(segment => segment is ".." or "." or ""))
            return null;
        var resolved = new Uri(indexUrl, string.Join('/', relative.Split('/').Select(Uri.EscapeDataString)));
        string root = indexUrl.AbsoluteUri[..(indexUrl.AbsoluteUri.LastIndexOf('/') + 1)];
        return resolved.AbsoluteUri.StartsWith(root, StringComparison.Ordinal) ? resolved : null;
    }

    internal static bool IsSha256(string? value) =>
        value is { Length: 64 } && value.All(Uri.IsHexDigit);

    internal static string ComputeSha256(string file)
    {
        using var stream = File.OpenRead(file);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
