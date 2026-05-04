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
    public string DownloadLocation
    {
        get => downloadLocation;
    }
    private bool canceled;

    public DownloadOperation(IPackage package, string downloadPath)
        : base(true, null)
    {
        downloadLocation = downloadPath;
        _package = package;

        Metadata.OperationInformation =
            "Downloading installer for Package="
            + _package.Id
            + " with Manager="
            + _package.Manager.Name;
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

        CancelRequested += (_, _) =>
        {
            canceled = true;
        };
    }

    public override Task<Uri> GetOperationIcon()
    {
        return Task.Run(_package.GetIconUrl);
    }

    protected override void ApplyRetryAction(string retryMode)
    {
        // Do nothing
    }

    protected override async Task<OperationVeredict> PerformOperation()
    {
        canceled = false;
        try
        {
            Line(
                $"Fetching download url for package {_package.Name} from {_package.Manager.DisplayName}...",
                LineType.Information
            );
            await _package.Details.Load();
            Uri? downloadUrl = _package.Details.InstallerUrl;
            if (downloadUrl is null)
            {
                Line(
                    $"UniGetUI was not able to find any installer for this package. "
                        + $"Please check that this package has an applicable installer and try again later",
                    LineType.Error
                );
                return OperationVeredict.Failure;
            }

            if (Directory.Exists(downloadLocation))
            {
                string? fileName = await _package.GetInstallerFileName();
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

            Line($"Download URL found at {downloadUrl} ", LineType.Information);
            using var httpClient = new HttpClient(CoreTools.GenericHttpClientParameters);
            using var response = await httpClient.GetAsync(
                downloadUrl,
                HttpCompletionOption.ResponseHeadersRead
            );

            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            var canReportProgress = totalBytes > 0;

            using var contentStream = await response.Content.ReadAsStreamAsync();
            using var fileStream = new FileStream(
                downloadLocation,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                8192,
                true
            );

            var buffer = new byte[4 * 1024 * 1024];
            long totalRead = 0;
            int bytesRead;

            int oldProgress = -1;
            while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead));
                totalRead += bytesRead;

                if (canReportProgress)
                {
                    var progress = (int)((totalRead * 100L) / totalBytes);
                    if (progress != oldProgress)
                    {
                        oldProgress = progress;
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

                if (canceled)
                {
                    fileStream.Close();
                    File.Delete(downloadLocation);
                    Line("User has canceled the operation", LineType.Error);
                    return OperationVeredict.Canceled;
                }
            }

            Line($"The file was saved to {downloadLocation}", LineType.Information);
            return OperationVeredict.Success;
        }
        catch (Exception ex)
        {
            Line($"{ex.GetType()}: {ex.Message}", LineType.Error);
            Line($"{ex.StackTrace}", LineType.Error);
            return OperationVeredict.Failure;
        }
    }
}
