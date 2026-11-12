using UniGetUI.Core.Logging;

namespace UniGetUI.Extensions.Xcode;

internal sealed record XcodeSavedPublicCatalog(byte[] Data, DateTime SavedAtUtc);

internal sealed class XcodePublicCatalogCache(Func<string> cacheFile, string name)
{
    internal async Task<XcodeSavedPublicCatalog?> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        FileStream input;
        try
        {
            input = new FileStream(cacheFile(), FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }

        await using (input.ConfigureAwait(false))
        {
            DateTime savedAtUtc = File.GetLastWriteTimeUtc(input.SafeFileHandle);
            byte[] data = await XcodePublicCatalogDownload.ReadBoundedAsync(
                input, $"saved {name}", cancellationToken).ConfigureAwait(false);
            return new XcodeSavedPublicCatalog(data, savedAtUtc);
        }
    }

    internal async Task SaveAsync(byte[] data, CancellationToken cancellationToken)
    {
        try
        {
            await WriteAsync(cacheFile(), data, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Could not save {name} for offline use: {ex.Message}");
        }
    }

    private async Task WriteAsync(string path, byte[] data, CancellationToken cancellationToken)
    {
        string directory = Path.GetDirectoryName(path)
            ?? throw new IOException($"The {name} cache has no parent directory.");
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".xcode-catalog-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                32 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await output.WriteAsync(data, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn($"Could not remove a temporary {name} cache file: {ex.Message}");
            }
        }
    }
}
