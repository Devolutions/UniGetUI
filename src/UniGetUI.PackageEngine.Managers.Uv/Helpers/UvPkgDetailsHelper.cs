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

        PyPIPackageMetadata? metadata = PyPIProjectClient.ParseMetadata(json);
        if (metadata is { } meta)
        {
            details.Version = meta.Version;
            details.Description = meta.Summary;
            details.Author = meta.Author;
            details.Publisher = meta.Maintainer;
            details.License = meta.License;
            details.HomepageUrl = meta.Homepage;
            details.ManifestUrl = meta.PackageUrl;

            List<string> tags = [];
            foreach (string line in meta.Classifiers)
            {
                if (line.Contains("License ::"))
                {
                    details.License = line.Split("::")[^1].Trim();
                }
                else if (line.Contains("Topic ::"))
                {
                    string tag = line.Split("::")[^1].Trim();
                    if (!tags.Contains(tag))
                        tags.Add(tag);
                }
            }
            details.Tags = [.. tags];

            details.Dependencies.Clear();
            foreach (string rawDependency in meta.RequiresDist)
            {
                string line = rawDependency.Split(';')[0];
                string name = line.Split(['>', '<', '=', '!', '~'])[0];
                details.Dependencies.Add(
                    new()
                    {
                        Name = name,
                        Version = line[name.Length..],
                        Mandatory = true,
                    }
                );
            }
        }

        PyPIInstallerInfo? installer = PyPIProjectClient.ParseInstaller(json);
        if (installer is { } file)
        {
            details.InstallerHash = file.InstallerHash;
            details.InstallerType = file.InstallerType;
            details.InstallerUrl = file.InstallerUrl;
            details.InstallerSize = CoreTools.GetFileSizeAsLong(file.InstallerUrl);
            details.UpdateDate = file.UploadTime;
        }
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

    protected override IReadOnlyList<string> GetInstallableVersions_UnSafe(IPackage package)
    {
        return PyPIProjectClient.ParseVersions(PyPIProjectClient.FetchJson(package.Id));
    }
}
