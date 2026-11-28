using UniGetUI.Core.IconEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.Generic.PyPI;
using UniGetUI.PackageEngine.Managers.UvManager.Models;
using UniGetUI.PackageEngine.Managers.UvManager.Services;

namespace UniGetUI.PackageEngine.Managers.UvManager.Helpers;

internal sealed class UvPkgDetailsHelper : BasePkgDetailsHelper
{
    private readonly Uv _manager;

    public UvPkgDetailsHelper(Uv manager)
        : base(manager)
    {
        _manager = manager;
    }

    protected override void GetDetails_UnSafe(IPackageDetails details)
    {
        string json = PyPIProjectClient.FetchJson(details.Package.Id);

        if (PyPIProjectClient.ParseMetadata(json) is { } metadata)
            PyPIProjectClient.ApplyMetadata(details, metadata);

        if (PyPIProjectClient.ParseInstaller(json) is { } installer)
            PyPIProjectClient.ApplyInstaller(details, installer);
    }

    protected override CacheableIcon? GetIcon_UnSafe(IPackage package)
    {
        throw new NotImplementedException();
    }

    protected override IReadOnlyList<Uri> GetScreenshots_UnSafe(IPackage package)
    {
        throw new NotImplementedException();
    }

    protected override string? GetInstallLocation_UnSafe(IPackage package)
    {
        IReadOnlyList<UvTool> tools = _manager.ListTools(
            "--show-paths",
            LoggableTaskType.LoadPackageDetails
        );

        UvTool? tool = tools.FirstOrDefault(candidate =>
            candidate.PackageId.Equals(package.Id, StringComparison.OrdinalIgnoreCase)
        );

        return tool?.ToolEnvironmentPath;
    }

    protected override IReadOnlyList<string> GetInstallableVersions_UnSafe(IPackage package) =>
        ListVersions(package, includePreReleases: false);

    internal IReadOnlyList<string> GetVersionsIncludingPreReleases(IPackage package) =>
        ListVersions(package, includePreReleases: true);

    private IReadOnlyList<string> ListVersions(IPackage package, bool includePreReleases) =>
        PyPIProjectClient.ParseVersions(PyPIProjectClient.FetchJson(package.Id), includePreReleases);
}
