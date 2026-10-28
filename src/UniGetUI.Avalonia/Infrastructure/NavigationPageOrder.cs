using UniGetUI.Avalonia.Views;
using UniGetUI.Core.SettingsEngine;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class NavigationPageOrder
{
    // Experimental pages are part of the stored order even while hidden, so turning one off and
    // back on puts it back where it was.
    public static PageType[] DefaultPages(bool catalogEnabled, bool gitRepositoriesEnabled = false, bool agentSkillsEnabled = false) =>
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
            _ => extension.IsEnabled,
        }).Select(extension => extension.Page),
    ];

    private static PageType[] AllPages => DefaultPages(catalogEnabled: true, gitRepositoriesEnabled: true, agentSkillsEnabled: true);

    public static PageType[] Get(bool catalogEnabled, bool gitRepositoriesEnabled = false, bool agentSkillsEnabled = false)
        => Resolve(Settings.GetValue(Settings.K.NavigationPageOrder), catalogEnabled, gitRepositoriesEnabled, agentSkillsEnabled);

    public static void Save(IReadOnlyList<PageType> visiblePages)
        => Settings.SetValue(
            Settings.K.NavigationPageOrder,
            Serialize(Merge(Get(catalogEnabled: true, gitRepositoriesEnabled: true, agentSkillsEnabled: true), visiblePages)));

    public static void Reset() => Settings.SetValue(Settings.K.NavigationPageOrder, "");

    internal static PageType[] Resolve(string stored, bool catalogEnabled, bool gitRepositoriesEnabled = false, bool agentSkillsEnabled = false)
    {
        PageType[] defaults = DefaultPages(catalogEnabled, gitRepositoriesEnabled, agentSkillsEnabled);
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

    internal static string Serialize(PageType[] fullOrder)
        => fullOrder.SequenceEqual(AllPages) ? "" : string.Join(',', fullOrder);
}
