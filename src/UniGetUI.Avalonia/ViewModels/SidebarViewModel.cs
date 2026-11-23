using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Models;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.ViewModels;

public partial class SidebarViewModel : ViewModelBase
{
    public bool SoftwareCatalogEnabled { get; } = Settings.Get(Settings.K.EnableSoftwareCatalog);

    // An administrator tool, opened from the catalog page rather than the sidebar; off unless asked for.
    public bool CatalogEditorEnabled { get; } =
        Settings.Get(Settings.K.EnableSoftwareCatalog) && Settings.Get(Settings.K.EnableCatalogEditor);

    // Only a catalog with packages to offer becomes the startup page; an unset or broken one would
    // open the app on an empty page. Checked once at startup, like the setting itself.
    public bool HasUsableCatalog { get; } =
        Settings.Get(Settings.K.EnableSoftwareCatalog) && SoftwareCatalog.HasUsableCatalog();

    // Turned on and off from Settings > Extensions while the app runs.
    [ObservableProperty]
    private bool _gitRepositoriesEnabled = Settings.Get(Settings.K.EnableGitRepositories);

    partial void OnGitRepositoriesEnabledChanged(bool value)
    {
        RefreshNavigationPages();
        if (value) return;
        GitRepositoriesBadgeCount = 0;
        GitRepositoriesIsLoading = false;
    }

#if UNIGETUI_XCODE_EXTENSION
    // Keep a saved preference without starting the extension on an unsupported system.
    [ObservableProperty]
    private bool _xcodeEnabled = XcodePlatform.IsSupported && Settings.Get(Settings.K.EnableXcode);

    partial void OnXcodeEnabledChanged(bool value)
    {
        RefreshNavigationPages();
        if (value) return;
        XcodeBadgeCount = 0;
        XcodeIsLoading = false;
    }
#endif

    [ObservableProperty]
    private bool _driversEnabled = AppExtensions.Drivers.IsEnabled;

    partial void OnDriversEnabledChanged(bool value)
    {
        RefreshNavigationPages();
        if (value) return;
        DriversBadgeCount = 0;
        DriversIsLoading = false;
    }

    private PageType[] GetNavigationPages() =>
#if UNIGETUI_XCODE_EXTENSION
        NavigationPageOrder.Get(SoftwareCatalogEnabled, GitRepositoriesEnabled, AgentSkillsEnabled, XcodeEnabled, DriversEnabled);
#else
        NavigationPageOrder.Get(SoftwareCatalogEnabled, GitRepositoriesEnabled, AgentSkillsEnabled, driversEnabled: DriversEnabled);
#endif

    private PageType[] GetMorePages() =>
#if UNIGETUI_XCODE_EXTENSION
        NavigationPageOrder.GetMore(SoftwareCatalogEnabled, GitRepositoriesEnabled, AgentSkillsEnabled, XcodeEnabled, DriversEnabled);
#else
        NavigationPageOrder.GetMore(SoftwareCatalogEnabled, GitRepositoriesEnabled, AgentSkillsEnabled, driversEnabled: DriversEnabled);
#endif

    public static readonly HashSet<string> ExtensionEnabledProperties =
    [
        nameof(GitRepositoriesEnabled),
        nameof(AgentSkillsEnabled),
        nameof(DriversEnabled),
#if UNIGETUI_XCODE_EXTENSION
        nameof(XcodeEnabled),
#endif
    ];

    public bool IsExtensionEnabled(PageType page) => EnabledExtensionPages().Contains(page);

    private IEnumerable<PageType> EnabledExtensionPages()
    {
        if (GitRepositoriesEnabled) yield return PageType.GitRepositories;
        if (AgentSkillsEnabled) yield return PageType.AgentSkills;
        if (DriversEnabled) yield return PageType.Drivers;
#if UNIGETUI_XCODE_EXTENSION
        if (XcodeEnabled) yield return PageType.Xcode;
#endif
    }

    private void RefreshNavigationPages()
    {
        NavigationPageOrder.PlaceNewlyEnabledExtensions(EnabledExtensionPages());
        MorePages = GetMorePages();
        NavigationPages = GetNavigationPages();
    }

    [ObservableProperty]
    private bool _agentSkillsEnabled = Settings.Get(Settings.K.EnableAgentSkills);

    partial void OnAgentSkillsEnabledChanged(bool value)
        => RefreshNavigationPages();

    public void SetExtensionEnabled(PageType page, bool enabled)
    {
        if (page == PageType.GitRepositories) GitRepositoriesEnabled = enabled;
        if (page == PageType.AgentSkills) AgentSkillsEnabled = enabled;
        if (page == PageType.Drivers) DriversEnabled = enabled && AppExtensions.Drivers.IsAvailable();
#if UNIGETUI_XCODE_EXTENSION
        if (page == PageType.Xcode) XcodeEnabled = enabled && XcodePlatform.IsSupported;
#endif
    }

    [ObservableProperty]
    private PageType[] _navigationPages;

    [ObservableProperty]
    private PageType[] _morePages;

    public static readonly PageType[] MoreToolPages =
    [
        PageType.OwnLog,
        PageType.ManagerLog,
        PageType.OperationHistory,
        PageType.ManagerHealth,
        PageType.ReleaseNotes,
        PageType.Help,
    ];

    public bool IsReachedFromMore(PageType page)
        => page == PageType.More || MorePages.Contains(page) || MoreToolPages.Contains(page);

    public void ReorderNavigationPages(IReadOnlyList<PageType> pages)
    {
        NavigationPageOrder.Save(pages);
        RefreshNavigationPages();
    }

    public void ReorderMorePages(IReadOnlyList<PageType> pages)
    {
        NavigationPageOrder.SaveMore(pages);
        RefreshNavigationPages();
    }

    public void MovePageToMore(PageType page, int? index = null)
    {
        NavigationPageOrder.MoveToMore(page, MorePages, index);
        RefreshNavigationPages();
    }

    public void PinPageToSidebar(PageType page, int? index = null)
    {
        NavigationPageOrder.MoveToSidebar(page, NavigationPages, index ?? NavigationPages.Length);
        RefreshNavigationPages();
    }

    public void ResetNavigationPages()
    {
        NavigationPageOrder.Reset();
        RefreshNavigationPages();
    }

    // ─── Badge properties ─────────────────────────────────────────────────────
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UpdatesBadgeText))]
    private int _updatesBadgeCount;

    [ObservableProperty]
    private bool _updatesBadgeVisible;

    [ObservableProperty]
    private bool _bundlesBadgeVisible;

    public string UpdatesBadgeText => UpdatesBadgeCount > 99 ? "99+" : UpdatesBadgeCount.ToString();

    // Main clones that a fast-forward would bring up to date.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitRepositoriesBadgeText))]
    [NotifyPropertyChangedFor(nameof(GitRepositoriesBadgeVisible))]
    private int _gitRepositoriesBadgeCount;

    public bool GitRepositoriesBadgeVisible => GitRepositoriesBadgeCount > 0;

    public string GitRepositoriesBadgeText => GitRepositoriesBadgeCount > 99 ? "99+" : GitRepositoriesBadgeCount.ToString();

    [ObservableProperty]
    private bool _gitRepositoriesIsLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DriversBadgeText))]
    [NotifyPropertyChangedFor(nameof(DriversBadgeVisible))]
    private int _driversBadgeCount;

    public bool DriversBadgeVisible => DriversBadgeCount > 0;

    public string DriversBadgeText => DriversBadgeCount > 99 ? "99+" : DriversBadgeCount.ToString();

    [ObservableProperty]
    private bool _driversIsLoading;

#if UNIGETUI_XCODE_EXTENSION
    // Whether a newer Xcode than the newest one installed can be installed; one at most.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(XcodeBadgeText))]
    [NotifyPropertyChangedFor(nameof(XcodeBadgeVisible))]
    private int _xcodeBadgeCount;

    public bool XcodeBadgeVisible => XcodeBadgeCount > 0;

    public string XcodeBadgeText => XcodeBadgeCount > 99 ? "99+" : XcodeBadgeCount.ToString();

    [ObservableProperty]
    private bool _xcodeIsLoading;
#endif

    // When the count changes, sync the badge visibility
    public static readonly HashSet<string> BadgeProperties =
    [
        nameof(UpdatesBadgeVisible),
        nameof(UpdatesBadgeText),
        nameof(BundlesBadgeVisible),
        nameof(GitRepositoriesBadgeVisible),
        nameof(GitRepositoriesBadgeText),
        nameof(DriversBadgeVisible),
        nameof(DriversBadgeText),
#if UNIGETUI_XCODE_EXTENSION
        nameof(XcodeBadgeVisible),
        nameof(XcodeBadgeText),
#endif
    ];

    public (bool Visible, string Text) BadgeFor(PageType page) => page switch
    {
        PageType.Updates => (UpdatesBadgeVisible, UpdatesBadgeText),
        PageType.Bundles => (BundlesBadgeVisible, ""),
        PageType.GitRepositories => (GitRepositoriesBadgeVisible, GitRepositoriesBadgeText),
        PageType.Drivers => (DriversBadgeVisible, DriversBadgeText),
#if UNIGETUI_XCODE_EXTENSION
        PageType.Xcode => (XcodeBadgeVisible, XcodeBadgeText),
#endif
        _ => (false, ""),
    };

    private bool _moreBadgeVisible;

    public bool MoreBadgeVisible => _moreBadgeVisible;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not (nameof(MorePages) or nameof(SelfUpdateBadgeVisible))
            && (e.PropertyName is not { } name || !BadgeProperties.Contains(name)))
            return;

        bool visible = SelfUpdateBadgeVisible || MorePages.Any(page => BadgeFor(page).Visible);
        if (visible == _moreBadgeVisible)
            return;
        _moreBadgeVisible = visible;
        base.OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(MoreBadgeVisible)));
    }

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
        NavigationPageOrder.PlaceNewlyEnabledExtensions(EnabledExtensionPages());
        _morePages = GetMorePages();
        _navigationPages = GetNavigationPages();
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

    public event EventHandler? ExtensionsRequested;

    [RelayCommand]
    private void OpenExtensions() => ExtensionsRequested?.Invoke(this, EventArgs.Empty);

    public string VersionLabel { get; } =
        CoreTools.Translate("UniGetUI Version {0} by Devolutions", CoreData.VersionName);

    [RelayCommand]
    public void RequestNavigation(string? pageName)
    {
        if (Enum.TryParse<PageType>(pageName, out var page))
            NavigationRequested?.Invoke(this, page);
    }

    [ObservableProperty]
    private bool _selfUpdateBadgeVisible;

    [ObservableProperty]
    private string _checkForUpdatesLabel = CoreTools.Translate("Check for UniGetUI updates");

    public void SetPendingSelfUpdate(string? versionName)
    {
        SelfUpdateBadgeVisible = versionName is not null;
        CheckForUpdatesLabel = versionName is null
            ? CoreTools.Translate("Check for UniGetUI updates")
            : CoreTools.Translate("UniGetUI {0} is available", ReleaseNotesLinks.ShortVersion(versionName));
    }

    [RelayCommand]
    private static void CheckForUpdates() =>
        _ = AvaloniaAutoUpdater.CheckAndInstallUpdatesAsync(autoLaunch: false, manualCheck: true);

    public void SelectNavButtonForPage(PageType page) =>
        SelectedPageType = page;

    public void SetNavItemLoading(PageType page, bool isLoading)
    {
        switch (page)
        {
            case PageType.Discover: DiscoverIsLoading = isLoading; break;
            case PageType.Updates: UpdatesIsLoading = isLoading; break;
            case PageType.Installed: InstalledIsLoading = isLoading; break;
            case PageType.GitRepositories: GitRepositoriesIsLoading = isLoading; break;
            case PageType.Drivers: DriversIsLoading = isLoading; break;
#if UNIGETUI_XCODE_EXTENSION
            case PageType.Xcode: XcodeIsLoading = isLoading; break;
#endif
        }
    }
}
