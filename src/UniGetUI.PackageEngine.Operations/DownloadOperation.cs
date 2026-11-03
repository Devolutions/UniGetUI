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

    protected override void ApplyRetryAction(string retryMode) { }

    /// <summary>
    /// Creates the HTTP client for the download. Virtual so tests can serve an
    /// in-memory payload without standing up a loopback server.
    /// </summary>
    protected virtual HttpClient CreateHttpClient() =>
        new(CoreTools.GenericHttpClientParameters);

    protected virtual SignatureCheck? CheckSignature(string path) =>
        OperatingSystem.IsWindows() ? AuthenticodeSignature.VerifyFile(path) : null;

    private async Task<SignatureCheck?> ReportSignatureAsync(string path)
    {
        try
        {
            SignatureCheck? check = await Task.Run(() => CheckSignature(path));
            if (check is { } signature)
                Line($"The installer is {signature.Describe()}", LineType.Information);
            return check;
        }
        catch (Exception ex)
        {
            Line($"The signature of {path} could not be read: {ex.Message}", LineType.Information);
            return null;
        }
    }

    private void RecordVerification(InstallerHashStatus status, string? algorithm, SignatureCheck? signature)
    {
        if (_package.Details.InstallerUrl is { } installerUrl)
            InstallerVerification.Record(
                installerUrl,
                _package.Details.InstallerHash,
                new InstallerVerification(status, algorithm, signature)
            );
    }

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
            if (downloadUrl is null)
            {
                Line(
                    $"UniGetUI was not able to find any installer for this package. "
                        + "Please check that this package has an applicable installer and try again later",
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
                _package.Details.InstallerHash,
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

            partialLocation = $"{downloadLocation}.{Guid.NewGuid():N}.partial";
            Line($"Download URL found at {downloadUrl} ", LineType.Information);
            using HttpClient? httpClient = downloadUrl.IsFile ? null : CreateHttpClient();
            using HttpResponseMessage? response =
                httpClient is null
                    ? null
                    : await httpClient.GetAsync(
                        downloadUrl,
                        HttpCompletionOption.ResponseHeadersRead,
                        CancellationToken
                    );
            response?.EnsureSuccessStatusCode();

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
                partialFileCreated = true;
                var buffer = new byte[4 * 1024 * 1024];
                long totalRead = 0;
                int oldProgress = -1;
                int bytesRead;
                while (
                    (bytesRead = await contentStream.ReadAsync(buffer.AsMemory(), CancellationToken)) > 0
                )
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), CancellationToken);
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

            CancellationToken.ThrowIfCancellationRequested();

            InstallerHashStatus hashStatus;
            if (hashPublished)
            {
                byte[] actualHash = await expectedHash.ComputeAsync(partialLocation, CancellationToken);
                if (!expectedHash.Matches(actualHash))
                {
                    Line(
                        $"The downloaded file does not match the {expectedHash.AlgorithmName} hash published "
                            + $"for this installer (expected {Convert.ToHexStringLower(expectedHash.Digest)}, "
                            + $"got {Convert.ToHexStringLower(actualHash)}). The file was discarded",
                        LineType.Error
                    );
                    RecordVerification(InstallerHashStatus.Mismatch, expectedHash.AlgorithmName, null);
                    return OperationVeredict.Failure;
                }

                Line(
                    $"The downloaded file matches the {expectedHash.AlgorithmName} hash published for this installer",
                    LineType.Information
                );
                hashStatus = InstallerHashStatus.Verified;
            }
            else if (string.IsNullOrWhiteSpace(_package.Details.InstallerHash))
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
                    $"The hash published for this installer ({_package.Details.InstallerHash}) is not in a "
                        + "format UniGetUI can check, so the download could not be verified",
                    LineType.Information
                );
                hashStatus = InstallerHashStatus.Unreadable;
            }

            File.Move(partialLocation, downloadLocation, overwrite: true);
            Line($"The file was saved to {downloadLocation}", LineType.Information);
            RecordVerification(
                hashStatus,
                hashPublished ? expectedHash.AlgorithmName : null,
                await ReportSignatureAsync(downloadLocation)
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
