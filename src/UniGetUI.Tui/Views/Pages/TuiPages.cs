using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Consolonia;
using Consolonia.Controls;
using UniGetUI.Core.Data;
using UniGetUI.Core.Language;
using UniGetUI.Core.Tools;
using UniGetUI.Tui.FakeData;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Theme;
using UniGetUI.Tui.Views.Controls;

namespace UniGetUI.Tui.Views.Pages;

/// <summary>Creates the page for each <see cref="TuiPageIds"/> entry.</summary>
internal static class TuiPages
{
    public static Control Build(string pageId) => pageId switch
    {
        TuiPageIds.Discover => new PackageListPage(PackagePageKind.Discover),
        TuiPageIds.Updates => new PackageListPage(PackagePageKind.Updates),
        TuiPageIds.Installed => new PackageListPage(PackagePageKind.Installed),
        TuiPageIds.Bundles => new PackageListPage(PackagePageKind.Bundles),
        TuiPageIds.Operations => new OperationsPage(),
        TuiPageIds.Managers => new ManagersPage(),
        TuiPageIds.Settings => new SettingsPage(),
        TuiPageIds.Logs => new LogsPage(),
        TuiPageIds.History => new HistoryPage(),
        _ => new TextBlock { Text = "Unknown page " + pageId },
    };

    public static string Label(string pageId) => pageId switch
    {
        TuiPageIds.Discover => CoreTools.Translate("Discover"),
        TuiPageIds.Updates => CoreTools.Translate("Updates"),
        TuiPageIds.Installed => CoreTools.Translate("Installed"),
        TuiPageIds.Bundles => CoreTools.Translate("Bundles"),
        TuiPageIds.Operations => CoreTools.Translate("Operations"),
        TuiPageIds.Managers => CoreTools.Translate("Managers"),
        TuiPageIds.Settings => CoreTools.Translate("Settings"),
        TuiPageIds.Logs => CoreTools.Translate("Logs"),
        TuiPageIds.History => CoreTools.Translate("History"),
        TuiPageIds.Help => CoreTools.Translate("Help"),
        _ => CoreTools.Translate("About"),
    };
}
