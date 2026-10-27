using System.Collections.Concurrent;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;

namespace UniGetUI.PackageEngine.Managers.MacPkgManager.Internal;

/// <summary>One installable .pkg, .dmg or .zip of a feed, with the app it installs when known.</summary>
internal sealed record PkgFeedItem(
    string Id,
    string Name,
    string Version,
    Uri Location,
    string? Sha256,
    long Size,
    IReadOnlyList<string> Architectures,
    Uri? Icon,
    AppBundleInfo? App = null,
    bool WrapsPackage = false)
{
    public FeedFileKind Kind => FeedFile.KindOf(Location.AbsolutePath);

    /// <summary>An app copied into place leaves no receipt: only the app itself shows it is installed.</summary>
    public bool CopiesAnApp => Kind == FeedFileKind.Archive || (Kind == FeedFileKind.DiskImage && !WrapsPackage);
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
    private static readonly TimeSpan IndexTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IndexLifetime = TimeSpan.FromMinutes(5);
    private static readonly string[] IconExtensions = [".png", ".jpg", ".jpeg"];

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

    public static void ForgetCachedIndexes()
    {
        Indexes.Clear();
        Folders.Clear();
    }

    private static readonly ConcurrentDictionary<Uri, IReadOnlyList<PkgFeedItem>> LastKnown = new();

    /// <summary>
    /// Lists a feed. When it cannot be reached (off the VPN, share not mounted) the last listing is
    /// reused, so installed packages keep their state; a web feed's last index also survives restarts.
    /// <paramref name="currentOnly"/> refuses that fallback, for what authorizes an install or checks a feed.
    /// </summary>
    public static IReadOnlyList<PkgFeedItem> List(Uri location, CancellationToken cancellationToken = default,
        bool currentOnly = false)
    {
        try
        {
            var items = location.IsFile ? ScanFolderCached(location.LocalPath, cancellationToken) : ReadIndex(location, cancellationToken);
            LastKnown[location] = items;
            return items;
        }
        catch (Exception ex) when (!currentOnly && (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
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
                int order = best is null ? 1 : CompareReleases(item, best, compare);
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

    /// <summary>
    /// Orders two releases of one package: by their version, then, when a vendor republished the same
    /// package version, by the version of the app inside, which is what an app in place is compared with.
    /// </summary>
    public static int CompareReleases(PkgFeedItem a, PkgFeedItem b, Func<string, string, int?> compare)
    {
        int order = compare(a.Version, b.Version) ?? 0;
        if (order == 0 && a.App?.Version is { } appA && b.App?.Version is { } appB)
            order = compare(appA, appB) ?? 0;
        return order;
    }

    // ── Folder feeds ───────────────────────────────────────────────────────

    // Icon and details lookups list the feed once per package, so a scan is reused briefly instead of
    // walking a network share again for each; a refresh of the manager's indexes forgets it.
    private static readonly TimeSpan FolderLifetime = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, CachedIndex> Folders = new(StringComparer.Ordinal);

    private static IReadOnlyList<PkgFeedItem> ScanFolderCached(string directory, CancellationToken cancellationToken)
    {
        if (Folders.TryGetValue(directory, out var cached) && DateTime.UtcNow - cached.FetchedUtc < FolderLifetime)
            return cached.Items;
        var items = ScanFolder(directory, cancellationToken);
        Folders[directory] = new CachedIndex(DateTime.UtcNow, items);
        return items;
    }

    // A file read for the first time mounts a disk image or reads a package's table of contents, so
    // several are read at once to keep a cold scan of a large feed within the listing timeout.
    internal static IReadOnlyList<PkgFeedItem> ScanFolder(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"The package feed folder {directory} does not exist or is not mounted.");

        string[] files = EnumeratePackages(directory).ToArray();
        var items = new PkgFeedItem?[files.Length];
        Parallel.For(0, files.Length, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            i => items[i] = TryReadFile(files[i]) is { } metadata ? ToItem(metadata, files[i], sha256: null) : null);
        return items.OfType<PkgFeedItem>().ToArray();
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
        // Bundle-style packages are directories; only flat packages, disk images and archives (files) are read.
        return Directory.EnumerateFiles(directory, "*", options)
            .Where(f => FeedFile.Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
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
            return ReadFile(file);
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            Logger.Warn($"Skipping {file}: it could not be read as an installer package ({ex.Message})");
            return null;
        }
    }

    internal static bool IsUnreadable(Exception ex) => ex is IOException or UnauthorizedAccessException
        or InvalidDataException or System.Xml.XmlException or EndOfStreamException;

    /// <summary>Reads a feed file, from the caches when it has not changed; throws when it cannot be read.</summary>
    internal static PkgMetadata ReadFile(string file)
    {
        lock (FileLocks.GetOrAdd(file, _ => new object()))
        {
            var info = new FileInfo(file);
            if (ParsedFiles.TryGetValue(file, out var cached)
                && cached.Size == info.Length && cached.WriteTimeUtc == info.LastWriteTimeUtc)
                return cached.Metadata;
            // What was read from a file is kept on disk per file version, so a file is read once. An app's
            // icon comes with reading it; a package's waits for FindOrExtractIcon, as its payload is large.
            string icon = ExtractedIconPath(info);
            string saved = Path.ChangeExtension(icon, ".json");
            var metadata = TryLoadMetadata(saved);
            if (metadata is null)
            {
                metadata = PkgMetadata.Read(file, File.Exists(icon) ? null : icon, iconFromPayload: false);
                TryWriteFile(saved, PkgJson.SerializeToUtf8Bytes(metadata, PkgMetadataJsonContext.Default.PkgMetadata));
            }
            ParsedFiles[file] = new ParsedFile(info.Length, info.LastWriteTimeUtc, metadata);
            return metadata;
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
        metadata.App,
        metadata.WrapsPackage);

    /// <summary>An image next to the package with the same name, else the icon already taken from the package's app.</summary>
    internal static string? FindIcon(string packageFile)
    {
        string stem = Path.Combine(Path.GetDirectoryName(packageFile)!, Path.GetFileNameWithoutExtension(packageFile));
        if (IconExtensions.Select(e => stem + e).FirstOrDefault(File.Exists) is { } sibling)
            return sibling;
        string extracted = ExtractedIconPath(new FileInfo(packageFile));
        return File.Exists(extracted) ? extracted : null;
    }

    private const string NoIconSuffix = ".none";

    /// <summary>
    /// The package's icon, extracted from its app on first request. A file found to have none is
    /// remembered, per reader version and file version, so its payload is not searched again.
    /// </summary>
    internal static string? FindOrExtractIcon(string packageFile)
    {
        if (FindIcon(packageFile) is { } icon)
            return icon;
        try
        {
            lock (FileLocks.GetOrAdd(packageFile, _ => new object()))
            {
                string destination = ExtractedIconPath(new FileInfo(packageFile));
                if (File.Exists(destination))
                    return destination;
                if (File.Exists(destination + NoIconSuffix))
                    return null;
                PkgMetadata.Read(packageFile, destination);
                if (File.Exists(destination))
                    return destination;
                TryWriteFile(destination + NoIconSuffix, []);
                return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException
            or System.Xml.XmlException or EndOfStreamException)
        {
            Logger.Warn($"Could not extract the icon of {packageFile}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Where extracted icons are kept. Replaceable so tests stay out of the user's cache.</summary>
    internal static string? TestIconDirectoryOverride { get; set; }

    // Bump when what is read from a file changes, so files are read again instead of trusting what
    // an older reader made of them (its metadata, or an icon it could not find).
    private const string ReaderVersion = "4";

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
                ? PkgJson.Deserialize(File.ReadAllBytes(saved), PkgMetadataJsonContext.Default.PkgMetadata)
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

        // The whole exchange has one deadline: the client's own timeout ends once the headers arrive.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(IndexTimeout);
        using var limited = new MemoryStream();
        try
        {
            using var client = new HttpClient(CoreTools.GenericHttpClientParameters);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
            using var response = client.GetAsync(indexUrl, HttpCompletionOption.ResponseHeadersRead, deadline.Token)
                .GetAwaiter().GetResult();
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new FileNotFoundException($"The package feed has no index at {indexUrl}. Run 'unigetui pkg-index' on its folder.");
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxIndexBytes)
                throw new InvalidDataException($"The package feed index at {indexUrl} is larger than {MaxIndexBytes} bytes.");

            using var body = response.Content.ReadAsStream(deadline.Token);
            byte[] buffer = new byte[81920];
            int read;
            while ((read = body.ReadAsync(buffer, deadline.Token).AsTask().GetAwaiter().GetResult()) > 0)
            {
                if (limited.Length + read > MaxIndexBytes)
                    throw new InvalidDataException($"The package feed index at {indexUrl} is larger than {MaxIndexBytes} bytes.");
                limited.Write(buffer, 0, read);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException($"The package feed index at {indexUrl} did not arrive within {IndexTimeout.TotalSeconds} seconds.");
        }

        byte[] json = limited.ToArray();
        var items = ParseIndex(json, indexUrl);
        Indexes[indexUrl] = new CachedIndex(DateTime.UtcNow, items);
        SaveIndex(indexUrl, json);
        return items;
    }

    internal static IReadOnlyList<PkgFeedItem> ParseIndex(byte[] json, Uri indexUrl)
    {
        var index = PkgJson.Deserialize(json, PkgFeedJsonContext.Default.PkgFeedIndex)
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
                entry.App?.ToInfo(),
                entry.WrapsPackage is true));
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
