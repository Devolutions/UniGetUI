#if WINDOWS
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.WingetManager;
using UniGetUI.PackageOperations;

namespace UniGetUI.PackageEngine.Operations;

public class WinGetManifestDownloadOperation : AbstractProcessOperation
{
    private readonly IPackage _package;
    private readonly string _downloadDirectory;
    public IPackage Package => _package;
    public string DownloadLocation => _downloadDirectory;

    public WinGetManifestDownloadOperation(IPackage package, string downloadDirectory)
        : base(true, null)
    {
        _package = package;
        _downloadDirectory = downloadDirectory;

        Metadata.OperationInformation =
            "Downloading installer and manifest for WinGet Package="
            + _package.Id
            + " into "
            + _downloadDirectory;
        Metadata.Title = CoreTools.Translate(
            "{package} installer and manifest download",
            new Dictionary<string, object?> { { "package", _package.Name } }
        );
        Metadata.Status = CoreTools.Translate(
            "{0} installer and manifest are being downloaded",
            _package.Name
        );
        Metadata.SuccessTitle = CoreTools.Translate("Download succeeded");
        Metadata.SuccessMessage = CoreTools.Translate(
            "{package} installer and manifest were downloaded successfully",
            new Dictionary<string, object?> { { "package", _package.Name } }
        );
        Metadata.FailureTitle = CoreTools.Translate(
            "Download failed",
            new Dictionary<string, object?> { { "package", _package.Name } }
        );
        Metadata.FailureMessage = CoreTools.Translate(
            "{package} installer and manifest could not be downloaded",
            new Dictionary<string, object?> { { "package", _package.Name } }
        );
    }

    public override Task<Uri> GetOperationIcon()
    {
        return Task.Run(_package.GetIconUrl);
    }

    protected override void ApplyRetryAction(string retryMode)
    {
        // Do nothing
    }

    protected override void PrepareProcessStartInfo()
    {
        var winget = (WinGet)_package.Manager;
        bool usePinget = winget.SelectedCliToolKind == WinGetCliToolKind.BundledPinget;

        List<string> args = ["download"];

        args.AddRange(WinGetPkgOperationHelper.GetIdNamePiece(_package).Split(" "));

        if (!_package.Source.IsVirtualManager)
        {
            args.AddRange(["--source", _package.Source.Name]);
        }

        args.AddRange(["--download-directory", $"\"{_downloadDirectory}\""]);

        if (!usePinget)
        {
            args.AddRange(
                [
                    "--accept-package-agreements",
                    "--accept-source-agreements",
                    "--disable-interactivity",
                ]
            );

            string proxyArg = WinGet.GetProxyArgument();
            if (proxyArg.Length > 0)
            {
                args.AddRange(proxyArg.Split(' '));
            }
        }

        process.StartInfo.FileName = winget.Status.ExecutablePath;
        process.StartInfo.Arguments =
            winget.Status.ExecutableCallArgs + " " + string.Join(" ", args);
    }

    protected override Task<OperationVeredict> GetProcessVeredict(
        int ReturnCode,
        List<string> Output
    )
    {
        // winget download return codes follow the standard winget code space.
        uint uintCode = (uint)ReturnCode;

        if (uintCode is 0x8A150077 or 0x8A15010C or 0x8A150005)
            return Task.FromResult(OperationVeredict.Canceled);

        return Task.FromResult(
            ReturnCode == 0 ? OperationVeredict.Success : OperationVeredict.Failure
        );
    }
}
#endif
