using System.Collections.Specialized;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Avalonia.Views;
using UniGetUI.Avalonia.Views.DialogPages;
using UniGetUI.Avalonia.Views.Pages;
using UniGetUI.Avalonia.Views.Pages.LogPages;
using UniGetUI.Avalonia.Views.Pages.SettingsPages;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Avalonia.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    // ─── Pages ───────────────────────────────────────────────────────────────
    private readonly DiscoverSoftwarePage DiscoverPage;
    private readonly SoftwareUpdatesPage UpdatesPage;
    private readonly InstalledPackagesPage InstalledPage;
    private readonly PackageBundlesPage BundlesPage;
    private SettingsBasePage? SettingsPage;
    private SettingsBasePage? ManagersPage;
    private UniGetUILogPage? UniGetUILogPage;
    private ManagerLogsPage? ManagerLogPage;
    private OperationHistoryPage? OperationHistoryPage;
    private HelpPage? HelpPage;
    private ReleaseNotesPage? ReleaseNotesPage;

    // ─── Navigation state ────────────────────────────────────────────────────
    private PageType _oldPage = PageType.Null;
    private PageType _currentPage = PageType.Null;
    public PageType CurrentPage_t => _currentPage;
    private readonly List<PageType> NavigationHistory = new();

    [ObservableProperty]
    private object? _currentPageContent;

    public event EventHandler<bool>? CanGoBackChanged;
    public event EventHandler<PageType>? CurrentPageChanged;

    [ObservableProperty]
    private string _announcementText = "";

    [ObservableProperty]
    private AutomationLiveSetting _announcementLiveSetting = AutomationLiveSetting.Polite;

    // ─── Operations panel ─────────────────────────────────────────────────────
    public AvaloniaList<OperationViewModel> Operations => AvaloniaOperationRegistry.OperationViewModels;

    [ObservableProperty]
    private bool _operationsPanelVisible;

    [ObservableProperty]
    private bool _operationsPanelExpanded = true;

    [RelayCommand]
    private void ToggleOperationsPanel() => OperationsPanelExpanded = !OperationsPanelExpanded;

    [RelayCommand]
    private void RetryFailedOperations() => AvaloniaOperationRegistry.RetryFailed();

    [RelayCommand]
    private void ClearSuccessfulOperations() => AvaloniaOperationRegistry.ClearSuccessful();

    [RelayCommand]
    private void ClearFinishedOperations() => AvaloniaOperationRegistry.ClearFinished();

    [RelayCommand]
    private void CancelAllOperations() => AvaloniaOperationRegistry.CancelAll();

    // ─── Sidebar ─────────────────────────────────────────────────────────────
    public SidebarViewModel Sidebar { get; } = new();

    // ─── Global search ───────────────────────────────────────────────────────
    [ObservableProperty]
    private string _globalSearchText = "";

    [ObservableProperty]
    private bool _globalSearchEnabled;

    [ObservableProperty]
    private string _globalSearchPlaceholder = "";

    // When search text changes, notify the current page
    private PackagesPageViewModel? _subscribedPageViewModel;
    private bool _syncingSearch;

    partial void OnGlobalSearchTextChanged(string value)
    {
        if (_syncingSearch) return;
        if (CurrentPageContent is AbstractPackagesPage page)
            page.ViewModel.GlobalQueryText = value;
    }

    private void SubscribeToPageViewModel(AbstractPackagesPage? page)
    {
        if (_subscribedPageViewModel is not null)
            _subscribedPageViewModel.PropertyChanged -= OnPageViewModelPropertyChanged;

        _subscribedPageViewModel = page?.ViewModel;

        if (_subscribedPageViewModel is not null)
            _subscribedPageViewModel.PropertyChanged += OnPageViewModelPropertyChanged;
    }

    private void OnPageViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PackagesPageViewModel.GlobalQueryText) && sender is PackagesPageViewModel vm)
        {
            _syncingSearch = true;
            GlobalSearchText = vm.GlobalQueryText;
            _syncingSearch = false;
        }
    }

    // ─── Banners ─────────────────────────────────────────────────────────────
    public InfoBarViewModel UpdatesBanner { get; } = new() { Severity = InfoBarSeverity.Success };
    public InfoBarViewModel ErrorBanner { get; } = new() { Severity = InfoBarSeverity.Error };
    public InfoBarViewModel WinGetWarningBanner { get; } = new() { Severity = InfoBarSeverity.Warning };
    public InfoBarViewModel TelemetryWarner { get; } = new() { Severity = InfoBarSeverity.Informational };

    // ─── Constructor ─────────────────────────────────────────────────────────
    [RelayCommand]
    private void ToggleSidebar() => Sidebar.IsPaneOpen = !Sidebar.IsPaneOpen;

    public MainWindowViewModel()
    {
        AccessibilityAnnouncementService.AnnouncementRequested += OnAnnouncementRequested;

        DiscoverPage = new DiscoverSoftwarePage();
        UpdatesPage = new SoftwareUpdatesPage();
        InstalledPage = new InstalledPackagesPage();
        BundlesPage = new PackageBundlesPage();

        // Wire loader status → sidebar badges (loaders are null until package engine initializes)
        foreach (var (pageType, loader) in new (PageType, AbstractPackageLoader?)[]
        {
            (PageType.Discover,  DiscoverablePackagesLoader.Instance),
            (PageType.Updates,   UpgradablePackagesLoader.Instance),
            (PageType.Installed, InstalledPackagesLoader.Instance),
        })
        {
            if (loader is null) continue;
            var pt = pageType;
            loader.FinishedLoading += (_, _) =>
                Dispatcher.UIThread.Post(() => Sidebar.SetNavItemLoading(pt, false));
            loader.StartedLoading += (_, _) =>
                Dispatcher.UIThread.Post(() => Sidebar.SetNavItemLoading(pt, true));
            Sidebar.SetNavItemLoading(pt, loader.IsLoading);
        }

        if (UpgradablePackagesLoader.Instance is { } upgLoader)
        {
            upgLoader.PackagesChanged += (_, _) =>
                Dispatcher.UIThread.Post(() =>
                {
                    Sidebar.UpdatesBadgeCount = upgLoader.Count();
                    MainWindow.Instance?.UpdateSystemTrayStatus();
                });
            Sidebar.UpdatesBadgeCount = upgLoader.Count();
            // Notifications and auto-update logic are handled by SoftwareUpdatesPage.WhenPackagesLoaded
        }

        WindowsAppNotificationBridge.NotificationActivated += action =>
            Dispatcher.UIThread.Post(() => HandleNotificationActivation(action));

        BundlesPage.UnsavedChangesStateChanged += (_, _) =>
            Dispatcher.UIThread.Post(() =>
                Sidebar.BundlesBadgeVisible = BundlesPage.HasUnsavedChanges);
        Sidebar.BundlesBadgeVisible = BundlesPage.HasUnsavedChanges;

        Sidebar.NavigationRequested += (_, pageType) => NavigateTo(pageType);

        AvaloniaAutoUpdater.UpdateAvailable += version => Dispatcher.UIThread.Post(() =>
        {
            UpdatesBanner.Title = CoreTools.Translate("UniGetUI {0} is ready to be installed.", version);
            UpdatesBanner.Message = CoreTools.Translate("The update process will start after closing UniGetUI");
            UpdatesBanner.ActionButtonText = CoreTools.Translate("Update now");
            UpdatesBanner.ActionButtonCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(AvaloniaAutoUpdater.TriggerInstall);
            UpdatesBanner.IsClosable = true;
            UpdatesBanner.IsOpen = true;
        });

        // Keep OperationsPanelVisible in sync with the live operations list
        Operations.CollectionChanged += (_, _) =>
            OperationsPanelVisible = Operations.Count > 0;

        if (OperatingSystem.IsWindows() && CoreTools.IsAdministrator() && !Settings.Get(Settings.K.AlreadyWarnedAboutAdmin))
        {
            Settings.Set(Settings.K.AlreadyWarnedAboutAdmin, true);
            WinGetWarningBanner.Title = CoreTools.Translate("Administrator privileges");
            WinGetWarningBanner.Message = CoreTools.Translate(
                "UniGetUI has been ran as administrator, which is not recommended. When running UniGetUI as administrator, EVERY operation launched from UniGetUI will have administrator privileges. You can still use the program, but we highly recommend not running UniGetUI with administrator privileges."
            );
            WinGetWarningBanner.IsClosable = true;
            WinGetWarningBanner.IsOpen = true;
        }

        if (!Settings.Get(Settings.K.ShownTelemetryBanner))
        {
            TelemetryWarner.Title = CoreTools.Translate("Share anonymous usage data");
            TelemetryWarner.Message = CoreTools.Translate(
                "UniGetUI collects anonymous usage data in order to improve the user experience."
            );
            TelemetryWarner.IsClosable = true;
            TelemetryWarner.ActionButtonText = CoreTools.Translate("Accept");
            TelemetryWarner.ActionButtonCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(() =>
            {
                TelemetryWarner.IsOpen = false;
                Settings.Set(Settings.K.ShownTelemetryBanner, true);
            });
            TelemetryWarner.OnClosed = () => Settings.Set(Settings.K.ShownTelemetryBanner, true);
            TelemetryWarner.IsOpen = true;
        }

        LoadDefaultPage();
    }

    private void OnAnnouncementRequested(object? _, AccessibilityAnnouncement announcement)
    {
        AnnouncementLiveSetting = announcement.LiveSetting;
        AnnouncementText = string.Empty;
        Dispatcher.UIThread.Post(
            () => AnnouncementText = announcement.Message,
            DispatcherPriority.Background);
    }

    // ─── Navigation ──────────────────────────────────────────────────────────
    public void LoadDefaultPage()
    {
        PageType type = Settings.GetValue(Settings.K.StartupPage) switch
        {
            "discover" => PageType.Discover,
            "updates" => PageType.Updates,
            "installed" => PageType.Installed,
            "bundles" => PageType.Bundles,
            "settings" => PageType.Settings,
            _ => UpgradablePackagesLoader.Instance is { } l && l.Count() > 0 ? PageType.Updates : PageType.Discover,
        };
        NavigateTo(type);
    }

    private Control GetPageForType(PageType type) =>
        type switch
        {
            PageType.Discover => DiscoverPage,
            PageType.Updates => UpdatesPage,
            PageType.Installed => InstalledPage,
            PageType.Bundles => BundlesPage,
            PageType.Settings => SettingsPage ??= new SettingsBasePage(false),
            PageType.Managers => ManagersPage ??= new SettingsBasePage(true),
            PageType.OwnLog => UniGetUILogPage ??= new UniGetUILogPage(),
            PageType.ManagerLog => ManagerLogPage ??= new ManagerLogsPage(),
            PageType.OperationHistory => OperationHistoryPage ??= new OperationHistoryPage(),
            PageType.Help => HelpPage ??= new HelpPage(),
            PageType.ReleaseNotes => ReleaseNotesPage ??= new ReleaseNotesPage(),
            PageType.Null => throw new InvalidOperationException("Page type is Null"),
            _ => throw new InvalidDataException($"Unknown page type {type}"),
        };

    public static PageType GetNextPage(PageType type) =>
        type switch
        {
            PageType.Discover => PageType.Updates,
            PageType.Updates => PageType.Installed,
            PageType.Installed => PageType.Bundles,
            PageType.Bundles => PageType.Settings,
            PageType.Settings => PageType.Managers,
            PageType.Managers => PageType.Discover,
            _ => PageType.Discover,
        };

    public static PageType GetPreviousPage(PageType type) =>
        type switch
        {
            PageType.Discover => PageType.Managers,
            PageType.Updates => PageType.Discover,
            PageType.Installed => PageType.Updates,
            PageType.Bundles => PageType.Installed,
            PageType.Settings => PageType.Bundles,
            PageType.Managers => PageType.Settings,
            _ => PageType.Discover,
        };

    public void NavigateTo(PageType newPage_t, bool toHistory = true)
    {
        if (newPage_t is PageType.About) { _ = ShowAboutDialog(); return; }
        if (newPage_t is PageType.Quit) { (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown(); return; }

        if (_currentPage == newPage_t)
        {
            // Re-focus the primary control even when we're already on the page
            (CurrentPageContent as AbstractPackagesPage)?.FocusPackageList();
            return;
        }

        Sidebar.SelectNavButtonForPage(newPage_t);

        var newPage = GetPageForType(newPage_t);
        var oldPage = CurrentPageContent as Control;

        if (oldPage is ISearchBoxPage oldSPage)
            oldSPage.QueryBackup = GlobalSearchText;
        (oldPage as IEnterLeaveListener)?.OnLeave();

        CurrentPageContent = newPage;
        _oldPage = _currentPage;
        _currentPage = newPage_t;

        if (toHistory && _oldPage is not PageType.Null)
        {
            NavigationHistory.Add(_oldPage);
            CanGoBackChanged?.Invoke(this, true);
        }

        (newPage as AbstractPackagesPage)?.FilterPackages();
        (newPage as IEnterLeaveListener)?.OnEnter();

        if (newPage is ISearchBoxPage newSPage)
        {
            SubscribeToPageViewModel(newPage as AbstractPackagesPage);
            GlobalSearchText = newSPage.QueryBackup;
            GlobalSearchPlaceholder = newSPage.SearchBoxPlaceholder;
            GlobalSearchEnabled = true;
        }
        else
        {
            SubscribeToPageViewModel(null);
            GlobalSearchText = "";
            GlobalSearchPlaceholder = "";
            GlobalSearchEnabled = false;
        }

        // Focus after search state is restored so MegaQueryVisible is already correct
        (newPage as AbstractPackagesPage)?.FocusPackageList();

        AccessibilityAnnouncementService.Announce(GetPageAnnouncement(newPage_t));
        CurrentPageChanged?.Invoke(this, newPage_t);
    }

    private static string GetPageAnnouncement(PageType pageType) => pageType switch
    {
        PageType.Discover => CoreTools.Translate("Discover Packages"),
        PageType.Updates => CoreTools.Translate("Software Updates"),
        PageType.Installed => CoreTools.Translate("Installed Packages"),
        PageType.Bundles => CoreTools.Translate("Package Bundles"),
        PageType.Settings => CoreTools.Translate("Settings"),
        PageType.Managers => CoreTools.Translate("Package Managers"),
        PageType.OwnLog => CoreTools.Translate("UniGetUI Log"),
        PageType.ManagerLog => CoreTools.Translate("Package Manager logs"),
        PageType.OperationHistory => CoreTools.Translate("Operation history"),
        PageType.Help => CoreTools.Translate("Help"),
        PageType.ReleaseNotes => CoreTools.Translate("Release notes"),
        _ => CoreTools.Translate("UniGetUI"),
    };

    public void NavigateBack()
    {
        if (CurrentPageContent is IInnerNavigationPage navPage && navPage.CanGoBack())
        {
            navPage.GoBack();
        }
        else if (NavigationHistory.Count > 0)
        {
            NavigateTo(NavigationHistory.Last(), toHistory: false);
            NavigationHistory.RemoveAt(NavigationHistory.Count - 1);
            CanGoBackChanged?.Invoke(this,
                NavigationHistory.Count > 0
                || ((CurrentPageContent as IInnerNavigationPage)?.CanGoBack() ?? false));
        }
    }

    public void OpenManagerLogs(IPackageManager? manager = null)
    {
        NavigateTo(PageType.ManagerLog);
        if (manager is not null) ManagerLogPage?.LoadForManager(manager);
    }

    public void OpenManagerSettings(IPackageManager? manager = null)
    {
        NavigateTo(PageType.Managers);
        if (manager is not null) ManagersPage?.NavigateTo(manager);
    }

    public void OpenSettingsPage(Type page)
    {
        NavigateTo(PageType.Settings);
        SettingsPage?.NavigateTo(page);
    }

    public void ShowHelp(string uriAttachment = "")
    {
        NavigateTo(PageType.Help);
        HelpPage?.NavigateTo(uriAttachment);
    }

    public async Task LoadCloudBundleAsync(string content)
    {
        NavigateTo(PageType.Bundles);
        await BundlesPage.OpenFromString(content, BundleFormatType.UBUNDLE, "GitHub Gist");
    }

    private async Task ShowAboutDialog()
    {
        Sidebar.SelectNavButtonForPage(PageType.Null);
        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (owner is not null)
            await new AboutWindow().ShowDialog(owner);
        Sidebar.SelectNavButtonForPage(_currentPage);
    }

    // ─── Notification activation ─────────────────────────────────────────────
    private void HandleNotificationActivation(string action)
    {
        if (action == NotificationArguments.UpdateAllPackages)
        {
            _ = AvaloniaPackageOperationHelper.UpdateAllAsync();
        }
        else if (action == NotificationArguments.ShowOnUpdatesTab)
        {
            NavigateTo(PageType.Updates);
            MainWindow.Instance?.ShowFromTray();
        }
        else if (action == NotificationArguments.Show)
        {
            MainWindow.Instance?.ShowFromTray();
        }
        else if (action == NotificationArguments.ReleaseSelfUpdateLock)
        {
            AvaloniaAutoUpdater.ReleaseLockForAutoupdate_Notification = true;
        }
    }

    // ─── Search box ──────────────────────────────────────────────────────────
    [RelayCommand]
    public void SubmitGlobalSearch()
    {
        if (CurrentPageContent is ISearchBoxPage page)
            page.SearchBox_QuerySubmitted(this, EventArgs.Empty);
    }
}
