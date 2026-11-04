using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageOperations;

namespace UniGetUI.PackageEngine.Operations;

public class DownloadOperation : AbstractOperation
{
    private readonly IPackage _package;
    public IPackage Package => _package;
    private string downloadLocation;
    public string DownloadLocation => downloadLocation;

    public DownloadOperation(IPackage package, string downloadPath)
        : base(true, null)
    {
        downloadLocation = downloadPath;
        _package = package;

        Metadata.OperationInformation =
            "Downloading installer for Package=" + _package.Id + " with Manager=" + _package.Manager.Name;
        Metadata.Title = CoreTools.Translate(
            "{package} installer download",
            new Dictionary<string, object?> { { "package", _package.Name } }
        );
        Metadata.Status = CoreTools.Translate("{0} installer is being downloaded", _package.Name);
        Metadata.SuccessTitle = CoreTools.Translate("Download succeeded");
        Metadata.SuccessMessage = CoreTools.Translate(
            "{package} installer was downloaded successfully",
            new Dictionary<string, object?> { { "package", _package.Name } }
        );
        Metadata.FailureTitle = CoreTools.Translate(
            "Download failed",
            new Dictionary<string, object?> { { "package", _package.Name } }
        );
        Metadata.FailureMessage = CoreTools.Translate(
            "{package} installer could not be downloaded",
            new Dictionary<string, object?> { { "package", _package.Name } }
        );
    }

    public override Task<Uri> GetOperationIcon()
    {
        return Task.Run(_package.GetIconUrl);
    }

    public bool SkipHashCheck { get; private set; }
    public bool HashMismatched { get; private set; }
    private string? _rejectedFile;
    private byte[]? _rejectedHash;
    private Uri? _rejectedUrl;
    private string? _rejectedPublishedHash;
    private readonly string _stagingToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4));

    internal const int MaxStagingStemLength = 32;
    internal const int MaxStagingExtensionLength = 16;
    private const long MaxTransportDecodingRatio = 64;

    internal static string StagingPathFor(string destination, string token)
    {
        string stem = Path.GetFileNameWithoutExtension(destination);
        string extension = Path.GetExtension(destination);
        if (stem.Length > MaxStagingStemLength)
            stem = stem[..MaxStagingStemLength];
        if (extension.Length > MaxStagingExtensionLength)
            extension = "";
        return Path.Join(Path.GetDirectoryName(destination), $"{stem}.{token}.partial{extension}");
    }

    protected override void ApplyRetryAction(string retryMode)
    {
        if (retryMode is RetryMode.Retry_SkipIntegrity)
            SkipHashCheck = true;
    }

    /// <summary>
    /// Creates the HTTP client for the download. Virtual so tests can serve an
    /// in-memory payload without standing up a loopback server.
    /// </summary>
    protected virtual HttpClient CreateHttpClient() => new(CreateDownloadHandler());

    internal static HttpClientHandler CreateDownloadHandler()
    {
        HttpClientHandler handler = CoreTools.GenericHttpClientParameters;
        handler.AutomaticDecompression = DecompressionMethods.None;
        return handler;
    }

    protected virtual SignatureCheck? CheckSignature(string path) =>
        OperatingSystem.IsWindows()
            ? AuthenticodeSignature.VerifyFile(path, checkRevocationOnline: false)
            : null;

    internal TimeSpan SignatureCheckTimeout { get; set; } = TimeSpan.FromSeconds(15);

    private async Task<SignatureCheck?> ReportSignatureAsync(string path)
    {
        try
        {
            SignatureCheck? check = await Task.Run(() => CheckSignature(path))
                .WaitAsync(SignatureCheckTimeout, CancellationToken);
            if (check is { } signature)
                Line($"The installer is {signature.Describe()}", LineType.Information);
            return check;
        }
        catch (TimeoutException)
        {
            Line(
                $"The signature of {path} could not be checked within {SignatureCheckTimeout.TotalSeconds:0} seconds",
                LineType.Information
            );
            return null;
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Line($"The signature of {path} could not be read: {ex.Message}", LineType.Information);
            return null;
        }
    }

    private static void RecordVerification(
        Uri installerUrl,
        string? publishedHash,
        InstallerHashStatus status,
        string? algorithm,
        SignatureCheck? signature
    ) =>
        InstallerVerification.Record(
            installerUrl,
            publishedHash,
            new InstallerVerification(status, algorithm, signature)
        );

    internal static bool ComesFromTheSourceFolder(Uri installerUrl, IManagerSource source)
    {
        if (source.Url is not { IsAbsoluteUri: true, IsFile: true } sourceUrl)
            return false;

        try
        {
            string root = Path.GetFullPath(sourceUrl.LocalPath);
            if (!root.EndsWith(Path.DirectorySeparatorChar))
                root += Path.DirectorySeparatorChar;

            return Path.GetFullPath(installerUrl.LocalPath)
                .StartsWith(
                    root,
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal
                );
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<bool> AlreadyDownloaded(string path, PublishedInstallerHash expectedHash)
    {
        try
        {
            return await expectedHash.MatchesAsync(path, CancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Line(
                $"The existing file {path} could not be checked against the published hash: {ex.Message}",
                LineType.Information
            );
            return false;
        }
    }

    internal static bool IsSameFile(string source, string destination)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(source),
                Path.GetFullPath(destination),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal
            );
        }
        catch (Exception)
        {
            return false;
        }
    }

    protected override async Task<OperationVeredict> PerformOperation()
    {
        bool partialFileCreated = false;
        string partialLocation = "";
        HashMismatched = false;
        try
        {
            CancellationToken.ThrowIfCancellationRequested();
            ReportProgress(OperationProgress.ForStage(OperationProgressStage.Downloading));
            Line(
                $"Fetching download url for package {_package.Name} from {_package.Manager.DisplayName}...",
                LineType.Information
            );
            await _package.Details.Load();
            CancellationToken.ThrowIfCancellationRequested();
            Uri? downloadUrl = _package.Details.InstallerUrl;
            string? publishedHash = _package.Details.InstallerHash;
            if (downloadUrl is null)
            {
                Line(
                    $"UniGetUI was not able to find any installer for this package. "
                        + "Please check that this package has an applicable installer and try again later",
                    LineType.Error
                );
                return OperationVeredict.Failure;
            }

            if (!downloadUrl.IsAbsoluteUri)
            {
                Line(
                    $"The installer address \"{downloadUrl}\" published for this package is not a complete address",
                    LineType.Error
                );
                return OperationVeredict.Failure;
            }

            if (Directory.Exists(downloadLocation))
            {
                string? fileName = await _package.GetInstallerFileName();
                CancellationToken.ThrowIfCancellationRequested();
                if (fileName is null)
                {
                    Line(
                        "An error occurred while retrieving file name, default will be used!",
                        LineType.Error
                    );
                    fileName = CoreTools.MakeValidFileName(_package.Name);
                }
                downloadLocation = Path.Join(downloadLocation, fileName);
            }

            if (downloadUrl.IsFile && !ComesFromTheSourceFolder(downloadUrl, _package.Source))
            {
                Line(
                    $"The installer address {downloadUrl} points at the file system, but the "
                        + $"source {_package.Source.Name} is not a local folder holding it. "
                        + "Refusing to read it",
                    LineType.Error
                );
                return OperationVeredict.Failure;
            }

            if (downloadUrl.IsFile && IsSameFile(downloadUrl.LocalPath, downloadLocation))
            {
                Line(
                    $"The chosen location {downloadLocation} is the package file itself, "
                        + "please choose a different destination",
                    LineType.Error
                );
                return OperationVeredict.Failure;
            }

            bool hashPublished = PublishedInstallerHash.TryParse(
                publishedHash,
                out PublishedInstallerHash expectedHash
            );

            if (File.Exists(downloadLocation) && hashPublished)
            {
                Line(
                    $"A file already exists at {downloadLocation}, checking whether it is this installer...",
                    LineType.Information
                );
                if (await AlreadyDownloaded(downloadLocation, expectedHash))
                {
                    long existingSize = new FileInfo(downloadLocation).Length;
                    ReportProgress(
                        OperationProgress.FromDownload((ulong)existingSize, (ulong)existingSize)
                    );
                    Line(
                        $"The file {downloadLocation} matches the {expectedHash.AlgorithmName} hash "
                            + "published for this installer, the download was skipped",
                        LineType.Information
                    );
                    RecordVerification(
                        downloadUrl,
                        publishedHash,
                        InstallerHashStatus.Verified,
                        expectedHash.AlgorithmName,
                        await ReportSignatureAsync(downloadLocation)
                    );
                    return OperationVeredict.Success;
                }

                Line(
                    $"The file {downloadLocation} does not match the {expectedHash.AlgorithmName} hash "
                        + "published for this installer, it will be downloaded again",
                    LineType.Information
                );
            }

            partialLocation = StagingPathFor(downloadLocation, _stagingToken);
            byte[]? actualHash = hashPublished && SkipHashCheck
                ? await ReuseRejectedFileAsync(partialLocation, expectedHash, downloadUrl, publishedHash)
                : null;
            if (actualHash is not null)
                partialFileCreated = true;
            else
            {
                (actualHash, string? transportEncoding) = await DownloadToAsync(
                    downloadUrl,
                    partialLocation,
                    hashPublished ? expectedHash : null,
                    () => partialFileCreated = true
                );
                if (transportEncoding is not null && !(hashPublished && expectedHash.Matches(actualHash)))
                {
                    actualHash = await DecodeTransportEncodingAsync(
                        partialLocation,
                        StagingPathFor(downloadLocation, _stagingToken + "d"),
                        transportEncoding,
                        hashPublished ? expectedHash : null
                    ) ?? actualHash;
                }
            }

            CancellationToken.ThrowIfCancellationRequested();

            InstallerHashStatus hashStatus;
            if (hashPublished)
            {
                if (expectedHash.Matches(actualHash))
                {
                    Line(
                        $"The downloaded file matches the {expectedHash.AlgorithmName} hash published for this installer",
                        LineType.Information
                    );
                    hashStatus = InstallerHashStatus.Verified;
                }
                else if (SkipHashCheck)
                {
                    Line(
                        $"The downloaded file does not match the {expectedHash.AlgorithmName} hash published "
                            + $"for this installer (expected {Convert.ToHexStringLower(expectedHash.Digest)}, "
                            + $"got {Convert.ToHexStringLower(actualHash)}). It was kept because the hash check "
                            + "was skipped",
                        LineType.Error
                    );
                    hashStatus = InstallerHashStatus.MismatchKept;
                }
                else
                {
                    Line(
                        $"The downloaded file does not match the {expectedHash.AlgorithmName} hash published "
                            + $"for this installer (expected {Convert.ToHexStringLower(expectedHash.Digest)}, "
                            + $"got {Convert.ToHexStringLower(actualHash)}). It was set aside as {partialLocation}. "
                            + "If the publisher replaced the installer without updating its manifest, "
                            + "retry skipping integrity checks to keep it anyway",
                        LineType.Error
                    );
                    HashMismatched = true;
                    _rejectedFile = partialLocation;
                    _rejectedHash = actualHash;
                    _rejectedUrl = downloadUrl;
                    _rejectedPublishedHash = publishedHash;
                    partialFileCreated = false;
                    RecordVerification(
                        downloadUrl,
                        publishedHash,
                        InstallerHashStatus.Mismatch,
                        expectedHash.AlgorithmName,
                        null
                    );
                    return OperationVeredict.Failure;
                }
            }
            else if (string.IsNullOrWhiteSpace(publishedHash))
            {
                Line(
                    "No hash is published for this installer, so the download could not be verified",
                    LineType.Information
                );
                hashStatus = InstallerHashStatus.NotPublished;
            }
            else
            {
                Line(
                    $"The hash published for this installer ({publishedHash}) is not in a "
                        + "format UniGetUI can check, so the download could not be verified",
                    LineType.Information
                );
                hashStatus = InstallerHashStatus.Unreadable;
            }

            SignatureCheck? signature = await ReportSignatureAsync(partialLocation);
            CancellationToken.ThrowIfCancellationRequested();
            File.Move(partialLocation, downloadLocation, overwrite: true);
            Line($"The file was saved to {downloadLocation}", LineType.Information);
            RecordVerification(
                downloadUrl,
                publishedHash,
                hashStatus,
                hashPublished ? expectedHash.AlgorithmName : null,
                signature
            );
            return OperationVeredict.Success;
        }
        catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
        {
            Line("User has canceled the operation", LineType.Error);
            return OperationVeredict.Canceled;
        }
        catch (Exception ex)
        {
            Line($"{ex.GetType()}: {ex.Message}", LineType.Error);
            Line($"{ex.StackTrace}", LineType.Error);
            return OperationVeredict.Failure;
        }
        finally
        {
            if (partialFileCreated && File.Exists(partialLocation))
                TryDelete(partialLocation);
        }
    }

    private async Task<byte[]?> ReuseRejectedFileAsync(
        string partialLocation,
        PublishedInstallerHash expectedHash,
        Uri downloadUrl,
        string? publishedHash
    )
    {
        if (_rejectedFile != partialLocation || _rejectedHash is not { } rejectedHash || !File.Exists(partialLocation))
        {
            ForgetRejectedFile();
            return null;
        }

        if (_rejectedUrl != downloadUrl || _rejectedPublishedHash != publishedHash)
        {
            Line(
                "The installer to download changed since the rejected attempt, it will be downloaded again",
                LineType.Information
            );
            ForgetRejectedFile();
            return null;
        }

        byte[] currentHash = await expectedHash.ComputeAsync(partialLocation, CancellationToken);
        ForgetRejectedFile();
        if (!CryptographicOperations.FixedTimeEquals(currentHash, rejectedHash))
        {
            Line(
                $"The file set aside as {partialLocation} changed since it was downloaded, it will be downloaded again",
                LineType.Information
            );
            return null;
        }

        long size = new FileInfo(partialLocation).Length;
        ReportProgress(OperationProgress.FromDownload((ulong)size, (ulong)size));
        Line($"Reusing the installer set aside as {partialLocation} by the previous attempt", LineType.Information);
        return currentHash;
    }

    private void ForgetRejectedFile()
    {
        _rejectedFile = null;
        _rejectedHash = null;
        _rejectedUrl = null;
        _rejectedPublishedHash = null;
    }

    private async Task<(byte[] Hash, string? TransportEncoding)> DownloadToAsync(
        Uri downloadUrl,
        string partialLocation,
        PublishedInstallerHash? expectedHash,
        Action onFileCreated
    )
    {
        Line($"Download URL found at {downloadUrl} ", LineType.Information);
        if (File.Exists(partialLocation))
            File.Delete(partialLocation);
        using HttpClient? httpClient = downloadUrl.IsFile ? null : CreateHttpClient();
        using HttpRequestMessage? request = httpClient is null ? null : new(HttpMethod.Get, downloadUrl);
        request?.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        using HttpResponseMessage? response =
            httpClient is null
                ? null
                : await httpClient.SendAsync(
                    request!,
                    HttpCompletionOption.ResponseHeadersRead,
                    CancellationToken
                );
        response?.EnsureSuccessStatusCode();
        string[] codings = response?.Content.Headers.ContentEncoding
            .Where(e => !e.Equals("identity", StringComparison.OrdinalIgnoreCase))
            .ToArray() ?? [];
        string? transportEncoding = codings.Length == 0 ? null : string.Join(", ", codings);

        long totalBytes;
        Stream sourceStream;
        if (response is null)
        {
            FileInfo sourceFile = new(downloadUrl.LocalPath);
            totalBytes = sourceFile.Length;
            sourceStream = sourceFile.OpenRead();
        }
        else
        {
            totalBytes = response.Content.Headers.ContentLength ?? -1L;
            sourceStream = await response.Content.ReadAsStreamAsync(CancellationToken);
        }

        var canReportProgress = totalBytes > 0;
        using IncrementalHash? hasher = expectedHash?.CreateHasher();
        await using (var contentStream = sourceStream)
        await using (var fileStream = new FileStream(
            partialLocation,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            8192,
            useAsync: true
        ))
        {
            onFileCreated();
            var buffer = new byte[4 * 1024 * 1024];
            long totalRead = 0;
            int oldProgress = -1;
            int bytesRead;
            while (
                (bytesRead = await contentStream.ReadAsync(buffer.AsMemory(), CancellationToken)) > 0
            )
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), CancellationToken);
                hasher?.AppendData(buffer, 0, bytesRead);
                totalRead += bytesRead;

                if (canReportProgress)
                {
                    var progress = (int)((totalRead * 100L) / totalBytes);
                    if (progress != oldProgress)
                    {
                        oldProgress = progress;
                        ReportProgress(
                            OperationProgress.FromDownload(
                                (ulong)totalRead,
                                (ulong)totalBytes
                            )
                        );
                        Line(
                            CoreTools.TextProgressGenerator(
                                30,
                                progress,
                                $"{CoreTools.FormatAsSize(totalRead)}/{CoreTools.FormatAsSize(totalBytes)}"
                            ),
                            LineType.ProgressIndicator
                        );
                    }
                }
            }
        }

        return (hasher?.GetHashAndReset() ?? [], transportEncoding);
    }

    private async Task<byte[]?> DecodeTransportEncodingAsync(
        string partialLocation,
        string decodedLocation,
        string transportEncoding,
        PublishedInstallerHash? expectedHash
    )
    {
        try
        {
            long limit = Math.Max(new FileInfo(partialLocation).Length, 1) * MaxTransportDecodingRatio;
            using IncrementalHash? hasher = expectedHash?.CreateHasher();
            await using (var source = new FileStream(partialLocation, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true))
            await using (Stream decoder = transportEncoding.ToLowerInvariant() switch
            {
                "gzip" or "x-gzip" => new GZipStream(source, CompressionMode.Decompress),
                "deflate" => new ZLibStream(source, CompressionMode.Decompress),
                "br" => new BrotliStream(source, CompressionMode.Decompress),
                _ => throw new NotSupportedException($"Content-Encoding \"{transportEncoding}\" is not supported"),
            })
            await using (var target = new FileStream(decodedLocation, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, useAsync: true))
            {
                var buffer = new byte[1024 * 1024];
                long written = 0;
                int read;
                while ((read = await decoder.ReadAsync(buffer.AsMemory(), CancellationToken)) > 0)
                {
                    written += read;
                    if (written > limit)
                        throw new InvalidDataException("the decoded file is implausibly larger than the transfer");
                    await target.WriteAsync(buffer.AsMemory(0, read), CancellationToken);
                    hasher?.AppendData(buffer, 0, read);
                }
            }

            byte[] decodedHash = hasher?.GetHashAndReset() ?? [];
            File.Move(decodedLocation, partialLocation, overwrite: true);
            Line(
                $"The server compressed the transfer (Content-Encoding: {transportEncoding}), the file was decoded",
                LineType.Information
            );
            return decodedHash;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Line(
                $"The server compressed the transfer (Content-Encoding: {transportEncoding}), but it could not be "
                    + $"decoded: {ex.Message}. The file was kept as received",
                LineType.Information
            );
            return null;
        }
        finally
        {
            if (File.Exists(decodedLocation))
                TryDelete(decodedLocation);
        }
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && _rejectedFile is { } rejectedFile)
        {
            _rejectedFile = null;
            try
            {
                File.Delete(rejectedFile);
            }
            catch (Exception)
            {
            }
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Line($"The temporary file {path} could not be removed: {ex.Message}", LineType.Information);
        }
    }
}
