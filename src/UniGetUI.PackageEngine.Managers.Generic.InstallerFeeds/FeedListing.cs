using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;

namespace UniGetUI.PackageEngine.Managers.InstallerFeeds;

/// <summary>
/// Lists the packages of a feed. A <c>file:</c> location (a local folder or a mounted share) is
/// scanned directly; an <c>https:</c> location is read through the index.json beside its packages.
/// </summary>
public sealed class FeedListing<TItem>
{
    private const int MaxIndexBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan IndexTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IndexLifetime = TimeSpan.FromMinutes(5);

    // Icon and details lookups list the feed once per package, so a scan is reused briefly instead of
    // walking a network share again for each; a refresh of the manager's indexes forgets it.
    private static readonly TimeSpan FolderLifetime = TimeSpan.FromSeconds(30);

    private readonly record struct CachedListing(DateTime FetchedUtc, IReadOnlyList<TItem> Items);

    private readonly Func<string, CancellationToken, IReadOnlyList<TItem>> _scanFolder;
    private readonly Func<byte[], Uri, IReadOnlyList<TItem>> _parseIndex;
    private readonly string _savedIndexDirectory;
    private readonly ConcurrentDictionary<Uri, CachedListing> _indexes = new();
    private readonly ConcurrentDictionary<string, CachedListing> _folders = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Uri, IReadOnlyList<TItem>> _lastKnown = new();

    public FeedListing(Func<string, CancellationToken, IReadOnlyList<TItem>> scanFolder,
        Func<byte[], Uri, IReadOnlyList<TItem>> parseIndex, string savedIndexDirectory)
    {
        _scanFolder = scanFolder;
        _parseIndex = parseIndex;
        _savedIndexDirectory = savedIndexDirectory;
    }

    public void ForgetCachedIndexes()
    {
        _indexes.Clear();
        _folders.Clear();
    }

    /// <summary>
    /// Lists a feed. When it cannot be reached (off the VPN, share not mounted) the last listing is
    /// reused, so installed packages keep their state; a web feed's last index also survives restarts.
    /// <paramref name="currentOnly"/> refuses that fallback, for what authorizes an install or checks a feed.
    /// </summary>
    public IReadOnlyList<TItem> List(Uri location, CancellationToken cancellationToken = default, bool currentOnly = false)
    {
        try
        {
            var items = location.IsFile ? ScanFolderCached(location.LocalPath, cancellationToken) : ReadIndex(location, cancellationToken);
            _lastKnown[location] = items;
            return items;
        }
        catch (Exception ex) when (!currentOnly && (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested))
        {
            if (_lastKnown.TryGetValue(location, out var known) || TryReadSavedIndex(location, out known))
            {
                Logger.Warn($"The package feed {location} is unreachable ({ex.Message}); using its last known contents.");
                return known;
            }
            throw;
        }
    }

    private IReadOnlyList<TItem> ScanFolderCached(string directory, CancellationToken cancellationToken)
    {
        if (_folders.TryGetValue(directory, out var cached) && DateTime.UtcNow - cached.FetchedUtc < FolderLifetime)
            return cached.Items;
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException($"The package feed folder {directory} does not exist or is not mounted.");
        var items = _scanFolder(directory, cancellationToken);
        _folders[directory] = new CachedListing(DateTime.UtcNow, items);
        return items;
    }

    private string SavedIndexPath(Uri indexUrl) => Path.Combine(CoreData.UniGetUICacheDirectory_Data, _savedIndexDirectory,
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(indexUrl.AbsoluteUri))) + ".json");

    private void SaveIndex(Uri indexUrl, byte[] json)
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

    private bool TryReadSavedIndex(Uri location, out IReadOnlyList<TItem> items)
    {
        items = [];
        if (location.IsFile)
            return false;
        try
        {
            Uri indexUrl = FeedLocation.IndexUrl(location);
            string path = SavedIndexPath(indexUrl);
            if (!File.Exists(path))
                return false;
            items = _parseIndex(File.ReadAllBytes(path), indexUrl);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            return false;
        }
    }

    private IReadOnlyList<TItem> ReadIndex(Uri location, CancellationToken cancellationToken)
    {
        Uri indexUrl = FeedLocation.IndexUrl(location);
        if (_indexes.TryGetValue(indexUrl, out var cached) && DateTime.UtcNow - cached.FetchedUtc < IndexLifetime)
            return cached.Items;

        byte[] json = DownloadIndex(indexUrl, cancellationToken);
        var items = _parseIndex(json, indexUrl);
        _indexes[indexUrl] = new CachedListing(DateTime.UtcNow, items);
        SaveIndex(indexUrl, json);
        return items;
    }

    private static byte[] DownloadIndex(Uri indexUrl, CancellationToken cancellationToken)
    {
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
        return limited.ToArray();
    }
}
