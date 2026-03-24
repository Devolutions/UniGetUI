using CommunityToolkit.Mvvm.ComponentModel;
using UniGetUI.Avalonia.Views;

namespace UniGetUI.Avalonia.ViewModels;

public partial class SidebarViewModel : ViewModelBase
{
    // ─── Badge properties ─────────────────────────────────────────────────────
    [ObservableProperty]
    private int _updatesBadgeCount;

    [ObservableProperty]
    private bool _updatesBadgeVisible;

    [ObservableProperty]
    private bool _bundlesBadgeVisible;

    // When the count changes, sync the badge visibility
    partial void OnUpdatesBadgeCountChanged(int value) =>
        UpdatesBadgeVisible = value > 0;

    // ─── Loading indicators ───────────────────────────────────────────────────
    [ObservableProperty]
    private bool _discoverIsLoading;

    [ObservableProperty]
    private bool _updatesIsLoading;

    [ObservableProperty]
    private bool _installedIsLoading;

    // ─── Selected page ────────────────────────────────────────────────────────
    [ObservableProperty]
    private PageType _selectedPageType = PageType.Null;

    // ─── Navigation ──────────────────────────────────────────────────────────
    public event EventHandler<PageType>? NavigationRequested;

    public void RequestNavigation(PageType page) =>
        NavigationRequested?.Invoke(this, page);

    public void SelectNavButtonForPage(PageType page) =>
        SelectedPageType = page;

    public void SetNavItemLoading(PageType page, bool isLoading)
    {
        switch (page)
        {
            case PageType.Discover: DiscoverIsLoading = isLoading; break;
            case PageType.Updates: UpdatesIsLoading = isLoading; break;
            case PageType.Installed: InstalledIsLoading = isLoading; break;
        }
    }
}
