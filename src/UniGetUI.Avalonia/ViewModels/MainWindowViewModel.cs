using System.Collections.ObjectModel;
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
using UniGetUI.PackageOperations;

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
    [NotifyPropertyChangedFor(nameof(ShowFailedOperationBadge))]
    private bool _operationsPanelVisible;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFailedOperationBadge))]
    private bool _operationsPanelExpanded = true;

    private readonly List<AbstractOperation> _operationBatch = new();
    private bool _batchSummaryShown;

    public bool HasFailedOperations => Operations.Any(o => o.Operation.Status is OperationStatus.Failed);

    public OperationViewModel? FirstFailedOperation =>
        Operations.FirstOrDefault(o => o.Operation.Status is OperationStatus.Failed);

    // Red badge on the chevron when the panel is collapsed and an operation failed
    // (failures no longer pop a toast; expanded, the failure is already visible).
    public bool ShowFailedOperationBadge => OperationsPanelVisible && !OperationsPanelExpanded && HasFailedOperations;

    public string OperationsHeaderText
    {
        get
        {
            int total = _operationBatch.Count;
            if (total == 0)
                return CoreTools.Translate("Operations");
            int completed = _operationBatch.Count(o =>
                o.Status is OperationStatus.Succeeded or OperationStatus.Failed or OperationStatus.Canceled);
            return CoreTools.Translate("{0} of {1} operations completed", completed, total);
        }
    }

    private void AddToBatch(AbstractOperation op)
    {
        if (_operationBatch.Count > 0
            && _operationBatch.All(o => o.Status is not (OperationStatus.InQueue or OperationStatus.Running)))
        {
            foreach (var old in _operationBatch)
                old.StatusChanged -= OnOperationStatusChanged;
            _operationBatch.Clear();
            _batchSummaryShown = false;
        }

        if (!_operationBatch.Contains(op))
        {
            _operationBatch.Add(op);
            op.StatusChanged += OnOperationStatusChanged;
        }
    }

    private void OnOperationStatusChanged(object? sender, OperationStatus e)
        => Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(OperationsHeaderText));
            OnPropertyChanged(nameof(HasFailedOperations));
            OnPropertyChanged(nameof(ShowFailedOperationBadge));
            MaybeShowBatchSummaryToast();
        });

    // Opt-in: one toast summarizing the batch once every operation in it has finished.
    private void MaybeShowBatchSummaryToast()
    {
        if (_batchSummaryShown || _operationBatch.Count == 0) return;
        if (!Settings.Get(Settings.K.ShowOperationSummaryNotifications)) return;
        if (_operationBatch.Any(o => o.Status is OperationStatus.InQueue or OperationStatus.Running)) return;

        _batchSummaryShown = true;

        int total = _operationBatch.Count;
        int failed = _operationBatch.Count(o => o.Status is OperationStatus.Failed);
        int succeeded = _operationBatch.Count(o => o.Status is OperationStatus.Succeeded);

        var toast = new InfoBarViewModel
        {
            Title = failed > 0
                ? CoreTools.Translate("Operations finished with errors")
                : CoreTools.Translate("Operations completed"),
            Message = failed > 0
                ? CoreTools.Translate("{0} of {1} succeeded, {2} failed", succeeded, total, failed)
                : CoreTools.Translate("{0} of {1} operations completed", succeeded, total),
            Severity = failed > 0 ? InfoBarSeverity.Error : InfoBarSeverity.Success,
            IsClosable = true,
            IsOpen = true,
        };
        toast.OnClosed = () => DismissToast(toast);
        ShowToast(toast);
    }

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
        else if (CurrentPageContent is SettingsBasePage)
            UpdateSettingsSuggestions(value);
    }

    // ─── Settings search suggestions ───────────────────────────────────────────
    public ObservableCollection<SettingsSearchResult> SettingsSuggestions { get; } = new();

    [ObservableProperty]
    private bool _isSuggestionsOpen;

    [ObservableProperty]
    private int _selectedSuggestionIndex = -1;

    private void UpdateSettingsSuggestions(string query)
    {
        SettingsSuggestions.Clear();
        foreach (var r in SettingsSearchIndex.Search(query))
            SettingsSuggestions.Add(r);

        SelectedSuggestionIndex = SettingsSuggestions.Count > 0 ? 0 : -1;
        IsSuggestionsOpen = SettingsSuggestions.Count > 0;
    }

    public void MoveSuggestionSelection(int delta)
    {
        if (SettingsSuggestions.Count == 0) return;
        int next = SelectedSuggestionIndex + delta;
        SelectedSuggestionIndex = Math.Clamp(next, 0, SettingsSuggestions.Count - 1);
    }

    public void CloseSuggestions()
    {
        IsSuggestionsOpen = false;
        SelectedSuggestionIndex = -1;
        SettingsSuggestions.Clear();
    }

    [RelayCommand]
    public void SelectSuggestion(SettingsSearchResult? result)
    {
        if (result is null) return;

        _syncingSearch = true;
        GlobalSearchText = "";
        _syncingSearch = false;
        CloseSuggestions();

        if (result.Manager is not null)
            OpenManagerSettings(result.Manager);
        else if (result.PageType is not null)
            OpenSettingsPage(result.PageType, result.Anchor);
    }

    private void SubscribeToPageViewModel(AbstractPackagesPage? page)
    {
        _subscribedPageViewModel?.PropertyChanged -= OnPageViewModelPropertyChanged;

        _subscribedPageViewModel = page?.ViewModel;

        _subscribedPageViewModel?.PropertyChanged += OnPageViewModelPropertyChanged;
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

    // ─── Title bar ───────────────────────────────────────────────────────────
    // Mirrors WinUI behavior: the version appears next to "UniGetUI" only when
    // the ShowVersionNumberOnTitlebar setting is enabled (the setting is gated
    // on restart, so a one-shot read at construction is sufficient).
    public string TitleBarText { get; } = Settings.Get(Settings.K.ShowVersionNumberOnTitlebar)
        ? $"UniGetUI {CoreTools.Translate("version {0}", CoreData.VersionName)}"
        : "UniGetUI";

    // ─── Notifications (bottom-right toasts) ───────────────────────────────────
    // Persistent banners kept as singletons; RegisterBannerToast mirrors their IsOpen into Toasts.
    public InfoBarViewModel UpdatesBanner { get; } = new() { Severity = InfoBarSeverity.Success };
    public InfoBarViewModel WinGetWarningBanner { get; } = new() { Severity = InfoBarSeverity.Warning };
    public InfoBarViewModel TelemetryWarner { get; } = new() { Severity = InfoBarSeverity.Informational };

    // Oldest first (rendered bottom-up so the newest sits nearest the corner).
    public ObservableCollection<InfoBarViewModel> Toasts { get; } = new();
    private readonly Dictionary<InfoBarViewModel, DispatcherTimer> _toastTimers = new();
    private const int MaxVisibleToasts = 5;

    // A toast with an action button stays until acted on; everything else auto-dismisses.
    private static bool IsSticky(InfoBarViewModel t) => !string.IsNullOrEmpty(t.ActionButtonText);
    private static TimeSpan ToastDuration(InfoBarViewModel t)
        => t.Severity is InfoBarSeverity.Error or InfoBarSeverity.Warning
            ? TimeSpan.FromSeconds(8)
            : TimeSpan.FromSeconds(5);

    public void ShowToast(InfoBarViewModel toast)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ShowToast(toast));
            return;
        }
        if (!Toasts.Contains(toast))
        {
            Toasts.Add(toast);
            AnnounceToast(toast);
        }
        TrimToastStack(keep: toast);
        ArmToastTimer(toast);
    }

    // Keep the stack from overflowing the screen: drop the oldest auto-dismissing toasts,
    // but never a sticky (action-button) one or the toast just shown.
    private void TrimToastStack(InfoBarViewModel keep)
    {
        while (Toasts.Count > MaxVisibleToasts)
        {
            var oldest = Toasts.FirstOrDefault(t => t != keep && !IsSticky(t));
            if (oldest is null) break;
            DismissToast(oldest);
        }
    }

    // Surface the toast to screen readers via the live region (assertive for errors so it
    // interrupts, polite otherwise) — the toast's own automation name isn't announced on its own.
    private static void AnnounceToast(InfoBarViewModel toast)
    {
        string message = string.IsNullOrEmpty(toast.Message)
            ? toast.Title
            : $"{toast.Title}. {toast.Message}";
        AccessibilityAnnouncementService.Announce(
            message,
            toast.Severity is InfoBarSeverity.Error
                ? AutomationLiveSetting.Assertive
                : AutomationLiveSetting.Polite);
    }

    public void DismissToast(InfoBarViewModel toast)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => DismissToast(toast));
            return;
        }
        DisposeToastTimer(toast);
        Toasts.Remove(toast);
    }

    // Pause the auto-dismiss countdown while the pointer hovers a toast; restart it on leave.
    public void PauseToast(InfoBarViewModel toast)
    {
        if (_toastTimers.TryGetValue(toast, out var timer)) timer.Stop();
    }

    public void ResumeToast(InfoBarViewModel toast)
    {
        if (_toastTimers.TryGetValue(toast, out var timer)) { timer.Stop(); timer.Start(); }
    }

    private void ArmToastTimer(InfoBarViewModel toast)
    {
        DisposeToastTimer(toast);
        if (IsSticky(toast))
            return;

        var timer = new DispatcherTimer { Interval = ToastDuration(toast) };
        timer.Tick += (_, _) => toast.IsOpen = false;
        _toastTimers[toast] = timer;
        timer.Start();
    }

    private void DisposeToastTimer(InfoBarViewModel toast)
    {
        if (_toastTimers.Remove(toast, out var timer))
            timer.Stop();
    }

    // Mirrors a persistent banner's IsOpen into Toasts so code that just flips IsOpen keeps working.
    private void RegisterBannerToast(InfoBarViewModel banner)
    {
        banner.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(InfoBarViewModel.IsOpen))
            {
                if (banner.IsOpen) ShowToast(banner);
                else DismissToast(banner);
            }
            // Content can change in place while the banner stays open (the updater cycles
            // statuses on the same banner, finally adding the "Update now" action); re-arm so
            // stickiness/countdown track the new content instead of a stale timer dismissing it.
            else if (banner.IsOpen && Toasts.Contains(banner))
            {
                ArmToastTimer(banner);
            }
        };
    }

    // ─── Constructor ─────────────────────────────────────────────────────────
    [RelayCommand]
    private void ToggleSidebar() => Sidebar.IsPaneOpen = !Sidebar.IsPaneOpen;

    public MainWindowViewModel()
    {
        AccessibilityAnnouncementService.AnnouncementRequested += OnAnnouncementRequested;

        // Wire before the blocks below flip the banners' IsOpen flags.
        RegisterBannerToast(UpdatesBanner);
        RegisterBannerToast(WinGetWarningBanner);
        RegisterBannerToast(TelemetryWarner);

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
            {
                Dispatcher.UIThread.Post(() => Sidebar.SetNavItemLoading(pt, false));
                // Return the load's memory to the OS once it settles.
                Infrastructure.MemoryTrimmer.RequestTrimAfterIdle();
            };
            loader.StartedLoading += (_, _) =>
            {
                Dispatcher.UIThread.Post(() => Sidebar.SetNavItemLoading(pt, true));
                Infrastructure.MemoryTrimmer.CancelPending();
            };
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
            UpdatesBanner.Severity = InfoBarSeverity.Success;
            UpdatesBanner.Title = CoreTools.Translate("UniGetUI {0} is ready to be installed.", version);
            UpdatesBanner.Message = CoreTools.Translate("The update process will start after closing UniGetUI");
            UpdatesBanner.ActionButtonText = CoreTools.Translate("Update now");
            UpdatesBanner.ActionButtonCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(AvaloniaAutoUpdater.TriggerInstall);
            UpdatesBanner.IsClosable = true;
            UpdatesBanner.IsOpen = true;
        });

        AvaloniaAutoUpdater.StatusChanged += status => Dispatcher.UIThread.Post(() =>
        {
            UpdatesBanner.Severity = status.Severity;
            UpdatesBanner.Title = status.Title;
            UpdatesBanner.Message = status.Message;
            UpdatesBanner.ActionButtonText = status.ActionButtonText ?? "";
            UpdatesBanner.ActionButtonCommand = status.ActionButtonAction is { } action
                ? new CommunityToolkit.Mvvm.Input.RelayCommand(action)
                : null;
            UpdatesBanner.IsClosable = status.IsClosable;
            UpdatesBanner.IsOpen = true;
        });

        // If the previous update attempt was killed mid-flow (typically by the
        // installer terminating us during file replacement), surface a banner now
        // that subscriptions are wired up.
        AvaloniaAutoUpdater.CheckForOrphanedUpdateAttempt();

        // Keep OperationsPanelVisible in sync with the live operations list
        Operations.CollectionChanged += (_, e) =>
        {
            if (e.NewItems is not null)
                foreach (OperationViewModel vm in e.NewItems)
                    AddToBatch(vm.Operation);
            OperationsPanelVisible = Operations.Count > 0;
            OnPropertyChanged(nameof(OperationsHeaderText));
            OnPropertyChanged(nameof(HasFailedOperations));
            OnPropertyChanged(nameof(ShowFailedOperationBadge));
        };

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

        if (WasUpdatedSinceLastRun() && !Settings.Get(Settings.K.DisableReleaseNotesOnUpdate))
        {
            NavigateTo(PageType.ReleaseNotes);
        }
        else
        {
            LoadDefaultPage();
        }
    }

    // Returns true the first time the app runs after being updated to a newer build
    private static bool WasUpdatedSinceLastRun()
    {
        _ = int.TryParse(Settings.GetValue(Settings.K.LastKnownBuildNumber), out int lastBuild);

        if (lastBuild != CoreData.BuildNumber)
        {
            Settings.SetValue(Settings.K.LastKnownBuildNumber, CoreData.BuildNumber.ToString());
        }

        return lastBuild != 0 && lastBuild < CoreData.BuildNumber;
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
        if (newPage_t is PageType.Quit) { MainWindow.Instance?.QuitApplication(); return; }

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

        CloseSuggestions();

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

    public void OpenSettingsPage(Type page, string? anchor = null)
    {
        NavigateTo(PageType.Settings);
        SettingsPage?.NavigateTo(page, anchor);
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
        // On settings/managers the box drives the suggestion dropdown: jump to the highlighted
        // result (falling back to the top one).
        if (CurrentPageContent is SettingsBasePage)
        {
            if (SettingsSuggestions.Count == 0) return;
            int i = SelectedSuggestionIndex >= 0 ? SelectedSuggestionIndex : 0;
            SelectSuggestion(SettingsSuggestions[i]);
            return;
        }

        if (CurrentPageContent is ISearchBoxPage page)
            page.SearchBox_QuerySubmitted(this, EventArgs.Empty);
    }
}
