using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class PageTitles
{
    public static string? For(PageType page) => page switch
    {
        PageType.Catalog => CoreTools.Translate("Software Catalog"),
        PageType.Discover => CoreTools.Translate("Discover Packages"),
        PageType.Updates => CoreTools.Translate("Software Updates"),
        PageType.Installed => CoreTools.Translate("Installed Packages"),
        PageType.Bundles => CoreTools.Translate("Package Bundles"),
        PageType.CatalogEditor => CoreTools.Translate("Catalog Editor"),
        PageType.GitRepositories => CoreTools.Translate("Git Repositories"),
        PageType.AgentSkills => CoreTools.Translate("Agent Skills"),
        PageType.Drivers => CoreTools.Translate("Drivers"),
        PageType.Xcode => CoreTools.Translate("Xcode"),
        PageType.Settings => CoreTools.Translate("Settings"),
        PageType.Managers => CoreTools.Translate("Package Managers"),
        PageType.OwnLog => CoreTools.Translate("UniGetUI Log"),
        PageType.ManagerLog => CoreTools.Translate("Package Manager logs"),
        PageType.OperationHistory => CoreTools.Translate("Operation history"),
        PageType.ManagerHealth => CoreTools.Translate("Manager health"),
        PageType.Help => CoreTools.Translate("Help"),
        PageType.ReleaseNotes => CoreTools.Translate("Release notes"),
        PageType.About => CoreTools.Translate("About"),
        PageType.More => CoreTools.Translate("More"),
        _ => null,
    };
}
