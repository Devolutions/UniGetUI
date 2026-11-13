using UniGetUI.Core.IconEngine;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.ManagerClasses.Classes;
using UniGetUI.PackageEngine.Managers.PacmanManager;

namespace UniGetUI.PackageEngine.Managers.AurManager;

internal sealed class AurPkgDetailsHelper : BasePkgDetailsHelper
{
    private readonly Aur _aur;

    public AurPkgDetailsHelper(Aur manager)
        : base(manager)
    {
        _aur = manager;
    }

    protected override void GetDetails_UnSafe(IPackageDetails details)
    {
        using var p = _aur.CreateListingProcess(["-Si", "--aur", "--color=never", details.Package.Id]);
        IProcessTaskLogger logger = Manager.TaskLogger.CreateNew(LoggableTaskType.LoadPackageDetails, p);
        p.Start();
        var stderr = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        logger.AddToStdOut(output);
        logger.AddToStdErr(stderr.Result);
        p.WaitForExit();

        AurOutputParser.ParseDetails(output, details);
        logger.Close(p.ExitCode);
    }

    protected override CacheableIcon? GetIcon_UnSafe(IPackage package)
        => throw new NotImplementedException();

    protected override IReadOnlyList<Uri> GetScreenshots_UnSafe(IPackage package)
        => throw new NotImplementedException();

    protected override string? GetInstallLocation_UnSafe(IPackage package)
        => AlpmOutput.FindInstallLocation(_aur.PacmanPath, package.Id);

    protected override IReadOnlyList<string> GetInstallableVersions_UnSafe(IPackage package)
        => throw new InvalidOperationException("The AUR does not support installing arbitrary versions");
}
