using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageOperations;

namespace UniGetUI.PackageEngine.Operations;

/// <summary>
/// Downloads a WinGet package installer via the configured GitHub accelerator
/// and stores the local path on OverridenOptions.AcceleratedInstallerPath.
/// Runs as a pre-operation so the download does not block the UI thread.
/// </summary>
public class XGetInstallerDownloadOperation : AbstractOperation
{
    private readonly IPackage _package;

    public XGetInstallerDownloadOperation(IPackage package)
        : base(queue_enabled: false)
    {
        _package = package;

        Metadata.OperationInformation =
            "Downloading accelerated installer for Package=" + _package.Id;
        Metadata.Title = CoreTools.Translate(
            "Downloading {package} installer via accelerator",
            new Dictionary<string, object?> { { "package", _package.Name } }
        );
        Metadata.Status = CoreTools.Translate(
            "{0} installer is being downloaded via accelerator",
            _package.Name
        );
        Metadata.SuccessTitle = CoreTools.Translate("Download succeeded");
        Metadata.SuccessMessage = CoreTools.Translate(
            "{package} installer was downloaded successfully",
            new Dictionary<string, object?> { { "package", _package.Name } }
        );
        Metadata.FailureTitle = CoreTools.Translate("Download failed");
        Metadata.FailureMessage = CoreTools.Translate(
            "{package} installer could not be downloaded via accelerator",
            new Dictionary<string, object?> { { "package", _package.Name } }
        );
    }

    public override Task<Uri> GetOperationIcon()
    {
        return Task.Run(_package.GetIconUrl);
    }

    protected override void ApplyRetryAction(string retryMode)
    {
    }

    protected override async Task<OperationVeredict> PerformOperation()
    {
        try
        {
            Line(
                $"Fetching installer details for {_package.Name}...",
                LineType.Information
            );

            await _package.Details.Load().ConfigureAwait(false);

            Uri? installerUrl = _package.Details.InstallerUrl;
            if (installerUrl is null)
            {
                Line(
                    "No installer URL found for this package.",
                    LineType.Error
                );
                return OperationVeredict.Failure;
            }

            Uri? accelerated = CoreTools.AccelerateDownloadUrl(installerUrl);
            if (accelerated is null || accelerated == installerUrl)
            {
                Line(
                    "URL is not a GitHub domain or acceleration not applicable; skipping.",
                    LineType.Information
                );
                return OperationVeredict.Success;
            }

            string? installerType = _package.Details.InstallerType;
            if (string.IsNullOrWhiteSpace(installerType))
                installerType = "exe";

            string safeId = string.Join("_", _package.Id.Split(Path.GetInvalidFileNameChars()));
            string tempDir = Path.Join(Path.GetTempPath(), "UniGetUI", "xget", safeId);
            Directory.CreateDirectory(tempDir);

            string? fileName = await _package.GetInstallerFileName().ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                string ext = installerType switch
                {
                    "msi" => ".msi",
                    "msix" => ".msix",
                    "wix" => ".msi",
                    _ => ".exe",
                };
                fileName = CoreTools.MakeValidFileName(_package.Name) + ext;
            }

            string localPath = Path.Join(tempDir, fileName);

            if (File.Exists(localPath))
            {
                Line($"Using cached installer at {localPath}", LineType.Information);
                _package.OverridenOptions.AcceleratedInstallerPath = localPath;
                _package.OverridenOptions.AcceleratedInstallerType = installerType;
                return OperationVeredict.Success;
            }

            Line($"Downloading from {accelerated}", LineType.Information);

            using var httpClient = new HttpClient(CoreTools.GenericHttpClientParameters);
            httpClient.Timeout = TimeSpan.FromMinutes(10);

            using var response = await httpClient.GetAsync(
                accelerated, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);

            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? -1L;
            var canReportProgress = totalBytes > 0;

            using var contentStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var fileStream = new FileStream(
                localPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

            var buffer = new byte[4 * 1024 * 1024];
            long totalRead = 0;
            int bytesRead;
            int oldProgress = -1;

            while ((bytesRead = await contentStream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead)).ConfigureAwait(false);
                totalRead += bytesRead;

                if (canReportProgress)
                {
                    var progress = (int)((totalRead * 100L) / totalBytes);
                    if (progress != oldProgress)
                    {
                        oldProgress = progress;
                        Line(
                            CoreTools.TextProgressGenerator(
                                30, progress,
                                $"{CoreTools.FormatAsSize(totalRead)}/{CoreTools.FormatAsSize(totalBytes)}"
                            ),
                            LineType.ProgressIndicator
                        );
                    }
                }
            }

            Line($"Saved to {localPath}", LineType.Information);
            _package.OverridenOptions.AcceleratedInstallerPath = localPath;
            _package.OverridenOptions.AcceleratedInstallerType = installerType;
            return OperationVeredict.Success;
        }
        catch (Exception ex)
        {
            Line($"{ex.GetType()}: {ex.Message}", LineType.Error);
            return OperationVeredict.Failure;
        }
    }
}
