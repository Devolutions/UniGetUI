using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
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

    // ─── Loading indicators ───────────────────────────────────────────────────
    [ObservableProperty]
    private bool _discoverIsLoading;

    [ObservableProperty]
    private bool _updatesIsLoading;

    [ObservableProperty]
    private bool _installedIsLoading;

    // ─── Pane open/closed ─────────────────────────────────────────────────────
    // NavMenuMode: Automatic mirrors WinUI (dock ≥1600px, else rail+overlay), Docked = always
    // inline, Overlay = always rail+overlay. IsPaneOpen = labeled pane showing, in any mode.
    public enum NavMode { Automatic, Docked, Overlay }

    private const double ExpandedThreshold = 1600; // WinUI ExpandedModeThresholdWidth
    private const double CompactThreshold = 800;    // WinUI CompactModeThresholdWidth

    // Live window width, pushed by MainWindow's bounds observer.
    [ObservableProperty]
    private double _windowWidth = 1450;

    [ObservableProperty]
    private NavMode _mode = ParseMode(Settings.GetValue(Settings.K.NavMenuMode));

    [ObservableProperty]
    private bool _isPaneOpen;

    public static NavMode ParseMode(string value) => value switch
    {
        "docked" => NavMode.Docked,
        "overlay" => NavMode.Overlay,
        _ => NavMode.Automatic,
    };

    // Whether the pane docks inline (vs. the rail + sliding overlay).
    public bool Docked => Mode switch
    {
        NavMode.Docked => true,
        NavMode.Overlay => false,
        _ => WindowWidth >= ExpandedThreshold,
    };

    private bool RailAllowed => WindowWidth >= CompactThreshold;

    // Icon rail vs docked labeled pane vs sliding overlay — mutually exclusive.
    public bool NavDockVisible => Docked && IsPaneOpen;
    public bool RailVisible => (Docked && !IsPaneOpen) || (!Docked && RailAllowed);
    public bool OverlayActive => !Docked && IsPaneOpen;

    private bool _wasDocked;

    public SidebarViewModel()
    {
        _wasDocked = Docked;
        _isPaneOpen = Docked && !Settings.Get(Settings.K.CollapseNavMenuOnWideScreen);
    }

    partial void OnWindowWidthChanged(double value) => ReconcileDock();

    partial void OnModeChanged(NavMode value) => ReconcileDock();

    // On a docked-state flip, open the pane by default (unless collapsed on a wide screen), or
    // collapse when leaving docked mode. Mirrors WinUI's startup IsPaneOpen logic.
    private void ReconcileDock()
    {
        bool docked = Docked;
        if (docked != _wasDocked)
        {
            _wasDocked = docked;
            IsPaneOpen = docked && !Settings.Get(Settings.K.CollapseNavMenuOnWideScreen);
        }
        RaiseLayoutChanged();
    }

    partial void OnIsPaneOpenChanged(bool value)
    {
        // Persist the collapse choice only while docked (WinUI persisted only at ≥1600px).
        if (Docked)
            Settings.Set(Settings.K.CollapseNavMenuOnWideScreen, !value);
        RaiseLayoutChanged();
    }

    private void RaiseLayoutChanged()
    {
        OnPropertyChanged(nameof(NavDockVisible));
        OnPropertyChanged(nameof(RailVisible));
        OnPropertyChanged(nameof(OverlayActive));
    }

    // ─── Selected page ────────────────────────────────────────────────────────
    [ObservableProperty]
    private PageType _selectedPageType = PageType.Null;

    // ─── Navigation ──────────────────────────────────────────────────────────
    public event EventHandler<PageType>? NavigationRequested;

    public string VersionLabel { get; } =
        CoreTools.Translate("UniGetUI Version {0} by Devolutions", CoreData.VersionName);

    [RelayCommand]
    public void RequestNavigation(string? pageName)
    {
        if (Enum.TryParse<PageType>(pageName, out var page))
            NavigationRequested?.Invoke(this, page);
    }

    [RelayCommand]
    private static Task CheckForUpdates() =>
        AvaloniaAutoUpdater.CheckAndInstallUpdatesAsync(autoLaunch: false, manualCheck: true);

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
