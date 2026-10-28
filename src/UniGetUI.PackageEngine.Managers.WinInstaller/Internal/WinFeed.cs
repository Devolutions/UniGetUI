using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Managers.InstallerFeeds;

namespace UniGetUI.PackageEngine.Managers.WinInstallerManager.Internal;

internal sealed record WinFeedItem(
    string Id,
    string Name,
    string Version,
    Uri Location,
    string? Sha256,
    long Size,
    IReadOnlyList<string> Architectures,
    Uri? Icon,
    InstallerDetails Details);

internal static class WinFeed
{
    private static readonly string[] IconExtensions = [".png", ".jpg", ".jpeg", ".ico"];

    private readonly record struct ParsedFile(long Size, DateTime WriteTimeUtc, DateTime ManifestWriteTimeUtc, InstallerMetadata Metadata);

    private static readonly ConcurrentDictionary<string, ParsedFile> ParsedFiles = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, object> FileLocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly FeedListing<WinFeedItem> Listing = new(ScanFolder, ParseIndex, "InstallerFeeds");

    public static void ForgetCachedIndexes() => Listing.ForgetCachedIndexes();

    internal static void ForgetReadFiles() => ParsedFiles.Clear();

    public static IReadOnlyList<WinFeedItem> List(Uri location, CancellationToken cancellationToken = default,
        bool currentOnly = false) => Listing.List(location, cancellationToken, currentOnly);

    public static IReadOnlyList<WinFeedItem> Latest(IEnumerable<WinFeedItem> items, Func<string, string, int?> compare,
        Architecture machine)
    {
        var latest = new List<WinFeedItem>();
        foreach (var group in items.GroupBy(i => i.Id, StringComparer.OrdinalIgnoreCase))
        {
            WinFeedItem? best = null;
            int bestFit = 0;
            foreach (var item in group)
            {
                int fit = InstallerFile.ArchitectureFit(item.Architectures, machine);
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

    internal static IEnumerable<string> EnumeratePackages(string directory) =>
        FeedLocation.EnumerateFiles(directory, InstallerFile.Extensions).Where(f => !InstallerFile.IsUninstaller(f));

    internal static IReadOnlyList<WinFeedItem> ScanFolder(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"The package feed folder {directory} does not exist or is not mounted.");
        string[] files = EnumeratePackages(directory).ToArray();
        var items = new WinFeedItem?[files.Length];
        Parallel.For(0, files.Length, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = cancellationToken },
            i => items[i] = TryReadFile(files[i]) is { } metadata ? ToItem(metadata, files[i]) : null);
        return items.OfType<WinFeedItem>().ToArray();
    }

    internal static InstallerMetadata? TryReadFile(string file)
    {
        try
        {
            return ReadFile(file);
        }
        catch (Exception ex) when (IsUnreadable(ex))
        {
            Logger.Warn($"Skipping {file}: {ex.Message}");
            return null;
        }
    }

    internal static bool IsUnreadable(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidDataException or EndOfStreamException;

    internal static InstallerMetadata ReadFile(string file)
    {
        lock (FileLocks.GetOrAdd(file, _ => new object()))
        {
            var info = new FileInfo(file);
            var manifest = new FileInfo(InstallerFile.ManifestPath(file));
            DateTime manifestTime = manifest.Exists ? manifest.LastWriteTimeUtc : DateTime.MinValue;
            if (ParsedFiles.TryGetValue(file, out var cached) && cached.Size == info.Length
                && cached.WriteTimeUtc == info.LastWriteTimeUtc && cached.ManifestWriteTimeUtc == manifestTime)
                return cached.Metadata;
            var metadata = InstallerFile.Read(file);
            if (metadata.Icon is { } icon)
                SaveIcon(info, icon);
            ParsedFiles[file] = new ParsedFile(info.Length, info.LastWriteTimeUtc, manifestTime, metadata with { Icon = null });
            return metadata;
        }
    }

    private static WinFeedItem ToItem(InstallerMetadata metadata, string file) => new(
        metadata.Id,
        metadata.Name,
        metadata.Version,
        new Uri(file),
        null,
        new FileInfo(file).Length,
        metadata.Architectures,
        FindIcon(file) is { } icon ? new Uri(icon) : null,
        metadata.Details);

    internal static string? FindIcon(string installerFile)
    {
        string stem = Path.Combine(Path.GetDirectoryName(installerFile)!, Path.GetFileNameWithoutExtension(installerFile));
        if (IconExtensions.Select(e => stem + e).FirstOrDefault(File.Exists) is { } sibling)
            return sibling;
        var info = new FileInfo(installerFile);
        return new[] { ".png", ".ico" }.Select(e => ExtractedIconPath(info, e)).FirstOrDefault(File.Exists);
    }

    internal static string? TestIconDirectoryOverride { get; set; }

    private static string ExtractedIconPath(FileInfo file, string extension) => Path.Combine(
        TestIconDirectoryOverride ?? Path.Combine(CoreData.UniGetUICacheDirectory_Icons, "InstallerFeeds"),
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}")))[..32] + extension);

    private static void SaveIcon(FileInfo file, byte[] icon)
    {
        string path = ExtractedIconPath(file, PeReader.IsPng(icon) ? ".png" : ".ico");
        if (File.Exists(path))
            return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temporary, icon);
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Debug($"Could not keep the icon of {file.FullName}: {ex.Message}");
        }
    }

    internal static IReadOnlyList<WinFeedItem> ParseIndex(byte[] json, Uri indexUrl)
    {
        var index = FeedJson.Deserialize(json, FeedIndexJsonContext.Default.FeedIndex)
            ?? throw new InvalidDataException("The package feed index is empty.");
        if (index.Version != FeedIndex.CurrentVersion)
            throw new InvalidDataException($"Unsupported package feed index version: {index.Version}.");

        var items = new List<WinFeedItem>();
        foreach (var entry in index.EntriesFor(FeedPlatform.Windows))
        {
            if (entry is not null && !FeedLocation.HasExtension(entry.File ?? "", InstallerFile.Extensions))
                continue;
            if (entry is null || string.IsNullOrWhiteSpace(entry.Id) || string.IsNullOrWhiteSpace(entry.Version)
                || !FeedLocation.IsSha256(entry.Sha256) || FeedLocation.ResolveWithin(indexUrl, entry.File) is not { } file
                || InstallerDetails.FromIndex(entry.Windows) is not { } details
                || (details.Kind == InstallerKind.Msi) != InstallerFile.IsMsi(entry.File ?? "")
                || (details.InstallArguments ?? InstallerKinds.InstallArguments(details.Kind)) is null)
            {
                Logger.Warn($"Skipping an invalid entry ({entry?.Id ?? "null"}) in the package feed index {indexUrl}.");
                continue;
            }
            items.Add(new WinFeedItem(
                entry.Id.Trim(),
                string.IsNullOrWhiteSpace(entry.Name) ? entry.Id.Trim() : entry.Name.Trim(),
                entry.Version.Trim(),
                file,
                entry.Sha256.ToLowerInvariant(),
                entry.Size,
                InstallerFile.Architectures(entry.Architectures ?? []),
                FeedLocation.ResolveWithin(indexUrl, entry.Icon),
                details with { InstallArguments = details.InstallArguments ?? InstallerKinds.InstallArguments(details.Kind) }));
        }
        return items;
    }
}
