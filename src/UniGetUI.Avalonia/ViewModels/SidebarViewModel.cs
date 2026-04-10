using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;

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

    partial void OnUpdatesBadgeVisibleChanged(bool _)
    {
        OnPropertyChanged(nameof(UpdatesBadgeExpandedVisible));
        OnPropertyChanged(nameof(UpdatesBadgeCompactVisible));
    }

    partial void OnBundlesBadgeVisibleChanged(bool _)
    {
        OnPropertyChanged(nameof(BundlesBadgeExpandedVisible));
        OnPropertyChanged(nameof(BundlesBadgeCompactVisible));
    }

    // ─── Loading indicators ───────────────────────────────────────────────────
    [ObservableProperty]
    private bool _discoverIsLoading;

    [ObservableProperty]
    private bool _updatesIsLoading;

    [ObservableProperty]
    private bool _installedIsLoading;

    // ─── Pane open/closed ─────────────────────────────────────────────────────
    [ObservableProperty]
    private bool isPaneOpen = !Settings.Get(Settings.K.CollapseNavMenuOnWideScreen);

    partial void OnIsPaneOpenChanged(bool value)
    {
        Settings.Set(Settings.K.CollapseNavMenuOnWideScreen, !value);
        OnPropertyChanged(nameof(PaneWidth));
        OnPropertyChanged(nameof(UpdatesBadgeExpandedVisible));
        OnPropertyChanged(nameof(UpdatesBadgeCompactVisible));
        OnPropertyChanged(nameof(BundlesBadgeExpandedVisible));
        OnPropertyChanged(nameof(BundlesBadgeCompactVisible));
    }

    public double PaneWidth => IsPaneOpen ? 250 : 72;

    public bool UpdatesBadgeExpandedVisible => UpdatesBadgeVisible && IsPaneOpen;
    public bool UpdatesBadgeCompactVisible => UpdatesBadgeVisible && !IsPaneOpen;
    public bool BundlesBadgeExpandedVisible => BundlesBadgeVisible && IsPaneOpen;
    public bool BundlesBadgeCompactVisible => BundlesBadgeVisible && !IsPaneOpen;

    // ─── Selected page ────────────────────────────────────────────────────────
    [ObservableProperty]
    private PageType _selectedPageType = PageType.Null;

    // ─── Navigation ──────────────────────────────────────────────────────────
    public event EventHandler<PageType>? NavigationRequested;

    public string VersionLabel { get; } = CoreTools.Translate("UniGetUI Version {0}", CoreData.VersionName);

    [RelayCommand]
    public void RequestNavigation(string? pageName)
    {
        if (Enum.TryParse<PageType>(pageName, out var page))
            NavigationRequested?.Invoke(this, page);
    }

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
