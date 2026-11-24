using UniGetUI.Avalonia.Views;
using UniGetUI.Core.SettingsEngine;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class NavigationPageOrder
{
    // Experimental pages are part of the stored order even while hidden, so turning one off and
    // back on puts it back where it was.
    public static PageType[] DefaultPages(
        bool catalogEnabled,
        bool gitRepositoriesEnabled = false,
        bool agentSkillsEnabled = false,
        bool xcodeEnabled = false,
        bool driversEnabled = false) =>
    [
        .. catalogEnabled ? [PageType.Catalog] : Array.Empty<PageType>(),
        PageType.Discover,
        PageType.Updates,
        PageType.Installed,
        PageType.Bundles,
        .. AppExtensions.All.Where(extension => extension.Page switch
        {
            PageType.GitRepositories => gitRepositoriesEnabled,
            PageType.AgentSkills => agentSkillsEnabled,
            PageType.Drivers => driversEnabled,
#if UNIGETUI_XCODE_EXTENSION
            PageType.Xcode => xcodeEnabled,
#endif
            _ => extension.IsEnabled,
        }).Select(extension => extension.Page),
    ];

    private static readonly PageType[] AllPages = DefaultPages(
        catalogEnabled: true,
        gitRepositoriesEnabled: true,
        agentSkillsEnabled: true,
        xcodeEnabled: true,
        driversEnabled: true);

    public static bool IsMovable(PageType page) => AllPages.Contains(page);

    public static PageType[] Get(
        bool catalogEnabled,
        bool gitRepositoriesEnabled = false,
        bool agentSkillsEnabled = false,
        bool xcodeEnabled = false,
        bool driversEnabled = false)
    {
        PageType[] more = StoredMorePages();
        return Resolve(
                Settings.GetValue(Settings.K.NavigationPageOrder),
                catalogEnabled,
                gitRepositoriesEnabled,
                agentSkillsEnabled,
                xcodeEnabled,
                driversEnabled)
            .Where(page => !more.Contains(page))
            .ToArray();
    }

    public static PageType[] GetMore(
        bool catalogEnabled,
        bool gitRepositoriesEnabled = false,
        bool agentSkillsEnabled = false,
        bool xcodeEnabled = false,
        bool driversEnabled = false)
    {
        PageType[] available = DefaultPages(catalogEnabled, gitRepositoriesEnabled, agentSkillsEnabled, xcodeEnabled, driversEnabled);
        return StoredMorePages().Where(available.Contains).ToArray();
    }

    public static void Save(IReadOnlyList<PageType> visiblePages)
        => Settings.SetValue(
            Settings.K.NavigationPageOrder,
            Serialize(Merge(FullOrder(), visiblePages)));

    public static void SaveMore(IReadOnlyList<PageType> visibleMorePages)
        => Settings.SetValue(
            Settings.K.NavigationMorePages,
            string.Join(',', Merge(StoredMorePages(), visibleMorePages)));

    public static void MoveToMore(PageType page, IReadOnlyList<PageType> visibleMorePages, int? index = null)
    {
        if (!IsMovable(page))
            return;

        Settings.SetValue(
            Settings.K.NavigationMorePages,
            string.Join(',', StoredMorePages().Where(stored => stored != page).Append(page)));
        List<PageType> visible = [.. visibleMorePages.Where(shown => shown != page)];
        visible.Insert(Math.Clamp(index ?? visible.Count, 0, visible.Count), page);
        SaveMore(visible);
    }

    public static void MoveToSidebar(PageType page, IReadOnlyList<PageType> visibleSidebarPages, int index)
    {
        if (!IsMovable(page))
            return;

        Settings.SetValue(
            Settings.K.NavigationMorePages,
            string.Join(',', StoredMorePages().Where(stored => stored != page)));

        List<PageType> sidebar = [.. visibleSidebarPages.Where(visible => visible != page)];
        sidebar.Insert(Math.Clamp(index, 0, sidebar.Count), page);
        Save(sidebar);
    }

    public static void PlaceNewlyEnabledExtensions(IEnumerable<PageType> enabledExtensionPages)
    {
        PageType[] enabled = [.. enabledExtensionPages.Where(IsMovable)];
        string stored = Settings.GetValue(Settings.K.NavigationKnownPages);
        if (stored == "")
        {
            Settings.SetValue(Settings.K.NavigationKnownPages, string.Join(',', enabled.Prepend(PageType.Discover)));
            return;
        }

        PageType[] known = ParsePages(stored);
        PageType[] added = [.. enabled.Where(page => !known.Contains(page))];
        if (added.Length == 0)
            return;

        Settings.SetValue(
            Settings.K.NavigationMorePages,
            string.Join(',', StoredMorePages().Concat(added.Where(page => !StoredMorePages().Contains(page)))));
        Settings.SetValue(Settings.K.NavigationKnownPages, string.Join(',', known.Concat(added)));
    }

    public static void Reset()
    {
        Settings.SetValue(Settings.K.NavigationPageOrder, "");
        Settings.SetValue(Settings.K.NavigationMorePages, "");
    }

    private static PageType[] FullOrder()
        => Resolve(Settings.GetValue(Settings.K.NavigationPageOrder), true, true, true, true, true);

    internal static PageType[] StoredMorePages() => ParsePages(Settings.GetValue(Settings.K.NavigationMorePages));

    internal static PageType[] ParsePages(string stored)
        => stored
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(name => Enum.TryParse(name, out PageType page) ? page : PageType.Null)
            .Where(AllPages.Contains)
            .Distinct()
            .ToArray();

    internal static PageType[] Resolve(
        string stored,
        bool catalogEnabled,
        bool gitRepositoriesEnabled = false,
        bool agentSkillsEnabled = false,
        bool xcodeEnabled = false,
        bool driversEnabled = false)
    {
        PageType[] defaults = DefaultPages(catalogEnabled, gitRepositoriesEnabled, agentSkillsEnabled, xcodeEnabled, driversEnabled);
        PageType[] ordered = stored
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(name => Enum.TryParse(name, out PageType page) ? page : PageType.Null)
            .Where(defaults.Contains)
            .Distinct()
            .ToArray();
        return [.. ordered, .. defaults.Where(page => !ordered.Contains(page))];
    }

    internal static PageType[] Merge(PageType[] fullOrder, IReadOnlyList<PageType> visiblePages)
    {
        PageType[] merged = [.. fullOrder];
        int next = 0;
        for (int i = 0; i < merged.Length && next < visiblePages.Count; i++)
        {
            if (visiblePages.Contains(merged[i]))
                merged[i] = visiblePages[next++];
        }
        return merged;
    }

    // An order that is the default one for the pages it holds is stored as unset, so a page added later
    // finds it still following the default.
    internal static string Serialize(PageType[] fullOrder)
        => fullOrder.SequenceEqual(AllPages.Where(fullOrder.Contains)) ? "" : string.Join(',', fullOrder);
}
