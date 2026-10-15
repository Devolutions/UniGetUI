using UniGetUI.Avalonia.Views;
using UniGetUI.Core.SettingsEngine;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class NavigationPageOrder
{
    public static PageType[] DefaultPages(bool catalogEnabled) => catalogEnabled
        ? [PageType.Catalog, PageType.Discover, PageType.Updates, PageType.Installed, PageType.Bundles, PageType.CatalogEditor]
        : [PageType.Discover, PageType.Updates, PageType.Installed, PageType.Bundles];

    public static PageType[] Get(bool catalogEnabled)
        => Resolve(Settings.GetValue(Settings.K.NavigationPageOrder), catalogEnabled);

    public static void Save(IReadOnlyList<PageType> visiblePages)
        => Settings.SetValue(
            Settings.K.NavigationPageOrder,
            Serialize(Merge(Get(catalogEnabled: true), visiblePages)));

    public static void Reset() => Settings.SetValue(Settings.K.NavigationPageOrder, "");

    internal static PageType[] Resolve(string stored, bool catalogEnabled)
    {
        PageType[] defaults = DefaultPages(catalogEnabled);
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
        => fullOrder.SequenceEqual(DefaultPages(catalogEnabled: true)) ? "" : string.Join(',', fullOrder);
}
