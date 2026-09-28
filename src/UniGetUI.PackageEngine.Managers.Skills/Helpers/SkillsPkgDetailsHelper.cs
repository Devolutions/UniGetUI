using System.Globalization;
using Devolutions.AgentSkills;
using UniGetUI.Core.IconEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.BaseProviders;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.Managers.SkillsManager;

internal sealed class SkillsPkgDetailsHelper : BasePkgDetailsHelper
{
    private readonly AgentSkills _skills;

    public SkillsPkgDetailsHelper(AgentSkills manager)
        : base(manager)
    {
        _skills = manager;
    }

    private InstalledSkillInfo? FindInstalled(IPackage package) =>
        _skills
            .Backend.GetInstalledSkills(CancellationToken.None)
            .FirstOrDefault(skill => skill.Name.Equals(package.Id, StringComparison.OrdinalIgnoreCase));

    protected override void GetDetails_UnSafe(IPackageDetails details)
    {
        details.InstallerType = CoreTools.Translate("Agent skill");

        var source = _skills.SourceFactory.GetLocator(details.Package.Source);
        if (source is not null)
        {
            details.HomepageUrl = source.Url;
            details.InstallerUrl = source.Url;
            if (source.GitHubOwner is { } owner)
            {
                details.Publisher = owner;
                details.Author = owner;
            }

            if (_skills.GetCatalogPage(source, details.Package.Id) is { } catalogPage)
                details.ManifestUrl = catalogPage;
        }

        if (FindInstalled(details.Package) is { } installed)
        {
            details.Description = installed.Description;

            // A Notion skill has a page of its own in the database it came from
            if (installed.SourceType == "notion" && Uri.TryCreate(installed.SourceUrl, UriKind.Absolute, out Uri? page))
                details.HomepageUrl = page;
            details.UpdateDate = installed.UpdatedAt?.ToLocalTime().ToString("d", CultureInfo.CurrentCulture);

            // The agents the skill is installed for
            var agents = _skills.Backend.GetAgents().ToDictionary(agent => agent.Id, agent => agent.DisplayName);
            details.Tags = installed.Agents.Select(id => agents.GetValueOrDefault(id, id)).ToArray();
        }
        else if (source is not null && _skills.Listings.Find(source, details.Package.Id) is { } available)
        {
            details.Description = available.Description;
        }
    }

    protected override IReadOnlyList<string> GetInstallableVersions_UnSafe(IPackage package) => [];

    protected override CacheableIcon? GetIcon_UnSafe(IPackage package) =>
        _skills.SourceFactory.GetLocator(package.Source)?.GitHubOwner is { } owner
            ? new CacheableIcon(new Uri($"https://github.com/{owner}.png?size=128"))
            : null;

    protected override IReadOnlyList<Uri> GetScreenshots_UnSafe(IPackage package) => [];

    protected override string? GetInstallLocation_UnSafe(IPackage package) => FindInstalled(package)?.Path;
}
