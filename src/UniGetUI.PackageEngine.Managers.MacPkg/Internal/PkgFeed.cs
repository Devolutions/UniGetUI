using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Managers.InstallerFeeds;

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
    private static readonly string[] IconExtensions = [".png", ".jpg", ".jpeg"];

    private readonly record struct ParsedFile(long Size, DateTime WriteTimeUtc, PkgMetadata Metadata);

    private static readonly ConcurrentDictionary<string, ParsedFile> ParsedFiles = new(StringComparer.Ordinal);
    private static readonly FeedListing<PkgFeedItem> Listing = new(ScanFolder, ParseIndex, "PkgFeeds");

    public static void ForgetCachedIndexes() => Listing.ForgetCachedIndexes();

    /// <summary>
    /// Lists a feed. When it cannot be reached (off the VPN, share not mounted) the last listing is
    /// reused, so installed packages keep their state; a web feed's last index also survives restarts.
    /// <paramref name="currentOnly"/> refuses that fallback, for what authorizes an install or checks a feed.
    /// </summary>
    public static IReadOnlyList<PkgFeedItem> List(Uri location, CancellationToken cancellationToken = default,
        bool currentOnly = false) => Listing.List(location, cancellationToken, currentOnly);

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

    // Bundle-style packages are directories; only flat packages, disk images and archives (files) are read.
    internal static IEnumerable<string> EnumeratePackages(string directory) =>
        FeedLocation.EnumerateFiles(directory, FeedFile.Extensions);

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
                TryWriteFile(saved, FeedJson.SerializeToUtf8Bytes(metadata, PkgMetadataJsonContext.Default.PkgMetadata));
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
                ? FeedJson.Deserialize(File.ReadAllBytes(saved), PkgMetadataJsonContext.Default.PkgMetadata)
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

    internal static IReadOnlyList<PkgFeedItem> ParseIndex(byte[] json, Uri indexUrl)
    {
        var index = FeedJson.Deserialize(json, FeedIndexJsonContext.Default.FeedIndex)
            ?? throw new InvalidDataException("The package feed index is empty.");
        if (index.Version != FeedIndex.CurrentVersion)
            throw new InvalidDataException($"Unsupported package feed index version: {index.Version}.");

        var items = new List<PkgFeedItem>();
        foreach (var entry in index.EntriesFor(FeedPlatform.MacOS))
        {
            if (entry is not null && !FeedLocation.HasExtension(entry.File ?? "", FeedFile.Extensions))
                continue;
            if (entry is null || string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Version)
                || !FeedLocation.IsSha256(entry.Sha256) || FeedLocation.ResolveWithin(indexUrl, entry.File) is not { } file)
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
                FeedLocation.ResolveWithin(indexUrl, entry.Icon),
                PkgIndexApp.ToInfo(entry.App),
                entry.WrapsPackage is true));
        }
        return items;
    }
}
