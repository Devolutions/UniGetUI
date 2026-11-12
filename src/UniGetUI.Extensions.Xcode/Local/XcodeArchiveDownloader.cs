// Portions adapted from xcodes (https://github.com/XcodesOrg/xcodes), MIT License.
// See THIRD_PARTY_NOTICES.md.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UniGetUI.Core.Logging;
using UniGetUI.Extensions.Xcode.AppleAuth;

namespace UniGetUI.Extensions.Xcode.Local;

/// <summary>
/// Downloads an Xcode archive from Apple's developer download host with the signed-in account's cookies. A partial
/// download is resumed when a checksum or strong server validator is available. A finished one is reused, and a
/// checksum from the catalog is verified before the archive is handed on. Nothing it receives is logged.
/// </summary>
internal sealed class XcodeArchiveDownloader(HttpClient http, Func<string> archiveDirectory)
{
    internal const string UnauthorizedMessage =
        "Apple refused the download or returned an unauthorized page. The session, developer access, or agreements "
        + "may need attention. Visit https://developer.apple.com/account or sign in with a different Apple ID, then try again.";

    internal const int AdcSessionExpiredResultCode = 1100;

    private const string UnusableAddress = "This release has no Apple download address UniGetUI can use.";
    private const string RetryAdvice = "Try again; the partial download is reused when it can be verified.";
    private const int BufferSize = 1 << 20;
    private const int MaximumValidatorBytes = 1024;

    /// <summary>How long a download may receive nothing before it is given up as stalled.</summary>
    internal TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Returns the path of the verified archive for <paramref name="download"/>. <paramref name="progress"/> receives
    /// the fraction downloaded, or null while the size is unknown.
    /// </summary>
    public async Task<string> DownloadAsync(Uri download, string? sha1, Action<double?> progress,
        CancellationToken cancellationToken)
    {
        using ArchiveLease archive = await DownloadForInstallAsync(download, sha1, progress, cancellationToken);
        return archive.Path;
    }

    /// <summary>Holds the cache lock until the caller has finished using the downloaded archive.</summary>
    public async Task<ArchiveLease> DownloadForInstallAsync(Uri download, string? sha1, Action<double?> progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (XcodeReleasesCatalog.TryDownload(download.AbsoluteUri) is null)
            throw new XcodesException(UnusableAddress);
        if (sha1 is not null && (sha1.Length != 40 || !sha1.All(char.IsAsciiHexDigit)))
            throw new XcodesException("The Xcode archive checksum is invalid. Refresh the catalog and try again.");
        string name = Path.GetFileName(download.AbsolutePath);
        if (name.Length == 0 || name.StartsWith('.') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new XcodesException(UnusableAddress);

        string directory = CacheDirectory(archiveDirectory(), download, sha1);
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(directory);
        else
            Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        FileStream? cacheLock = LockDownload(directory);
        try
        {
            string path = await DownloadLockedAsync(download, sha1, progress, directory, name, cancellationToken);
            var lease = new ArchiveLease(path, cacheLock);
            cacheLock = null;
            return lease;
        }
        finally
        {
            cacheLock?.Dispose();
        }
    }

    private async Task<string> DownloadLockedAsync(Uri download, string? sha1, Action<double?> progress,
        string directory, string name, CancellationToken cancellationToken)
    {
        string archive = Path.Combine(directory, name);
        string partial = archive + ".part";
        string validator = Path.Combine(directory, "validator");

        if (File.Exists(archive))
        {
            if (new FileInfo(archive).Length > 0 && (sha1 is null || await MatchesAsync(archive, sha1, cancellationToken)))
            {
                Logger.Info($"Xcode: reusing the downloaded archive {name}");
                progress(1);
                return archive;
            }
            Logger.Warn($"Xcode: the downloaded archive {name} is empty or does not match its checksum and is downloaded again");
            File.Delete(archive);
        }

        int? authResultCode = await AuthorizeAsync(cancellationToken);
        await TransferAsync(download, partial, validator, sha1 is not null, progress, restarted: false,
            adcSessionExpired: authResultCode == AdcSessionExpiredResultCode, cancellationToken);

        if (sha1 is not null && !await MatchesAsync(partial, sha1, cancellationToken))
        {
            File.Delete(partial);
            File.Delete(validator);
            throw new XcodesException($"The downloaded archive {name} is damaged: its checksum does not match. Try again.");
        }
        File.Move(partial, archive, overwrite: false);
        File.Delete(validator);
        return archive;
    }

    internal sealed class ArchiveLease(string path, FileStream cacheLock) : IDisposable
    {
        public string Path { get; } = path;

        public void Dispose() => cacheLock.Dispose();
    }

    internal static string CacheDirectory(string root, Uri download, string? sha1 = null)
        => Path.Combine(root, Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{download.AbsoluteUri}\n{sha1?.ToLowerInvariant()}"))));

    private static FileStream LockDownload(string directory)
    {
        try
        {
            return new FileStream(Path.Combine(directory, "lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException ex)
        {
            throw new XcodesException("The Xcode download cache could not be locked. Another Xcode install or download may be using it.", ex);
        }
    }

    /// <summary>
    /// Requests Apple's developer download list so <c>developer.apple.com</c> issues the <c>ADCDownloadAuth</c>
    /// cookie needed by <c>download.developer.apple.com</c>. A refusal is left for the archive transfer to report.
    /// </summary>
    private async Task<int?> AuthorizeAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, AppleDownloadsCatalog.DownloadsUri);
        request.Headers.Accept.ParseAdd("application/json");
        try
        {
            using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                Logger.Warn($"Xcode: Apple answered {(int)response.StatusCode} when authorizing the download");
                return null;
            }

            byte[] data = await AppleAuthClient.ReadBoundedAsync(
                response, cancellationToken, AppleDownloadsCatalog.MaximumResponseBytes);
            try
            {
                if (data.Length == 0)
                    return null;
                AppleDownloadList? list = AppleDownloadsCatalog.DeserializeList(data);
                if (list?.ResultCode is { } code && code != 0)
                    Logger.Warn($"Xcode: Apple's download service returned result {code} when authorizing the download");
                return list?.ResultCode;
            }
            catch (JsonException)
            {
                Logger.Warn("Xcode: Apple's download authorization response was not valid JSON");
                return null;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(data);
            }
        }
        catch (AppleAuthException)
        {
            Logger.Warn("Xcode: Apple's download authorization response was too large");
            return null;
        }
        catch (HttpRequestException ex)
        {
            Logger.Warn($"Xcode: could not authorize the download: {ex.Message}");
            return null;
        }
    }

    private async Task TransferAsync(Uri download, string partial, string validatorPath, bool hasChecksum,
        Action<double?> progress, bool restarted, bool adcSessionExpired, CancellationToken cancellationToken)
    {
        long existing = !restarted && File.Exists(partial) ? new FileInfo(partial).Length : 0;
        EntityTagHeaderValue? validator = existing > 0 ? await ReadValidatorAsync(validatorPath, cancellationToken) : null;
        if (existing > 0 && validator is null && !hasChecksum)
        {
            Logger.Info("Xcode: restarting a partial download without a checksum or strong server validator");
            existing = 0;
        }
        using var request = new HttpRequestMessage(HttpMethod.Get, download);
        request.Headers.Accept.ParseAdd("*/*");
        if (existing > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existing, null);
            if (validator is not null)
                request.Headers.IfRange = new RangeConditionHeaderValue(validator);
        }

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new XcodesException($"Could not download Xcode: {ex.Message}", ex);
        }

        using (response)
        {
            if (existing > 0 && !restarted && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            {
                response.Dispose();
                Logger.Warn("Xcode: Apple rejected the download range; retrying from the beginning");
                await TransferAsync(download, partial, validatorPath, hasChecksum, progress, restarted: true,
                    adcSessionExpired, cancellationToken);
                return;
            }

            ThrowIfRefused(response, adcSessionExpired);
            if (response.StatusCode is not (HttpStatusCode.OK or HttpStatusCode.PartialContent))
                throw new XcodesException(
                    $"Could not download Xcode: Apple answered {(int)response.StatusCode} {response.ReasonPhrase}.");

            bool resumed = response.StatusCode == HttpStatusCode.PartialContent;
            long? total = null;
            if (resumed && !TryGetResumeLength(response, existing, validator, out total))
            {
                if (existing == 0 || restarted)
                    throw new XcodesException("Apple returned an invalid partial download response. Try again.");
                response.Dispose();
                Logger.Warn("Xcode: Apple returned an invalid download range; retrying from the beginning");
                await TransferAsync(download, partial, validatorPath, hasChecksum, progress, restarted: true,
                    adcSessionExpired, cancellationToken);
                return;
            }
            if (!resumed)
            {
                existing = 0;
                total = response.Content.Headers.ContentLength;
                if (total is <= 0)
                    throw new XcodesException("Apple returned an empty Xcode archive. Try again.");
            }

            await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var target = new FileStream(partial, resumed ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, BufferSize, useAsync: true);
            if (!resumed)
                await WriteValidatorAsync(validatorPath, response.Headers.ETag, cancellationToken);

            byte[] buffer = new byte[BufferSize];
            long received = existing;
            progress(Fraction(received, total));
            while (true)
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                stall.CancelAfter(StallTimeout);
                int read;
                try
                {
                    read = await source.ReadAsync(buffer, stall.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new XcodesException($"The download stalled. {RetryAdvice}");
                }
                catch (Exception ex) when (ex is IOException or HttpRequestException)
                {
                    throw new XcodesException($"The download was interrupted: {ex.Message} {RetryAdvice}", ex);
                }
                if (read == 0) break;
                if (total is { } expected && read > expected - received)
                    throw new XcodesException("Apple sent more archive data than expected. Try again.");
                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                received += read;
                progress(Fraction(received, total));
            }

            if (received == 0 || (total is { } declared && received != declared))
                throw new XcodesException($"The download ended early. {RetryAdvice}");
        }
    }

    private static bool TryGetResumeLength(
        HttpResponseMessage response, long existing, EntityTagHeaderValue? validator, out long? total)
    {
        total = null;
        ContentRangeHeaderValue? range = response.Content.Headers.ContentRange;
        if (existing <= 0 || range is not { From: { } from, To: { } to, Length: { } length }
            || !string.Equals(range.Unit, "bytes", StringComparison.OrdinalIgnoreCase)
            || from != existing || length <= existing || to != length - 1
            || (response.Content.Headers.ContentLength is { } count && count != length - existing)
            || (validator is not null && !validator.Equals(response.Headers.ETag)))
            return false;
        total = length;
        return true;
    }

    private static async Task<EntityTagHeaderValue?> ReadValidatorAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024, useAsync: true);
        byte[] buffer = new byte[MaximumValidatorBytes + 1];
        int count = 0;
        while (count < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken);
            if (read == 0)
                break;
            count += read;
        }
        string value = Encoding.UTF8.GetString(buffer, 0, count);
        if (count <= MaximumValidatorBytes && EntityTagHeaderValue.TryParse(value, out EntityTagHeaderValue? validator)
            && !validator.IsWeak && !string.Equals(validator.Tag, "*", StringComparison.Ordinal))
            return validator;
        Logger.Warn("Xcode: the saved download validator is invalid and will not be used");
        return null;
    }

    private static async Task WriteValidatorAsync(
        string path, EntityTagHeaderValue? validator, CancellationToken cancellationToken)
    {
        string? value = validator is { IsWeak: false } && !string.Equals(validator.Tag, "*", StringComparison.Ordinal)
            ? validator.ToString() : null;
        if (value is null || Encoding.UTF8.GetByteCount(value) > MaximumValidatorBytes)
        {
            File.Delete(path);
            return;
        }
        await File.WriteAllTextAsync(path, value, new UTF8Encoding(false), cancellationToken);
    }

    /// <summary>
    /// Apple rarely answers a refused download with 403: it redirects to a page under /unauthorized/ and answers 200.
    /// An archive is never a web page, so one is a refusal too.
    /// </summary>
    internal static void ThrowIfRefused(HttpResponseMessage response, bool adcSessionExpired = false)
    {
        Uri? final = response.RequestMessage?.RequestUri;
        bool unauthorizedPage = final is not null
            && final.AbsolutePath.Contains("/unauthorized", StringComparison.OrdinalIgnoreCase);
        bool page = string.Equals(response.Content.Headers.ContentType?.MediaType, "text/html", StringComparison.OrdinalIgnoreCase);
        if (unauthorizedPage || response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
            || (page && response.IsSuccessStatusCode))
            throw new XcodesException(UnauthorizedMessage)
            {
                IsAccessFailure = true,
                IsAdcSessionExpired = adcSessionExpired,
            };
    }

    private static double? Fraction(long received, long? total)
        => total is > 0 ? Math.Clamp((double)received / total.Value, 0, 1) : null;

    internal static async Task<bool> MatchesAsync(string file, string sha1, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize,
            useAsync: true);
        byte[] hash = await SHA1.HashDataAsync(stream, cancellationToken);
        return string.Equals(Convert.ToHexStringLower(hash), sha1, StringComparison.OrdinalIgnoreCase);
    }
}
