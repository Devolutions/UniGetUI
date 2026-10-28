using System.Security.Cryptography;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;

namespace UniGetUI.PackageEngine.Managers.InstallerFeeds;

public static class FeedStaging
{
    private static readonly TimeSpan StallTimeout = TimeSpan.FromMinutes(2);

    /// <summary>Copies a folder-feed package, or downloads a web-feed one, to the given path.</summary>
    public static async Task StageAsync(Uri location, long expectedSize, string destination, Action<string> log,
        Action<OperationProgress> progress, CancellationToken cancellationToken)
    {
        progress(OperationProgress.ForStage(OperationProgressStage.Downloading));
        if (location.IsFile)
        {
            log($"Copying {location.LocalPath}...");
            await using var source = File.OpenRead(location.LocalPath);
            await using var target = File.Create(destination);
            await CopyAsync(source, target, source.Length, progress, cancellationToken);
            return;
        }

        log($"Downloading {location}...");
        using var client = new HttpClient(CoreTools.GenericHttpClientParameters);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
        using var response = await client.GetAsync(location, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using (var body = await response.Content.ReadAsStreamAsync(cancellationToken))
        await using (var target = File.Create(destination))
            await CopyAsync(body, target, response.Content.Headers.ContentLength ?? expectedSize, progress, cancellationToken);
    }

    private static async Task CopyAsync(Stream source, Stream target, long total, Action<OperationProgress> progress,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[1024 * 1024];
        long copied = 0;
        using var stalled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (true)
        {
            // A source that stops sending must not hold the install, nor the helper's one install at a time.
            stalled.CancelAfter(StallTimeout);
            int read;
            try { read = await source.ReadAsync(buffer, stalled.Token); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new IOException($"The package stopped arriving for {StallTimeout.TotalSeconds} seconds.");
            }
            if (read == 0)
                break;
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            copied += read;
            progress(OperationProgress.FromDownload((ulong)copied, (ulong)Math.Max(total, 0)));
        }
    }

    /// <summary>Refuses a staged file whose hash differs from the one the feed index announces.</summary>
    public static void VerifyHash(string? expected, string staged)
    {
        if (expected is null)
            return;
        using var stream = File.OpenRead(staged);
        string actual = Convert.ToHexStringLower(SHA256.HashData(stream));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The downloaded package's SHA-256 is {actual}, but the feed index announces {expected}.");
    }
}
