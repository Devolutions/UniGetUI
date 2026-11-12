using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Views;
using UniGetUI.Avalonia.Views.DialogPages;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Extensions.Xcode;
using UniGetUI.Extensions.Xcode.AppleAuth;

namespace UniGetUI.Avalonia.ViewModels.Pages;

/// <summary>
/// The Xcode page installs and selects Xcode versions, and manages supported simulator runtime workflows.
/// One instance lives for the whole session, so checks and the sidebar badge continue while the page is closed.
/// </summary>
public partial class XcodeViewModel : ViewModelBase
{
    /// <summary>What an installation writes is kept to read; this many lines is far more than it ever needs.</summary>
    private const int MaxLogLines = 300;
    private const int MaxLogCharacters = 512 * 1024;
    private const int MaxOutputLineLength = 16 * 1024;
    private const string LongLineOmitted = "[A long output line was omitted.]";

    private readonly XcodesService _xcodes;
    private readonly IXcodeReleasesCatalog? _catalog;
    private readonly IXcodeSimulatorRuntimeInstaller _runtimeInstaller;
    private readonly IXcodePublicSimulatorRuntimeDownloader _runtimeDownloader;
    private readonly DispatcherTimer _checkTimer = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<string> _log = [];
    private readonly object _outputLock = new();
    private readonly Queue<XcodesOutputLine> _pendingOutput = new();
    private CancellationTokenSource? _outputOperation;
    private int _pendingOutputCharacters;
    private bool _pendingOutputOmitted;
    private bool _outputScheduled;
    private int _logCharacters;
    private bool _logOmitted;
    private bool _possibleAccessFailure;
    private string? _accessFailureOutput;
    private CancellationTokenSource? _operationCancellation;
    private bool _operationIsInstallation;
    private BrowserOperationTarget? _browserOperationTarget;
    private IReadOnlyList<XcodeRelease> _releases = [];
    private IReadOnlyList<InstalledXcode> _installedXcodes = [];
    private IReadOnlyList<XcodeCatalogRelease> _catalogReleases = [];
    private bool _catalogCacheLoaded;
    private bool _catalogFromSavedFile;
    private bool _catalogFresh;
    private DateTime? _catalogChecked;
    private bool _initialized;
    private bool _stopped;
    private bool _versionLoadNoticeShown;
    private bool _lastVersionRefreshFailed;
    private bool _operationUsesXcodebuild;
    private bool _operationDownloadsPublicRuntime;
    private string? _signInAppleId;
    private bool _rebuildingBrowser;
    private bool _syncingTreeSelection;
    private string? _preferredBrowserKey;
    private readonly HashSet<string> _collapsedBrowserGroups;
    private readonly Dictionary<string, XcodeBrowserNodeViewModel> _browserNodes = [];
    private XcodeRelease? _retryRelease;
    private InstalledXcode? _retryReplacing;
    private BrowserOperationTarget? _retryDownloadTarget;
    private (string Version, string Build, XcodeArchitecture Architecture, string AppleId)? _lastAutomaticAttempt;
    private int _releaseFilterIndex;
    private int _architectureFilterIndex;

    private sealed record BrowserOperationTarget(string Value, bool IsInstalled)
    {
        public bool Matches(XcodeBrowserItemViewModel item) => IsInstalled
            ? item.IsInstalled && string.Equals(item.Location, Value, StringComparison.Ordinal)
            : item.IsAvailable && string.Equals(item.Key, Value, StringComparison.Ordinal);
    }

    public XcodeViewModel() : this(SharedService, catalog: SharedCatalog) { }

    internal static XcodeReleasesCatalog CreateCatalog()
        => new(() => Path.Combine(CoreData.UniGetUICacheDirectory_Data, "XcodeReleases.json"));

    private static readonly Lazy<XcodeCatalogSession> _sharedCatalog = new(() => new XcodeCatalogSession(CreateCatalog()));
    private static readonly Lazy<XcodesService> _sharedService = new(() => new XcodesService(SharedCatalog,
        () => Path.Combine(CoreData.UniGetUICacheDirectory_Data, "XcodeArchives"), AppleAccount.CreateDefault()));

    /// <summary>The release list the page and the installer share, so one check downloads it once.</summary>
    internal static XcodeCatalogSession SharedCatalog => _sharedCatalog.Value;

    internal static XcodesService? SharedServiceForTests { get; set; }

    /// <summary>The service the page, its settings and the Apple ID card share, with the one Apple account.</summary>
    internal static XcodesService SharedService => SharedServiceForTests ?? _sharedService.Value;

    internal static XcodeSimulatorRuntimeService CreateSimulatorRuntimeService()
        => new(() => Path.Combine(CoreData.UniGetUICacheDirectory_Data, "AppleSimulatorCatalog.plist"));

    internal XcodeViewModel(XcodesService xcodes, XcodeAccountViewModel? account = null, IXcodeReleasesCatalog? catalog = null,
        IXcodeSimulatorRuntimeService? runtimes = null, IXcodeSimulatorRuntimeInstaller? runtimeInstaller = null,
        IXcodePublicSimulatorRuntimeDownloader? runtimeDownloader = null)
    {
        _xcodes = xcodes;
        _catalog = catalog;
        _runtimeInstaller = runtimeInstaller ?? new XcodeSimulatorRuntimeInstaller();
        _runtimeDownloader = runtimeDownloader ?? new XcodePublicSimulatorRuntimeDownloader();
        ConfirmCancel = ConfirmCancelInWindowAsync;
        Runtimes = new XcodeSimulatorRuntimesViewModel(runtimes ?? CreateSimulatorRuntimeService());
        if (catalog is not null)
            _catalogStatusText = CoreTools.Translate("Release details are loaded when checking for updates or starting an install.");
        Account = account ?? new XcodeAccountViewModel(xcodes);
        ConfirmInstall = ConfirmInstallInWindowAsync;
        ConfirmDownload = ConfirmDownloadInWindowAsync;
        Account.PropertyChanged += AccountPropertyChanged;
        _includePrereleases = XcodePreferences.IncludePrereleases;
        _installedOnly = XcodePreferences.BrowserInstalledOnly;
        _groupVersions = XcodePreferences.BrowserGrouped;
        _collapsedBrowserGroups = XcodePreferences.BrowserCollapsedGroups;
        _releaseFilterIndex = XcodePreferences.BrowserReleaseFilter;
        _architectureFilterIndex = XcodePreferences.BrowserArchitectureFilter;
        _checkTimer.Tick += async (_, _) => await CheckAutomaticallyAsync();
        ConfigureTimer();
    }

    public InfoBarViewModel Notice { get; } = new();
    public XcodeAccountViewModel Account { get; }
    public XcodeSimulatorRuntimesViewModel Runtimes { get; }

    private void AccountPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(XcodeAccountViewModel.IsBusy))
        {
            OnPropertyChanged(nameof(CanStartOperation));
            OnPropertyChanged(nameof(CanOpenSimulatorRuntimes));
            OnPropertyChanged(nameof(CanRetryInstallation));
        }
    }

    // What the page asks in dialogs over the main window. The tests have no window, so they answer for it.
    internal Func<XcodeRelease, InstalledXcode?, XcodeCompatibilityAssessment, XcodeInstallOptions, Task<bool>> ConfirmInstall { get; set; }
    internal Func<XcodeRelease, Task<bool>> ConfirmDownload { get; set; }
    internal Func<InstalledXcodeItemViewModel, Task<bool>> ConfirmUninstall { get; set; }
        = ConfirmUninstallInWindowAsync;
    internal Func<XcodeSimulatorRuntimeInstallPlan, Task<bool>> ConfirmRuntimeInstall { get; set; }
        = ConfirmRuntimeInstallInWindowAsync;
    internal Func<Task<string?>> ChooseRuntimeExportFolder { get; set; } = ChooseRuntimeExportFolderInWindowAsync;
    internal Func<XcodeSimulatorRuntimeInstallPlan, string, Task<bool>> ConfirmRuntimeExport { get; set; }
        = ConfirmRuntimeExportInWindowAsync;
    internal Func<XcodeSimulatorRuntime, string, Task<bool>> ConfirmRuntimeDownload { get; set; }
        = ConfirmRuntimeDownloadInWindowAsync;
    internal Func<Task<bool>> ConfirmCancel { get; set; }
    internal Func<string, Task> CopyText { get; set; } = CopyTextToClipboardAsync;
    internal Func<string, Task> OpenXcode { get; set; } = OpenXcodeCoreAsync;
    internal Func<string, Task> ShowPathInFinder { get; set; } = ShowPathInFinderCoreAsync;
    internal Func<string, Task> ShowArchiveInFinder { get; set; } = ShowArchiveInFinderCoreAsync;
    internal Func<string, Task> ShowSimulatorPathInFinder { get; set; } = ShowSimulatorPathInFinderCoreAsync;
    internal Func<Version> CurrentMacOS { get; set; } = () => Environment.OSVersion.Version;
    internal Func<XcodesPrompt, CancellationToken, Task<string?>> Ask { get; set; } = XcodePromptDialog.AskAsync;

    // ─── State ───────────────────────────────────────────────────────────────

    /// <summary>Something is being read, installed or removed; one thing at a time.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStartOperation), nameof(CanOpenSimulatorRuntimes),
        nameof(CanRetryInstallation), nameof(ShowBusyBar))]
    private bool _isBusy;

    /// <summary>Whether a button that installs, selects or uninstalls can be used now.</summary>
    public bool CanStartOperation => !IsBusy && !Account.IsBusy;

    public bool CanOpenSimulatorRuntimes => CanStartOperation || (IsOperationRunning && Runtimes.HasOperationTarget);

    public bool CanRetryInstallation => CanStartOperation && HasLoaded;

    /// <summary>The bar under the toolbar; an installation shows its own, with the progress.</summary>
    public bool ShowBusyBar => IsBusy && !IsOperationRunning;

    /// <summary>Whether the lists have been read at least once, so that an empty one means nothing is there.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NoneInstalled), nameof(HasInstalledCountBadge), nameof(IsAvailableEmpty),
        nameof(AvailableEmptyText), nameof(BrowserEmptyText), nameof(CanRetryInstallation))]
    private bool _hasLoaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableEmptyText), nameof(BrowserEmptyText))]
    private bool _hasVersionLoadError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastCheckedText))]
    private DateTime? _lastChecked;

    public string LastCheckedText => LastChecked is { } checkedAt
        ? CoreTools.Translate("Checked {0}", checkedAt.ToString("g", System.Globalization.CultureInfo.CurrentCulture))
        : "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVersionListStatus))]
    private string _versionListStatusText = "";

    public bool HasVersionListStatus => VersionListStatusText.Length > 0;

    [ObservableProperty]
    private string _catalogStatusText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRuntimeBrowser), nameof(ShowVersionBrowser))]
    private bool _showSimulatorRuntimes;

    public bool ShowRuntimeBrowser => ShowSimulatorRuntimes;
    public bool ShowVersionBrowser => !ShowSimulatorRuntimes;

    // ─── Installed and available versions ───────────────────────────────────

    internal event Action? ScrollToSelectedRequested;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(HasInstalled),
        nameof(NoneInstalled),
        nameof(HasActiveInstalled),
        nameof(HasInactiveInstalled),
        nameof(HasInstalledCountBadge),
        nameof(InstalledCountText),
        nameof(InstalledSummaryTitle),
        nameof(InstalledSummaryDetail),
        nameof(HasInstalledSummaryDetail))]
    private IReadOnlyList<InstalledXcodeItemViewModel> _installed = [];

    public bool HasInstalled => Installed.Count > 0;
    public bool NoneInstalled => HasLoaded && Installed.Count == 0;
    public bool HasActiveInstalled => Installed.Any(item => item.IsSelected);
    public bool HasInactiveInstalled => HasInstalled && !HasActiveInstalled;
    public bool HasInstalledCountBadge => HasInstalled || HasLoaded;
    public string InstalledCountText => Installed.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public string InstalledSummaryTitle
    {
        get
        {
            if (Installed.Count == 0)
                return "";
            return Installed.Count == 1
                ? CoreTools.Translate("Xcode {0} installed", Installed[0].Version)
                : CoreTools.Translate("{0} versions installed", Installed.Count);
        }
    }

    public string InstalledSummaryDetail
    {
        get
        {
            if (Installed.Count == 0)
                return "";
            InstalledXcodeItemViewModel? active = Installed.FirstOrDefault(item => item.IsSelected);
            if (Installed.Count == 1)
            {
                InstalledXcodeItemViewModel only = Installed[0];
                return only.IsSelected
                    ? $"{CoreTools.Translate("Active")} · {only.Location}"
                    : only.Location;
            }
            return active is not null
                ? $"{CoreTools.Translate("Active: Xcode {0}", active.Version)} · {active.Location}"
                : string.Join(", ", Installed.Select(item => item.Title));
        }
    }

    public bool HasInstalledSummaryDetail => InstalledSummaryDetail.Length > 0;

    [RelayCommand]
    private void FocusInstalled()
    {
        if (Installed.Count == 0)
            return;

        InstalledXcodeItemViewModel target = Installed.FirstOrDefault(item => item.IsSelected) ?? Installed[0];
        bool wasShowingRuntimes = ShowSimulatorRuntimes;
        if (ShowSimulatorRuntimes)
            ShowSimulatorRuntimes = false;

        XcodeBrowserItemViewModel? match = BrowserItems.FirstOrDefault(
            item => item.IsInstalled && string.Equals(item.Location, target.Location, StringComparison.Ordinal))
            ?? BrowserItems.FirstOrDefault(item => item.IsInstalled);

        if (match is null)
        {
            SearchText = "";
            ReleaseFilterIndex = 0;
            ArchitectureFilterIndex = 0;
            RebuildBrowser();
            match = BrowserItems.FirstOrDefault(
                item => item.IsInstalled && string.Equals(item.Location, target.Location, StringComparison.Ordinal))
                ?? BrowserItems.FirstOrDefault(item => item.IsInstalled);
        }
        else if (!wasShowingRuntimes && SelectedBrowserItem is { IsInstalled: true })
        {
            string focusedPath = SelectedBrowserItem.Location;
            InstalledOnly = !InstalledOnly;
            match = BrowserItems.FirstOrDefault(
                item => item.IsInstalled && string.Equals(item.Location, focusedPath, StringComparison.Ordinal))
                ?? BrowserItems.FirstOrDefault(item => item.IsInstalled);
        }

        if (match is not null)
        {
            ExpandAncestorsFor(match.Key);
            SelectedBrowserItem = match;
        }

        ScrollToSelectedRequested?.Invoke();
    }

    private void ExpandAncestorsFor(string itemKey)
    {
        foreach (XcodeBrowserNodeViewModel major in BrowserGroups)
        {
            if (!ContainsKey(major, itemKey))
                continue;
            major.IsExpanded = true;
            foreach (XcodeBrowserNodeViewModel child in major.Children)
            {
                if (child.IsGroup && ContainsKey(child, itemKey))
                    child.IsExpanded = true;
            }
        }
    }

    private static bool ContainsKey(XcodeBrowserNodeViewModel node, string itemKey)
        => node.Key == itemKey || node.Children.Any(child => ContainsKey(child, itemKey));

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvailable), nameof(IsAvailableEmpty), nameof(AvailableEmptyText))]
    private IReadOnlyList<AvailableXcodeItemViewModel> _available = [];

    public bool HasAvailable => Available.Count > 0;
    public bool IsAvailableEmpty => Available.Count == 0;

    public string AvailableEmptyText
    {
        get
        {
            if (HasVersionLoadError)
                return CoreTools.Translate("Xcode versions are unavailable. Try checking for updates again.");
            if (!HasLoaded)
                return CoreTools.Translate("Reading the Xcode versions...");
            return SearchText.Trim().Length > 0
                ? CoreTools.Translate("No version matches your search")
                : CoreTools.Translate("There is no other version to install");
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AvailableEmptyText))]
    private string _searchText = "";

    /// <summary>Whether betas and release candidates are listed, and offered as the update.</summary>
    [ObservableProperty]
    private bool _includePrereleases;

    partial void OnSearchTextChanged(string value) => RebuildAvailable();

    partial void OnIncludePrereleasesChanged(bool value)
    {
        XcodePreferences.IncludePrereleases = value;
        RebuildAvailable();
        UpdateAvailableUpdate();
    }

    // The browser filters are independent of whether prereleases are offered as updates.
    [ObservableProperty]
    private bool _installedOnly;

    partial void OnInstalledOnlyChanged(bool value)
    {
        XcodePreferences.BrowserInstalledOnly = value;
        RebuildBrowser();
    }

    [ObservableProperty]
    private bool _groupVersions;

    partial void OnGroupVersionsChanged(bool value) => XcodePreferences.BrowserGrouped = value;

    public int ReleaseFilterIndex
    {
        get => _releaseFilterIndex;
        set
        {
            if (value is < 0 or > 2 || !SetProperty(ref _releaseFilterIndex, value)) return;
            XcodePreferences.BrowserReleaseFilter = value;
            RebuildBrowser();
        }
    }

    public int ArchitectureFilterIndex
    {
        get => _architectureFilterIndex;
        set
        {
            if (value is < 0 or > 3 || !SetProperty(ref _architectureFilterIndex, value)) return;
            XcodePreferences.BrowserArchitectureFilter = value;
            RebuildBrowser();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBrowserEmpty), nameof(BrowserEmptyText))]
    private IReadOnlyList<XcodeBrowserItemViewModel> _browserItems = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBrowserSelection))]
    private XcodeBrowserItemViewModel? _selectedBrowserItem;

    [ObservableProperty]
    private IReadOnlyList<XcodeBrowserNodeViewModel> _browserGroups = [];

    [ObservableProperty]
    private XcodeBrowserNodeViewModel? _selectedTreeNode;

    public bool HasBrowserSelection => SelectedBrowserItem is not null;
    public bool IsBrowserEmpty => BrowserItems.Count == 0;
    public string BrowserEmptyText => HasVersionLoadError
        ? CoreTools.Translate("Xcode versions are unavailable. Try checking for updates again.")
        : HasLoaded
            ? CoreTools.Translate("No Xcode versions match these filters.")
            : CoreTools.Translate("Reading the Xcode versions...");

    partial void OnSelectedBrowserItemChanged(XcodeBrowserItemViewModel? value)
    {
        if (!_rebuildingBrowser && value is not null)
            _preferredBrowserKey = value.Key;
        if (_rebuildingBrowser || _syncingTreeSelection) return;
        _syncingTreeSelection = true;
        try
        {
            SelectedTreeNode = value is not null && _browserNodes.TryGetValue(value.Key, out XcodeBrowserNodeViewModel? node)
                ? node : null;
        }
        finally
        {
            _syncingTreeSelection = false;
        }
    }

    partial void OnSelectedTreeNodeChanged(XcodeBrowserNodeViewModel? value)
    {
        if (_rebuildingBrowser || _syncingTreeSelection) return;
        _syncingTreeSelection = true;
        try
        {
            SelectedBrowserItem = value?.Item;
            if (value?.Item is { } item)
                _preferredBrowserKey = item.Key;
        }
        finally
        {
            _syncingTreeSelection = false;
        }
    }

    // ─── The update ──────────────────────────────────────────────────────────

    /// <summary>The release that is newer than every Xcode installed, when there is one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUpdate), nameof(UpdateCount), nameof(UpdateVersion), nameof(UpdateButtonText))]
    private XcodeRelease? _updateRelease;

    public bool HasUpdate => UpdateRelease is not null;

    /// <summary>Zero or one, shown on the sidebar.</summary>
    public int UpdateCount => UpdateRelease is null ? 0 : 1;

    public string UpdateVersion => UpdateRelease?.Version ?? "";
    public string UpdateButtonText => CoreTools.Translate("Update to Xcode {0}", UpdateVersion);

    private void UpdateAvailableUpdate()
        => UpdateRelease = XcodesService.FindUpdate(_releases, _installedXcodes, IncludePrereleases);

    // ─── The running installation ────────────────────────────────────────────

    /// <summary>The panel about an installation is shown: while it runs, and after it failed until it is closed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOperationFinished))]
    private bool _hasOperation;

    /// <summary>An installation or a selection is running; closing the app would cut it short.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOperationFinished), nameof(ShowBusyBar), nameof(CanOpenSimulatorRuntimes))]
    private bool _isOperationRunning;

    public bool IsOperationFinished => HasOperation && !IsOperationRunning;

    [ObservableProperty]
    private string _operationTitle = "";

    [ObservableProperty]
    private string _operationStatus = "";

    [ObservableProperty]
    private string _operationStep = "";

    [ObservableProperty]
    private double _operationProgress;

    [ObservableProperty]
    private bool _operationIsIndeterminate = true;

    [ObservableProperty]
    private string _operationLog = "";

    [ObservableProperty]
    private bool _isLogExpanded;

    [ObservableProperty]
    private bool _hasAccessRecovery;

    // ─── Lifetime ────────────────────────────────────────────────────────────

    private void ConfigureTimer()
    {
        int minutes = XcodePreferences.CheckIntervalMinutes;
        _checkTimer.Stop();
        if (minutes <= 0 || _stopped) return;
        _checkTimer.Interval = TimeSpan.FromMinutes(minutes);
        _checkTimer.Start();
    }

    /// <summary>Ends automatic checks and any installation, as when the Xcode extension is turned off.</summary>
    public void Stop()
    {
        _stopped = true;
        _checkTimer.Stop();
        _lifetime.Cancel();
        Account.PropertyChanged -= AccountPropertyChanged;
    }

    /// <summary>Picks up the choices made on its page under Settings &gt; Extensions.</summary>
    public void ReloadPreferences()
    {
        IncludePrereleases = XcodePreferences.IncludePrereleases;
        ConfigureTimer();
    }

    internal async Task<bool> ChangeInstallDirectoryAsync(string? directory)
    {
        bool changed = false;
        await RunExclusiveAsync(async () =>
        {
            if (!XcodePreferences.TrySetInstallDirectory(directory))
            {
                ShowNotice(CoreTools.Translate("Could not save the Xcode installation directory"),
                    CoreTools.Translate("Check the application log for details."), InfoBarSeverity.Error);
                return;
            }
            changed = true;
            _initialized = true;
            _installedXcodes = [];
            _releases = [];
            _preferredBrowserKey = null;
            _retryRelease = null;
            _retryReplacing = null;
            _retryDownloadTarget = null;
            HasAccessRecovery = false;
            HasLoaded = false;
            HasVersionLoadError = false;
            _lastVersionRefreshFailed = false;
            LastChecked = null;
            VersionListStatusText = "";
            Installed = [];
            RebuildAvailable();
            UpdateAvailableUpdate();
            await RefreshAsync(refresh: false, announce: true);
        });
        return changed;
    }

    internal async Task<bool> ChangeArchiveDirectoryAsync(string? directory)
    {
        bool changed = false;
        await RunExclusiveAsync(() =>
        {
            if (!XcodePreferences.TrySetArchiveDirectory(directory))
            {
                ShowNotice(CoreTools.Translate("Could not save the Xcode archive directory"),
                    CoreTools.Translate("Check the application log for details."), InfoBarSeverity.Error);
                return Task.CompletedTask;
            }
            changed = true;
            return Task.CompletedTask;
        }, refreshAccount: false);
        return changed;
    }

    /// <summary>
    /// Reads what is installed, and what Apple offers. With automatic checks on, it first
    /// downloads the list again. Runs once, at startup.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        await RunExclusiveAsync(async () =>
        {
            _initialized = true;
            bool refresh = XcodePreferences.CheckIntervalMinutes > 0;
            await RefreshAsync(refresh, announce: false);
            if (refresh)
                await InstallAutomaticUpdateAsync();
        });
    }

    internal async Task CheckAutomaticallyAsync()
        => await RunExclusiveAsync(async () =>
        {
            if (XcodePreferences.CheckIntervalMinutes <= 0)
                return;
            await RefreshAsync(refresh: true, announce: false);
            await InstallAutomaticUpdateAsync();
        });

    private bool AutomaticInstallEnabled
        => !_stopped && XcodePreferences.AutoInstallUpdates && XcodePreferences.CheckIntervalMinutes > 0;

    private async Task InstallAutomaticUpdateAsync()
    {
        if (!AutomaticInstallEnabled || !HasLoaded || _lastVersionRefreshFailed || UpdateRelease is not { } release)
            return;

        XcodeCompatibilityAssessment? compatibility =
            XcodeCatalogMatching.ForRelease(release, _catalogReleases, CurrentMacOS(), _catalogFresh)?.Compatibility;
        if (release.Build is null || release.Architecture == XcodeArchitecture.Unspecified
            || compatibility?.Status != XcodeCompatibility.Supported)
        {
            ShowNotice(CoreTools.Translate("Automatic Xcode update paused"),
                CoreTools.Translate("The update cannot start automatically because its build or macOS compatibility is unknown or unsupported."),
                InfoBarSeverity.Warning);
            return;
        }

        if (_releases.Any(candidate => XcodeVersion.Compare(candidate.Version, release.Version) == 0
            && (candidate.IsIdentityUnverified || string.IsNullOrEmpty(candidate.Build)
                || !string.Equals(candidate.Build, release.Build, StringComparison.OrdinalIgnoreCase))))
        {
            ShowNotice(CoreTools.Translate("Automatic Xcode update paused"),
                CoreTools.Translate("The available builds of Xcode {0} are ambiguous. Choose one in the version list before installing.", release.Version),
                InfoBarSeverity.Warning);
            return;
        }

        XcodeAccountStatus account = Account.RefreshForOperation();
        if (Account.HasWarning || account.AppleId is null)
        {
            ShowNotice(CoreTools.Translate("Automatic Xcode update paused"),
                Account.HasWarning ? Account.Warning : CoreTools.Translate("Sign in to Apple before automatic installation can begin."),
                InfoBarSeverity.Warning);
            return;
        }
        var attempt = (release.Version, release.Build, release.Architecture, account.AppleId);
        if (_lastAutomaticAttempt == attempt)
            return;

        XcodeAccountStatus verified;
        try
        {
            verified = await _xcodes.RefreshAccountAsync(_lifetime.Token);
        }
        catch (XcodesException ex)
        {
            Logger.Warn($"Could not verify the Apple session for an automatic Xcode update: {ex.Message}");
            ShowNotice(CoreTools.Translate("Automatic Xcode update paused"), ex.Message, InfoBarSeverity.Warning);
            return;
        }
        XcodeAccountStatus current = Account.RefreshForOperation();
        if (!AutomaticInstallEnabled)
            return;
        if (Account.HasWarning || !verified.IsSignedIn || !current.IsSignedIn
            || !string.Equals(account.AppleId, verified.AppleId, StringComparison.Ordinal)
            || !string.Equals(account.AppleId, current.AppleId, StringComparison.Ordinal))
        {
            ShowNotice(CoreTools.Translate("Automatic Xcode update paused"),
                Account.HasWarning ? Account.Warning : CoreTools.Translate("Verify the current Apple session before automatic installation can begin."),
                InfoBarSeverity.Warning);
            return;
        }

        _lastAutomaticAttempt = attempt;
        await CompleteInstallationAsync(release, _installedXcodes.FirstOrDefault(), XcodePreferences.InstallOptions, automatic: true);
    }

    /// <summary>Runs one thing at a time: reading, installing and removing all share the page.</summary>
    private async Task RunExclusiveAsync(Func<Task> work, bool refreshAccount = true)
    {
        if (_stopped || IsBusy || !Account.TryBeginOperation(refreshAccount)) return;
        IsBusy = true;
        try
        {
            await work();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Logger.Error("An Xcode command failed");
            Logger.Error(ex);
            ShowNotice(CoreTools.Translate("Something went wrong"), ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            IsBusy = false;
            Account.EndOperation(refreshAccount);
        }
    }

    // ─── Reading ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private Task CheckForUpdates() => RunExclusiveAsync(() => RefreshAsync(refresh: true, announce: true));

    [RelayCommand]
    private Task OpenSimulatorRuntimes()
    {
        if (!_stopped && IsOperationRunning && Runtimes.HasOperationTarget)
        {
            ShowSimulatorRuntimes = true;
            return Task.CompletedTask;
        }

        return RunExclusiveAsync(async () =>
        {
            Runtimes.ShowAllRuntimesCommand.Execute(null);
            ShowSimulatorRuntimes = true;
            await Runtimes.RefreshAsync(_lifetime.Token);
        }, refreshAccount: false);
    }

    [RelayCommand]
    private Task OpenMappedSimulatorRuntimes() => RunExclusiveAsync(async () =>
    {
        if (SelectedBrowserItem is not { } item)
            return;
        string title = item.IsInstalled ? $"{item.Title} - {item.Location}" : item.Title;
        Runtimes.ShowMappedFor(title, item.Details?.SDKIdentities ?? [], CatalogStatusText);
        ShowSimulatorRuntimes = true;
        await Runtimes.RefreshAsync(_lifetime.Token);
    }, refreshAccount: false);

    [RelayCommand]
    private Task RefreshSimulatorRuntimes()
        => RunExclusiveAsync(() => Runtimes.RefreshAsync(_lifetime.Token), refreshAccount: false);

    [RelayCommand]
    private void ShowXcodeVersions() => ShowSimulatorRuntimes = false;

    [RelayCommand]
    private Task InstallSimulatorRuntime(XcodeSimulatorRuntimeItemViewModel? item)
    {
        if (item is null)
            return Task.CompletedTask;

        return RunExclusiveAsync(async () =>
        {
            if (!Runtimes.CanInstall(item.Runtime))
            {
                ShowNotice(CoreTools.Translate("Cannot install this simulator runtime"),
                    CoreTools.Translate("Refresh the Apple simulator catalog and choose a supported runtime."),
                    InfoBarSeverity.Error);
                return;
            }

            XcodeSimulatorRuntimeInstallPlan plan;
            try
            {
                plan = await _runtimeInstaller.PrepareAsync(item.Runtime, _lifetime.Token);
            }
            catch (XcodesException ex)
            {
                ShowNotice(CoreTools.Translate("Cannot install this simulator runtime"),
                    RuntimeInstallReason(ex.Message), InfoBarSeverity.Error);
                return;
            }
            if (!await ConfirmRuntimeInstall(plan))
                return;

            XcodeSimulatorRuntimeInstallOutcome outcome = XcodeSimulatorRuntimeInstallOutcome.Installed;
            bool installed = await RunOperationAsync(
                CoreTools.Translate("Installing {0}", plan.Runtime.Name),
                CoreTools.Translate("Could not install {0}", plan.Runtime.Name),
                CoreTools.Translate("Cancellation requested for {0}", plan.Runtime.Name),
                async (_, output, cancellation) =>
                {
                    outcome = await _runtimeInstaller.InstallAsync(plan, output, cancellation);
                },
                usesXcodebuild: true,
                runtimeTarget: item.Runtime);

            await Runtimes.RefreshAsync(_lifetime.Token);
            if (installed)
                ShowNotice(
                    outcome == XcodeSimulatorRuntimeInstallOutcome.AlreadyAvailable
                        ? CoreTools.Translate("Simulator runtime is already available")
                        : CoreTools.Translate("Simulator runtime is ready"),
                    CoreTools.Translate("Developer tools: {0}", plan.DeveloperDirectory), InfoBarSeverity.Success);
        }, refreshAccount: false);
    }

    private static async Task<bool> ConfirmRuntimeInstallInWindowAsync(XcodeSimulatorRuntimeInstallPlan plan)
    {
        if (MainWindow.Instance is not { } owner)
            return false;
        ImmersiveConfirmationDialog dialog = CreateRuntimeInstallDialog(plan);
        await dialog.ShowDialog(owner);
        return dialog.Result is true;
    }

    internal static ImmersiveConfirmationDialog CreateRuntimeInstallDialog(XcodeSimulatorRuntimeInstallPlan plan)
    {
        var body = new StackPanel { Spacing = 12, MaxWidth = 520 };
        body.Children.Add(Paragraph(CoreTools.Translate(
            "Xcode will download and install {0}, build {1}, for {2}.",
            plan.Runtime.Name, plan.Runtime.Build!, XcodeText.Architecture(plan.Runtime.Architecture))));
        body.Children.Add(Paragraph(CoreTools.Translate("Developer tools: {0}", plan.DeveloperDirectory)));
        body.Children.Add(Paragraph(CoreTools.Translate(
            "Xcode manages this download and may require its own Apple account or administrator authorization. It does not use the Apple ID UniGetUI downloads Xcode with.")));
        body.Children.Add(Paragraph(CoreTools.Translate(
            "Stopping requests cancellation; macOS or Xcode may continue processing the runtime afterwards.")));
        return new ImmersiveConfirmationDialog(
            CoreTools.Translate("Install simulator runtime?"), XcodeDialogContent.Scroll(body),
            CoreTools.Translate("Install"), CoreTools.Translate("Cancel"))
        {
            FocusPrimaryButton = false,
        };
    }

    [RelayCommand]
    private Task ExportSimulatorRuntime(XcodeSimulatorRuntimeItemViewModel? item)
    {
        if (item is null)
            return Task.CompletedTask;

        return RunExclusiveAsync(async () =>
        {
            if (!Runtimes.CanInstall(item.Runtime))
            {
                ShowNotice(CoreTools.Translate("Cannot export this simulator runtime"),
                    CoreTools.Translate("Refresh the Apple simulator catalog and choose a supported runtime."),
                    InfoBarSeverity.Error);
                return;
            }

            XcodeSimulatorRuntimeInstallPlan plan;
            try
            {
                plan = await _runtimeInstaller.PrepareExportAsync(item.Runtime, _lifetime.Token);
            }
            catch (XcodesException ex)
            {
                ShowNotice(CoreTools.Translate("Cannot export this simulator runtime"),
                    RuntimeInstallReason(ex.Message), InfoBarSeverity.Error);
                return;
            }
            string? directory = await ChooseRuntimeExportFolder();
            if (directory is null || !await ConfirmRuntimeExport(plan, directory))
                return;

            XcodeSimulatorRuntimeExportPaths? paths = null;
            XcodeSimulatorRuntimeExportResult? result = null;
            bool exported = await RunOperationAsync(
                CoreTools.Translate("Exporting {0}", plan.Runtime.Name),
                CoreTools.Translate("Could not export {0}", plan.Runtime.Name),
                CoreTools.Translate("Cancellation requested for {0}", plan.Runtime.Name),
                async (_, output, cancellation) =>
                {
                    result = await _runtimeInstaller.ExportAsync(plan, directory, value => paths = value, output, cancellation);
                },
                usesXcodebuild: true,
                runtimeTarget: item.Runtime);

            if (!exported && paths is not null)
            {
                Notice.Message = CoreTools.Translate("{0} Temporary data may remain in {1}; an incomplete copy may remain at {2}.",
                    Notice.Message, paths.TemporaryDirectory, paths.PartialPath);
                string recoveryPaths = paths.TemporaryDirectory + Environment.NewLine + paths.PartialPath;
                Notice.ActionButtonText = CoreTools.Translate("Copy recovery paths");
                Notice.ActionButtonCommand = new AsyncRelayCommand(() => CopyPathAsync(recoveryPaths));
            }
            await Runtimes.RefreshAsync(_lifetime.Token);
            if (exported && result is not null)
            {
                string imagePath = result.ImagePath;
                string message = CoreTools.Translate("Saved to {0}", imagePath);
                if (result.RetainedTemporaryDirectory is { } retained)
                    message += " " + CoreTools.Translate("Temporary data remains in {0}.", retained);
                ShowNotice(CoreTools.Translate("Simulator runtime image exported"), message, InfoBarSeverity.Success);
                Notice.ActionButtonText = CoreTools.Translate("Copy path");
                Notice.ActionButtonCommand = new AsyncRelayCommand(() => CopyPathAsync(imagePath));
            }
        }, refreshAccount: false);
    }

    private static async Task<string?> ChooseRuntimeExportFolderInWindowAsync()
    {
        if (MainWindow.Instance is not { } owner)
            return null;
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = CoreTools.Translate("Choose where to save the simulator runtime image"),
            AllowMultiple = false,
        });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    private static async Task<bool> ConfirmRuntimeExportInWindowAsync(
        XcodeSimulatorRuntimeInstallPlan plan, string directory)
    {
        if (MainWindow.Instance is not { } owner)
            return false;
        ImmersiveConfirmationDialog dialog = CreateRuntimeExportDialog(plan, directory);
        await dialog.ShowDialog(owner);
        return dialog.Result is true;
    }

    internal static ImmersiveConfirmationDialog CreateRuntimeExportDialog(
        XcodeSimulatorRuntimeInstallPlan plan, string directory)
    {
        var body = new StackPanel { Spacing = 12, MaxWidth = 520 };
        body.Children.Add(Paragraph(CoreTools.Translate(
            "Xcode will export {0}, build {1}, for {2}. It may also install the runtime on this Mac.",
            plan.Runtime.Name, plan.Runtime.Build!, XcodeText.Architecture(plan.Runtime.Architecture))));
        body.Children.Add(Paragraph(CoreTools.Translate("Developer tools: {0}", plan.DeveloperDirectory)));
        body.Children.Add(Paragraph(CoreTools.Translate("Save the image in: {0}", directory)));
        body.Children.Add(Paragraph(CoreTools.Translate(
            "Xcode manages this download and may require its own Apple account or administrator authorization. It does not use the Apple ID UniGetUI downloads Xcode with.")));
        body.Children.Add(Paragraph(CoreTools.Translate(
            "The image is staged in the system temporary folder and copied without overwriting existing files. Allow space in both locations.")));
        body.Children.Add(Paragraph(CoreTools.Translate(
            "Xcode chooses the image; UniGetUI checks the copy but does not independently verify its build or signature.")));
        body.Children.Add(Paragraph(CoreTools.Translate(
            "Stopping requests cancellation; macOS or Xcode may continue processing, and temporary or incomplete files may remain.")));
        return new ImmersiveConfirmationDialog(
            CoreTools.Translate("Export simulator runtime?"), XcodeDialogContent.Scroll(body),
            CoreTools.Translate("Export"), CoreTools.Translate("Cancel"))
        {
            FocusPrimaryButton = false,
        };
    }

    [RelayCommand]
    private Task DownloadSimulatorRuntime(XcodeSimulatorRuntimeItemViewModel? item)
    {
        if (item is null)
            return Task.CompletedTask;

        return RunExclusiveAsync(async () =>
        {
            if (!Runtimes.CanDownload(item.Runtime))
            {
                ShowNotice(CoreTools.Translate("Cannot download this simulator runtime"),
                    CoreTools.Translate("Refresh the Apple simulator catalog and choose a supported runtime."),
                    InfoBarSeverity.Error);
                return;
            }

            string? directory = await ChooseRuntimeExportFolder();
            if (directory is null || !await ConfirmRuntimeDownload(item.Runtime, directory))
                return;

            XcodeSimulatorRuntimeDownloadPaths? paths = null;
            string? imagePath = null;
            bool downloaded = await RunOperationAsync(
                CoreTools.Translate("Downloading {0}", item.Runtime.Name),
                CoreTools.Translate("Could not download {0}", item.Runtime.Name),
                CoreTools.Translate("Cancellation requested for {0}", item.Runtime.Name),
                async (_, output, cancellation) =>
                {
                    imagePath = await _runtimeDownloader.DownloadAsync(
                        item.Runtime, directory, value => paths = value, output, cancellation);
                },
                downloadsPublicRuntime: true,
                runtimeTarget: item.Runtime);

            if (!downloaded && paths is not null)
            {
                string recovery = CoreTools.Translate("An incomplete download may remain at {0}.", paths.PartialPath);
                Notice.Message = string.IsNullOrEmpty(Notice.Message) ? recovery : Notice.Message + " " + recovery;
                Notice.ActionButtonText = CoreTools.Translate("Copy path");
                Notice.ActionButtonCommand = new AsyncRelayCommand(() => CopyPathAsync(paths.PartialPath));
            }
            if (downloaded && imagePath is not null)
            {
                ShowNotice(CoreTools.Translate("Simulator runtime image downloaded"),
                    CoreTools.Translate("Saved to {0}", imagePath) + " "
                    + CoreTools.Translate("Its build and Apple signature have not been independently verified."),
                    InfoBarSeverity.Success);
                Notice.ActionButtonText = CoreTools.Translate("Copy path");
                Notice.ActionButtonCommand = new AsyncRelayCommand(() => CopyPathAsync(imagePath));
            }
        }, refreshAccount: false);
    }

    private static async Task<bool> ConfirmRuntimeDownloadInWindowAsync(
        XcodeSimulatorRuntime runtime, string directory)
    {
        if (MainWindow.Instance is not { } owner)
            return false;
        ImmersiveConfirmationDialog dialog = CreateRuntimeDownloadDialog(runtime, directory);
        await dialog.ShowDialog(owner);
        return dialog.Result is true;
    }

    internal static ImmersiveConfirmationDialog CreateRuntimeDownloadDialog(
        XcodeSimulatorRuntime runtime, string directory)
    {
        string architecture = XcodeText.Architecture(runtime.Architecture) is { Length: > 0 } value
            ? value : CoreTools.Translate("Architecture unknown");
        var body = new StackPanel { Spacing = 12, MaxWidth = 520 };
        body.Children.Add(Paragraph(CoreTools.Translate(
            "Download {0}, build {1}, from Apple's public CDN. Architecture: {2}.",
            runtime.Name, runtime.Build!, architecture)));
        body.Children.Add(Paragraph(CoreTools.Translate("Save the image in: {0}", directory)));
        if (runtime.SizeBytes is { } bytes)
            body.Children.Add(Paragraph(CoreTools.Translate(
                "Apple's catalog reports approximately {0} MB; the actual download size may differ.",
                (bytes / 1_000_000d).ToString("N1", System.Globalization.CultureInfo.CurrentCulture))));
        body.Children.Add(Paragraph(CoreTools.Translate(
            "This download uses no Apple account and does not install the runtime.")));
        body.Children.Add(Paragraph(CoreTools.Translate(
            "UniGetUI does not independently verify the image's build or Apple signature. Compatibility with the selected Xcode is unknown.")));
        body.Children.Add(Paragraph(CoreTools.Translate(
            "Allow enough disk space. Cancelling may leave an incomplete file; the download cannot be resumed.")));
        return new ImmersiveConfirmationDialog(
            CoreTools.Translate("Download simulator runtime?"), XcodeDialogContent.Scroll(body),
            CoreTools.Translate("Download"), CoreTools.Translate("Cancel"))
        {
            FocusPrimaryButton = false,
        };
    }

    /// <summary>Reads what is installed again, with the saved list of versions.</summary>
    internal Task ReloadAsync() => RunExclusiveAsync(() => RefreshAsync(refresh: false, announce: false));

    private async Task RefreshAsync(bool refresh, bool announce)
    {
        await LoadAsync(refresh, announce, XcodePreferences.InstallDirectory);
    }

    /// <param name="refresh">Download the list of versions again instead of reading the saved copy.</param>
    /// <param name="announce">Say what was found, as after the button was pressed, rather than only updating the page.</param>
    private async Task LoadAsync(bool refresh, bool announce, string? installDirectory, bool reportLoadFailure = false)
    {
        CancellationToken cancellation = _lifetime.Token;
        await LoadSavedCatalogAsync(cancellation);
        string? refreshError = null;
        IReadOnlyList<InstalledXcode> installed;
        IReadOnlyList<XcodeRelease> releases;
        try
        {
            installed = await _xcodes.GetInstalledAsync(cancellation, installDirectory);
        }
        catch (XcodesException ex)
        {
            RecordVersionLoadFailure(null, VersionLoadFailureReason(ex),
                announce || reportLoadFailure || _installedXcodes.Count > 0 || _versionLoadNoticeShown, reportLoadFailure);
            return;
        }

        try
        {
            try
            {
                releases = await _xcodes.GetReleasesAsync(refresh, cancellation, installDirectory);
            }
            catch (XcodesException ex) when (refresh)
            {
                // Offline, or Xcode Releases could not be reached: the saved list is still worth showing.
                Logger.Warn($"Could not download the list of Xcode versions: {ex.Message}");
                refreshError = VersionLoadFailureReason(ex);
                _lastVersionRefreshFailed = true;
                releases = await _xcodes.GetReleasesAsync(false, cancellation, installDirectory);
            }
        }
        catch (XcodesException ex)
        {
            string reason = refreshError is null
                ? VersionLoadFailureReason(ex)
                : CoreTools.Translate("{0} The saved Xcode list could not be read: {1}", refreshError, VersionLoadFailureReason(ex));
            RecordVersionLoadFailure(installed, reason, showNotice: true, preserveNotice: reportLoadFailure);
            return;
        }

        string? catalogError = refresh ? await RefreshCatalogAsync(cancellation) : null;
        _installedXcodes = installed;
        _releases = releases;
        Installed = [.. installed.Select(xcode => new InstalledXcodeItemViewModel(xcode))];
        RebuildAvailable();
        UpdateAvailableUpdate();
        HasVersionLoadError = false;
        HasLoaded = true;
        if (_versionLoadNoticeShown)
        {
            Notice.IsOpen = false;
            _versionLoadNoticeShown = false;
        }
        if (refresh && refreshError is null)
        {
            _lastVersionRefreshFailed = false;
            LastChecked = DateTime.Now;
        }
        VersionListStatusText = _lastVersionRefreshFailed
            ? CoreTools.Translate("Could not check for new Xcode versions. Showing the saved list.")
            : refresh ? ""
            : LastChecked is null
                ? CoreTools.Translate("Showing saved Xcode versions; their age is unknown.")
                : CoreTools.Translate("Showing saved Xcode versions.");

        if (announce)
        {
            if (refreshError is null && catalogError is not null)
                ShowNotice(CoreTools.Translate("Could not refresh Xcode release details"), catalogError, InfoBarSeverity.Warning);
            else
                AnnounceResult(refreshError);
        }
    }

    private static string VersionLoadFailureReason(XcodesException error) => error.Message;

    private void RecordVersionLoadFailure(
        IReadOnlyList<InstalledXcode>? installed, string reason, bool showNotice, bool preserveNotice)
    {
        Logger.Warn($"Could not list the Xcode versions: {reason}");
        if (installed is not null)
            _installedXcodes = installed;
        _releases = [];
        Installed = [.. _installedXcodes.Select(xcode => new InstalledXcodeItemViewModel(xcode))];
        HasLoaded = false;
        HasVersionLoadError = true;
        LastChecked = null;
        VersionListStatusText = CoreTools.Translate("Xcode versions are unavailable. Try checking for updates again.");
        RebuildAvailable();
        UpdateAvailableUpdate();

        if (!showNotice)
            return;
        if (preserveNotice && Notice.IsOpen)
        {
            Notice.Message = CoreTools.Translate("{0} Could not refresh the Xcode versions: {1}", Notice.Message, reason).Trim();
            if (Notice.Severity != InfoBarSeverity.Error)
                Notice.Severity = InfoBarSeverity.Warning;
        }
        else
        {
            ShowNotice(CoreTools.Translate("Could not read the Xcode versions"), reason, InfoBarSeverity.Error);
            _versionLoadNoticeShown = true;
        }
    }

    private async Task LoadSavedCatalogAsync(CancellationToken cancellation)
    {
        if (_catalogCacheLoaded || _catalog is not IXcodeReleasesCatalogCache cache) return;

        try
        {
            XcodeSavedReleaseCatalog? saved = await cache.GetSavedAsync(cancellation);
            _catalogCacheLoaded = true;
            if (saved is null || _catalogChecked is not null) return;
            _catalogReleases = saved.Releases;
            _catalogChecked = saved.SavedAtUtc.ToLocalTime();
            _catalogFromSavedFile = true;
            SetStaleCatalogStatus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or XcodesException)
        {
            _catalogCacheLoaded = true;
            Logger.Warn($"Could not read saved Xcode Releases details: {ex.Message}");
            SetStaleCatalogStatus();
        }
    }

    private async Task<string?> RefreshCatalogAsync(CancellationToken cancellation)
    {
        if (_catalog is null) return null;

        try
        {
            _catalogReleases = await _catalog.GetAsync(cancellation);
            _catalogFresh = true;
            _catalogFromSavedFile = false;
            _catalogChecked = DateTime.Now;
            CatalogStatusText = CoreTools.Translate("Release details from Xcode Releases, refreshed at {0}.",
                _catalogChecked.Value.ToString("g", System.Globalization.CultureInfo.CurrentCulture));
            return null;
        }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        {
            return CatalogRefreshFailed(CoreTools.Translate("The request timed out."));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or XcodesException)
        {
            Logger.Warn($"Could not refresh Xcode Releases metadata: {ex.Message}");
            return CatalogRefreshFailed(ex.Message);
        }
    }

    private string CatalogRefreshFailed(string reason)
    {
        SetStaleCatalogStatus();
        return reason;
    }

    private void SetStaleCatalogStatus()
    {
        _catalogFresh = false;
        if (_catalogChecked is not { } checkedAt)
        {
            CatalogStatusText = CoreTools.Translate("Release details are unavailable.");
            return;
        }

        string time = checkedAt.ToString("g", System.Globalization.CultureInfo.CurrentCulture);
        CatalogStatusText = _catalogFromSavedFile
            ? CoreTools.Translate("Saved Xcode Releases details may be out of date; last saved {0}.", time)
            : CoreTools.Translate("Release details may be out of date; last refreshed {0}.", time);
    }

    private void AnnounceResult(string? refreshError)
    {
        string newest = _installedXcodes.Count > 0 ? _installedXcodes[0].Version : "";
        if (refreshError is not null)
            ShowNotice(CoreTools.Translate("Could not check for new versions"),
                CoreTools.Translate("The saved list is shown instead. {0}", refreshError),
                InfoBarSeverity.Warning);
        else if (UpdateRelease is { } update)
            ShowNotice(CoreTools.Translate("Xcode {0} is available", update.Version),
                CoreTools.Translate("The newest Xcode you have is {0}.", newest), InfoBarSeverity.Informational);
        else if (_installedXcodes.Count == 0)
            ShowNotice(CoreTools.Translate("No Xcode is installed yet"),
                CoreTools.Translate("Choose a version from the list below to install it."), InfoBarSeverity.Informational);
        else
            ShowNotice(CoreTools.Translate("Xcode is up to date"),
                CoreTools.Translate("No release newer than Xcode {0} was found.", newest), InfoBarSeverity.Success);
    }

    private void RebuildAvailable()
    {
        string search = SearchText.Trim();
        if (search.StartsWith("xcode", StringComparison.OrdinalIgnoreCase))
            search = search[5..].Trim();

        Available = [.. AvailableBuildGroups()
            .Select(group => group[0])
            .Where(release => IncludePrereleases || !release.IsPrerelease)
            .Where(release => search.Length == 0 || Matches(release, search))
            .Select(release => new AvailableXcodeItemViewModel(release))];
        RebuildBrowser();
    }

    private IEnumerable<IReadOnlyList<XcodeRelease>> AvailableBuildGroups()
        => _releases.Where(release => !IsInstalled(release))
            .GroupBy(release => release.Version, StringComparer.OrdinalIgnoreCase)
            .SelectMany(group => group.GroupBy(release => release.Build, StringComparer.Ordinal))
            .Select(group => (IReadOnlyList<XcodeRelease>)[.. group]);

    private IReadOnlyList<XcodeRelease> PreferFilteredArchitecture(IReadOnlyList<XcodeRelease> releases)
    {
        XcodeArchitecture? architecture = ArchitectureFilterIndex switch
        {
            1 => XcodeArchitecture.AppleSilicon,
            2 => XcodeArchitecture.Universal,
            3 => XcodeArchitecture.Intel,
            _ => null,
        };
        return architecture is null ? releases
            : [.. releases.OrderByDescending(release => release.Architecture == architecture)];
    }

    private void RebuildBrowser()
    {
        string search = SearchText.Trim();
        if (search.StartsWith("xcode", StringComparison.OrdinalIgnoreCase))
            search = search[5..].Trim();

        Version current = CurrentMacOS();
        IEnumerable<XcodeBrowserItemViewModel> installed =
            Installed.Select(item => new XcodeBrowserItemViewModel(item,
                XcodeCatalogMatching.ForInstalled(item.Xcode, _catalogReleases, current, _catalogFresh),
                XcodesService.FindStableCounterpart(item.Xcode, _releases, _installedXcodes),
                FindInstalledDownload(item.Xcode)));
        IEnumerable<XcodeBrowserItemViewModel> available = AvailableBuildGroups()
            .Select(PreferFilteredArchitecture)
            .Select(group => new XcodeBrowserItemViewModel(group,
                XcodeCatalogMatching.ForAvailable(group[0].Version, group,
                    [.. _catalogReleases.Where(candidate => string.Equals(candidate.Build, group[0].Build, StringComparison.Ordinal))],
                    current, _catalogFresh)));
        var items = installed.Concat(available)
            .Where(item => !InstalledOnly || item.IsInstalled)
            .Where(item => ReleaseFilterIndex switch
            {
                1 => !item.IsPrerelease,
                2 => item.IsPrerelease,
                _ => true,
            })
            .Where(item => item.MatchesArchitecture(ArchitectureFilterIndex) && item.Matches(search))
            .OrderByDescending(item => item.Version, Comparer<string>.Create(XcodeVersion.Compare))
            .ThenByDescending(item => item.IsInstalled)
            .ThenBy(item => item.Location, StringComparer.Ordinal)
            .ToArray();

        string? previousVersion = SelectedBrowserItem?.Version;
        _rebuildingBrowser = true;
        try
        {
            foreach (XcodeBrowserItemViewModel item in BrowserItems)
                item.SetOperationTarget(false);
            foreach (XcodeBrowserItemViewModel item in items)
                item.SetOperationTarget(_browserOperationTarget?.Matches(item) == true);
            BrowserItems = items;
            BrowserGroups = CreateBrowserGroups(items);
            SelectedBrowserItem = items.FirstOrDefault(item => item.Key == _preferredBrowserKey)
                ?? items.FirstOrDefault(item => item.Version == previousVersion)
                ?? items.FirstOrDefault(item => item.IsActive)
                ?? items.FirstOrDefault();
            SelectedTreeNode = SelectedBrowserItem is { } selected && _browserNodes.TryGetValue(selected.Key, out var node)
                ? node : null;
            _preferredBrowserKey ??= SelectedBrowserItem?.Key;
        }
        finally
        {
            _rebuildingBrowser = false;
        }
    }

    private IReadOnlyList<XcodeBrowserNodeViewModel> CreateBrowserGroups(IReadOnlyList<XcodeBrowserItemViewModel> items)
    {
        _browserNodes.Clear();
        return [.. items
            .GroupBy(item => XcodeVersion.TryParse(item.Version, out XcodeVersion version) ? version.Major : -1)
            .Select(major => BrowserGroup(
                FormattableString.Invariant($"major:{major.Key}"),
                major.Key < 0 ? CoreTools.Translate("Other versions") : CoreTools.Translate("Xcode {0}", major.Key),
                major.Key < 0
                    ? [.. major.Select(BrowserLeaf)]
                    : [.. major
                        .GroupBy(item => XcodeVersion.TryParse(item.Version, out XcodeVersion version) ? version.Minor : 0)
                        .Select(minor => BrowserGroup(
                            FormattableString.Invariant($"minor:{major.Key}.{minor.Key}"),
                            FormattableString.Invariant($"{major.Key}.{minor.Key}"),
                            [.. minor.Select(BrowserLeaf)]))]))];
    }

    private XcodeBrowserNodeViewModel BrowserGroup(
        string key, string title, IReadOnlyList<XcodeBrowserNodeViewModel> children)
        => new(key, title, children, !_collapsedBrowserGroups.Contains(key), SetBrowserGroupExpanded);

    private XcodeBrowserNodeViewModel BrowserLeaf(XcodeBrowserItemViewModel item)
    {
        var node = new XcodeBrowserNodeViewModel(item);
        _browserNodes[item.Key] = node;
        return node;
    }

    private void SetBrowserGroupExpanded(string key, bool expanded)
    {
        bool changed = expanded ? _collapsedBrowserGroups.Remove(key) : _collapsedBrowserGroups.Add(key);
        if (changed)
            XcodePreferences.SaveBrowserCollapsedGroups(_collapsedBrowserGroups);
    }

    private bool IsInstalled(XcodeRelease release)
        => !release.IsIdentityUnverified && (release.IsInstalled
            || (release.Build is { Length: > 0 } build && _installedXcodes.Any(xcode =>
                string.Equals(xcode.Build, build, StringComparison.Ordinal)
                && (xcode.Architecture == XcodeArchitecture.Unspecified || xcode.Architecture == release.Architecture))));

    private XcodeRelease? FindInstalledDownload(InstalledXcode xcode)
        => string.IsNullOrEmpty(xcode.Build) || xcode.Architecture == XcodeArchitecture.Unspecified
            ? null : _releases.FirstOrDefault(release => !release.IsIdentityUnverified
                && string.Equals(release.Version, xcode.Version, StringComparison.OrdinalIgnoreCase)
                && string.Equals(release.Build, xcode.Build, StringComparison.Ordinal)
                && release.Architecture == xcode.Architecture);

    private static bool Matches(XcodeRelease release, string search)
        => release.Version.Contains(search, StringComparison.OrdinalIgnoreCase)
           || release.Build?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;

    private static bool MatchesIdentity(XcodeRelease candidate, XcodeRelease release)
        => !candidate.IsIdentityUnverified
           && string.Equals(candidate.Version, release.Version, StringComparison.OrdinalIgnoreCase)
           && string.Equals(candidate.Build, release.Build, StringComparison.Ordinal)
           && candidate.Architecture == release.Architecture;

    // ─── Installing and updating ─────────────────────────────────────────────

    [RelayCommand]
    private Task Install(AvailableXcodeItemViewModel? item)
        => item is null ? Task.CompletedTask : InstallVersionAsync(item.Release, replacing: null);

    /// <summary>Installs the newest release next to the Xcode that is there; that one is kept until it is removed.</summary>
    [RelayCommand]
    private Task UpdateXcode()
        => UpdateRelease is { } release
            ? InstallVersionAsync(release, replacing: _installedXcodes.FirstOrDefault())
            : Task.CompletedTask;

    [RelayCommand]
    private Task DownloadXcode(XcodeBrowserItemViewModel? item)
        => item?.DownloadRelease is { } release
            ? DownloadVersionAsync(release, item.IsInstalled
                ? new BrowserOperationTarget(item.Location, IsInstalled: true)
                : new BrowserOperationTarget(item.Key, IsInstalled: false))
            : Task.CompletedTask;

    private Task DownloadVersionAsync(XcodeRelease release, BrowserOperationTarget target) => RunExclusiveAsync(async () =>
    {
        if (!HasLoaded)
        {
            ShowNotice(CoreTools.Translate("Could not read the Xcode versions"),
                CoreTools.Translate("Read the Xcode versions before starting a download."), InfoBarSeverity.Error);
            return;
        }
        if (release.IsIdentityUnverified || !_releases.Any(item => MatchesIdentity(item, release)))
        {
            ShowNotice(CoreTools.Translate("Could not download Xcode {0}", release.Version),
                CoreTools.Translate("This build is no longer listed as available. Check for updates before trying again."),
                InfoBarSeverity.Warning);
            return;
        }

        string? archiveDirectory = XcodePreferences.ArchiveDirectory;
        string? appleId = Account.RefreshForOperation().AppleId;
        if (Account.HasWarning)
        {
            ShowNotice(CoreTools.Translate("Could not read the saved Apple ID"), Account.Warning, InfoBarSeverity.Warning);
            return;
        }
        if (!await ConfirmDownload(release))
            return;

        string? currentAppleId = Account.RefreshForOperation().AppleId;
        if (Account.HasWarning || !string.Equals(appleId, currentAppleId, StringComparison.Ordinal))
        {
            ShowNotice(CoreTools.Translate("Apple ID changed"),
                CoreTools.Translate("Review the current Apple ID before trying the download again."),
                InfoBarSeverity.Warning);
            return;
        }

        string? archive = null;
        bool downloaded = await RunOperationAsync(
            CoreTools.Translate("Downloading Xcode {0}", release.Version),
            CoreTools.Translate("Could not download Xcode {0}", release.Version),
            CoreTools.Translate("The download of Xcode {0} was cancelled", release.Version),
            async (prompts, output, cancellation) =>
                archive = await _xcodes.DownloadAsync(release, prompts, output, cancellation, archiveDirectory),
            isAppleDownload: true, browserTarget: target);

        if (!downloaded && HasAccessRecovery)
        {
            _retryRelease = release;
            _retryDownloadTarget = target;
        }
        if (downloaded && archive is not null)
        {
            ShowNotice(CoreTools.Translate("Xcode archive downloaded"),
                CoreTools.Translate("Saved to {0}", archive) + " "
                + CoreTools.Translate("The archive's Apple signature and build are checked during installation, not during this download."),
                InfoBarSeverity.Success);
            Notice.ActionButtonText = CoreTools.Translate("Show in Finder");
            Notice.ActionButtonCommand = new AsyncRelayCommand(() => RevealDownloadedArchiveAsync(archive));
        }
    });

    private Task InstallVersionAsync(XcodeRelease release, InstalledXcode? replacing) => RunExclusiveAsync(async () =>
    {
        if (!HasLoaded)
        {
            ShowNotice(CoreTools.Translate("Could not read the Xcode versions"),
                CoreTools.Translate("Read the Xcode versions before starting an installation."), InfoBarSeverity.Error);
            return;
        }
        if (release.IsIdentityUnverified)
        {
            ShowNotice(CoreTools.Translate("Could not install Xcode {0}", release.Version),
                CoreTools.Translate("The exact build is not yet known. Check again after release details are available."),
                InfoBarSeverity.Warning);
            return;
        }
        if (!_releases.Any(item => MatchesIdentity(item, release) && !IsInstalled(item)))
        {
            ShowNotice(CoreTools.Translate("Could not install Xcode {0}", release.Version),
                CoreTools.Translate("This build is no longer listed as available. Check for updates before trying again."),
                InfoBarSeverity.Error);
            return;
        }

        XcodeInstallOptions options = XcodePreferences.InstallOptions;
        string? catalogError = await RefreshCatalogAsync(_lifetime.Token);
        RebuildBrowser();
        if (catalogError is not null)
            ShowNotice(CoreTools.Translate("Could not refresh Xcode release details"), catalogError, InfoBarSeverity.Warning);

        Version current = CurrentMacOS();
        XcodeCompatibilityAssessment compatibility =
            XcodeCatalogMatching.ForRelease(release, _catalogReleases, current, _catalogFresh)?.Compatibility
            ?? new XcodeCompatibilityAssessment(XcodeCompatibility.Unknown, null, current);
        string? appleId = Account.RefreshForOperation().AppleId;
        if (Account.HasWarning)
        {
            ShowNotice(CoreTools.Translate("Could not read the saved Apple ID"), Account.Warning, InfoBarSeverity.Warning);
            return;
        }
        if (!await ConfirmInstall(release, replacing, compatibility, options))
            return;

        string? currentAppleId = Account.RefreshForOperation().AppleId;
        if (Account.HasWarning || !string.Equals(appleId, currentAppleId, StringComparison.Ordinal))
        {
            ShowNotice(CoreTools.Translate("Apple ID changed"),
                CoreTools.Translate("Review the current Apple ID before trying the installation again."),
                InfoBarSeverity.Warning);
            return;
        }

        await CompleteInstallationAsync(release, replacing, options, automatic: false);
    });

    private async Task CompleteInstallationAsync(
        XcodeRelease release, InstalledXcode? replacing, XcodeInstallOptions options, bool automatic)
    {
        XcodeInstallationResult? result = null;
        bool installed = await RunOperationAsync(
            CoreTools.Translate("Installing Xcode {0}", release.Version),
            CoreTools.Translate("Could not install Xcode {0}", release.Version),
            CoreTools.Translate("The installation of Xcode {0} was cancelled", release.Version),
            async (prompts, output, cancellation) =>
                result = await _xcodes.InstallAsync(release, options, automatic ? null : prompts, output, cancellation),
            isInstallation: true,
            browserTarget: new BrowserOperationTarget(XcodeBrowserItemViewModel.AvailableKey(release), IsInstalled: false));

        // Whatever happened, what is on the disk now is what the page should show.
        await LoadAsync(refresh: false, announce: false, installDirectory: options.InstallDirectory, reportLoadFailure: true);
        if (!installed && HasAccessRecovery)
        {
            _retryRelease = release;
            _retryReplacing = replacing;
        }
        if (installed)
        {
            if (HasLoaded)
                AnnounceInstalled(release, result, replacing, options.Select);
            else
                ShowCompletionWithFailedRefresh(CoreTools.Translate("Finished installing Xcode {0}", release.Version));
        }
    }

    [RelayCommand]
    private Task RetryInstallation()
        => _retryRelease is { } release && CanRetryInstallation
            ? _retryDownloadTarget is { } target
                ? DownloadVersionAsync(release, target)
                : InstallVersionAsync(release, _retryReplacing)
            : Task.CompletedTask;

    [RelayCommand]
    private void DismissAccessRecovery() => HasAccessRecovery = false;

    private void AnnounceInstalled(
        XcodeRelease release, XcodeInstallationResult? result, InstalledXcode? replaced, bool selectRequested)
    {
        InstalledXcodeItemViewModel? installed = result is null || string.IsNullOrEmpty(result.Build)
            || (release.Build is not null && !string.Equals(release.Build, result.Build, StringComparison.OrdinalIgnoreCase))
            || (release.Architecture != XcodeArchitecture.Unspecified && release.Architecture != result.Architecture)
            ? null
            : Installed.FirstOrDefault(xcode =>
                string.Equals(xcode.Location, result.Path, StringComparison.Ordinal)
                && string.Equals(xcode.Xcode.Build, result.Build, StringComparison.OrdinalIgnoreCase)
                && xcode.Xcode.Architecture == result.Architecture
                && XcodeVersion.SameProductVersion(xcode.Version, release.Version));
        if (installed is null)
        {
            ShowNotice(CoreTools.Translate("Finished installing Xcode {0}", release.Version),
                CoreTools.Translate("The installation finished, but this Xcode could not be confirmed in the installation directory. Refresh the list and check the folder before trying again."),
                InfoBarSeverity.Warning);
            return;
        }

        if (selectRequested && !installed.IsSelected)
        {
            ShowNotice(CoreTools.Translate("Xcode {0} was installed", release.Version),
                CoreTools.Translate("The installation finished, but UniGetUI could not confirm an installed copy was selected for command-line tools."),
                InfoBarSeverity.Warning);
            return;
        }

        InstalledXcodeItemViewModel? previous = replaced is null || string.IsNullOrEmpty(replaced.Build)
            ? null
            : Installed.FirstOrDefault(xcode => string.Equals(xcode.Version, replaced.Version, StringComparison.OrdinalIgnoreCase)
                                                && string.Equals(xcode.Location, replaced.Path, StringComparison.Ordinal)
                                                && string.Equals(xcode.Xcode.Build, replaced.Build, StringComparison.Ordinal)
                                                && xcode.Xcode.Architecture == replaced.Architecture
                                                && !ReferenceEquals(xcode, installed));

        ShowNotice(CoreTools.Translate("Xcode {0} was installed", release.Version),
            previous is not null
                ? CoreTools.Translate("Xcode {0} is still installed.", previous.Version)
                : installed.Location,
            InfoBarSeverity.Success);

        if (previous is not null)
        {
            Notice.ActionButtonText = CoreTools.Translate("Show Xcode {0} in Finder", previous.Version);
            Notice.ActionButtonCommand = new AsyncRelayCommand(() => RevealInFinderAsync(previous));
        }
    }

    private void ShowCompletionWithFailedRefresh(string title)
    {
        string reason = Notice.Message;
        ShowNotice(title, CoreTools.Translate("The Xcode versions could not be refreshed: {0}", reason), InfoBarSeverity.Warning);
        _versionLoadNoticeShown = true;
    }

    private async Task<bool> ConfirmInstallInWindowAsync(
        XcodeRelease release, InstalledXcode? replacing, XcodeCompatibilityAssessment compatibility, XcodeInstallOptions options)
        => MainWindow.Instance is { } owner
            && await ConfirmInstallAsync(release, replacing, compatibility, options, Account.Status, Account.Warning, owner);

    private static async Task<bool> ConfirmInstallAsync(
        XcodeRelease release, InstalledXcode? replacing, XcodeCompatibilityAssessment compatibility,
        XcodeInstallOptions options, string accountStatus, string accountWarning, MainWindow owner)
    {
        ImmersiveConfirmationDialog dialog = CreateInstallDialog(
            release, replacing, compatibility, options, accountStatus, accountWarning);
        await dialog.ShowDialog(owner);
        return dialog.Result is true;
    }

    internal static ImmersiveConfirmationDialog CreateInstallDialog(
        XcodeRelease release, InstalledXcode? replacing, XcodeCompatibilityAssessment compatibility, XcodeInstallOptions options,
        string? accountStatus = null, string? accountWarning = null)
    {
        var body = new StackPanel { Spacing = 12, MaxWidth = 520 };
        if (compatibility.Status == XcodeCompatibility.Unsupported)
            body.Children.Add(Warning(CoreTools.Translate(
                "The selected Xcode requires macOS {0} or later. This Mac is running macOS {1}.",
                XcodeBrowserItemViewModel.DisplayVersion(compatibility.RequiredMacOS!),
                XcodeBrowserItemViewModel.DisplayVersion(compatibility.CurrentMacOS))));
        else if (compatibility.Status == XcodeCompatibility.Unknown)
            body.Children.Add(Warning(CoreTools.Translate(
                "Compatibility with this macOS version is unknown.")));
        if (accountStatus is not null)
        {
            body.Children.Add(Paragraph(accountStatus));
            if (!string.IsNullOrEmpty(accountWarning))
                body.Children.Add(Warning(accountWarning));
        }
        if (release.IsPrerelease)
            body.Children.Add(Warning(CoreTools.Translate("This is a pre-release version of Xcode, meant for testing.")));
        if (XcodeText.Details(release.Build, release.Architecture) is { Length: > 0 } details)
            body.Children.Add(Paragraph(details));
        body.Children.Add(Paragraph(CoreTools.Translate(
            "Xcode is downloaded from Apple and unpacked next to the versions you already have. It is several gigabytes, so this can take a while.")));
        body.Children.Add(Paragraph(CoreTools.Translate(
            "You may be asked for your Apple ID, a verification code and your macOS password. UniGetUI keeps the Apple ID password in its own item of your login keychain, and never keeps the macOS password.")));
        if (replacing is not null)
            body.Children.Add(Paragraph(CoreTools.Translate(
                "Xcode {0} is kept, so nothing is lost if the new version does not suit you. You can uninstall it afterwards.",
                replacing.Version)));
        if (options.Select)
            body.Children.Add(Paragraph(CoreTools.Translate("It will become the Xcode that command line tools use.")));

        return new ImmersiveConfirmationDialog(
            replacing is null
                ? CoreTools.Translate("Install Xcode {0}?", release.Version)
                : CoreTools.Translate("Update to Xcode {0}?", release.Version),
            XcodeDialogContent.Scroll(body),
            compatibility.Status == XcodeCompatibility.Unsupported
                ? CoreTools.Translate("Install Anyway")
                : replacing is null ? CoreTools.Translate("Install") : CoreTools.Translate("Update"),
            CoreTools.Translate("Cancel"))
        {
            FocusPrimaryButton = compatibility.Status != XcodeCompatibility.Unsupported,
        };
    }

    private async Task<bool> ConfirmDownloadInWindowAsync(XcodeRelease release)
    {
        if (MainWindow.Instance is not { } owner)
            return false;
        ImmersiveConfirmationDialog dialog = CreateDownloadDialog(release, Account.Status, Account.Warning);
        await dialog.ShowDialog(owner);
        return dialog.Result is true;
    }

    internal static ImmersiveConfirmationDialog CreateDownloadDialog(
        XcodeRelease release, string? accountStatus = null, string? accountWarning = null)
    {
        var body = new StackPanel { Spacing = 12, MaxWidth = 520 };
        if (accountStatus is not null)
        {
            body.Children.Add(Paragraph(accountStatus));
            if (!string.IsNullOrEmpty(accountWarning))
                body.Children.Add(Warning(accountWarning));
        }
        if (release.IsPrerelease)
            body.Children.Add(Warning(CoreTools.Translate("This is a pre-release version of Xcode, meant for testing.")));
        if (XcodeText.Details(release.Build, release.Architecture) is { Length: > 0 } details)
            body.Children.Add(Paragraph(details));
        body.Children.Add(Paragraph(CoreTools.Translate(
            "Download the archive from Apple to UniGetUI's cache without installing Xcode. It may be several gigabytes.")));
        body.Children.Add(Paragraph(CoreTools.Translate(
            "A catalog checksum is verified when available. The archive's Apple signature and build are checked during installation, not during this download.")));
        body.Children.Add(Paragraph(CoreTools.Translate(
            "The cached archive may be removed by cache cleanup or by the delete-download setting after an installation.")));
        body.Children.Add(Paragraph(CoreTools.Translate(
            "You may be asked for your Apple ID and a verification code. UniGetUI keeps the Apple ID password in its own login-keychain item.")));
        return new ImmersiveConfirmationDialog(
            CoreTools.Translate("Download Xcode {0}?", release.Version),
            XcodeDialogContent.Scroll(body), CoreTools.Translate("Download"), CoreTools.Translate("Cancel"))
        {
            FocusPrimaryButton = false,
        };
    }

    // ─── Selecting ───────────────────────────────────────────────────────────

    /// <summary>Makes an installed Xcode the one command line tools use.</summary>
    [RelayCommand]
    private Task Select(InstalledXcodeItemViewModel? item)
    {
        if (item is not { CanSelect: true })
            return Task.CompletedTask;

        return RunExclusiveAsync(async () =>
        {
            string? installDirectory = XcodePreferences.InstallDirectory;
            bool selected = await RunOperationAsync(
                CoreTools.Translate("Selecting Xcode {0}", item.Version),
                CoreTools.Translate("Could not select Xcode {0}", item.Version),
                CoreTools.Translate("Selecting Xcode {0} was cancelled", item.Version),
                (prompts, output, cancellation) => _xcodes.SelectAsync(item.Xcode, output, cancellation),
                browserTarget: new BrowserOperationTarget(item.Location, IsInstalled: true));

            await LoadAsync(refresh: false, announce: false, installDirectory: installDirectory, reportLoadFailure: true);
            if (selected)
            {
                if (HasLoaded)
                {
                    bool confirmed = _installedXcodes.Any(xcode => xcode.IsSelected
                        && string.Equals(xcode.Path, item.Location, StringComparison.Ordinal)
                        && string.Equals(xcode.Version, item.Version, StringComparison.OrdinalIgnoreCase)
                        && (item.Xcode.Build is null || string.Equals(xcode.Build, item.Xcode.Build, StringComparison.Ordinal)));
                    ShowNotice(
                        confirmed
                            ? CoreTools.Translate("Xcode {0} is now the selected Xcode", item.Version)
                            : CoreTools.Translate("Finished selecting Xcode {0}", item.Version),
                        confirmed
                            ? CoreTools.Translate("Command line tools such as xcodebuild use it.")
                            : CoreTools.Translate("Selecting finished, but the selected Xcode path could not be confirmed. Refresh the list before trying again."),
                        confirmed ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
                }
                else
                    ShowCompletionWithFailedRefresh(CoreTools.Translate("Finished selecting Xcode {0}", item.Version));
            }
        });
    }

    // ─── Running operations ──────────────────────────────────────────────────────

    /// <summary>
    /// Runs an operation, keeping its output on screen when it fails.
    /// </summary>
    private async Task<bool> RunOperationAsync(
        string title,
        string failureTitle,
        string cancelledTitle,
        Func<XcodesPromptHandler, Action<XcodesOutputLine>, CancellationToken, Task> run,
        bool isInstallation = false,
        bool isAppleDownload = false,
        bool usesXcodebuild = false,
        bool downloadsPublicRuntime = false,
        BrowserOperationTarget? browserTarget = null,
        XcodeSimulatorRuntime? runtimeTarget = null)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operationCancellation = cancellation;
        StartOperation(title, isInstallation, usesXcodebuild, downloadsPublicRuntime, browserTarget, runtimeTarget, cancellation);
        try
        {
            try
            {
                await run((prompt, token) => AskAsync(cancellation, prompt, token),
                    line => QueueOutput(cancellation, line), cancellation.Token);
            }
            finally
            {
                // What the operation wrote last is shown before the outcome is reported.
                ShowPendingOutput();
            }
            HasOperation = false;
            return true;
        }
        catch (OperationCanceledException)
        {
            HasOperation = false;
            ShowNotice(cancelledTitle, isInstallation
                ? CoreTools.Translate("An Xcode folder may have been created. Check it before retrying.")
                : isAppleDownload
                    ? CoreTools.Translate("A partial archive may be kept so the download can resume when it can be verified.")
                : usesXcodebuild
                    ? CoreTools.Translate("macOS or Xcode may continue processing the runtime. Refresh the list to check.")
                    : "", InfoBarSeverity.Informational);
            return false;
        }
        catch (HttpRequestException ex) when (downloadsPublicRuntime)
        {
            Logger.Warn($"Apple runtime download failed: {ex.Message}");
            string reason = ex.StatusCode is { } status
                ? CoreTools.Translate("The Apple runtime download returned HTTP {0}.", (int)status)
                : CoreTools.Translate("Could not connect to Apple: {0}", ex.Message);
            FailOperation(failureTitle, reason);
            return false;
        }
        catch (XcodesException ex)
        {
            Logger.Warn($"{title} failed: {ex.Message}");
            string reason = ex.Message switch
            {
                _ when usesXcodebuild || downloadsPublicRuntime => RuntimeInstallReason(ex.Message),
                _ => ex.Message,
            };
            bool possibleAccessFailure;
            string? accessOutput;
            lock (_outputLock)
            {
                possibleAccessFailure = _possibleAccessFailure;
                accessOutput = _accessFailureOutput;
            }
            if ((isInstallation || isAppleDownload) && possibleAccessFailure && accessOutput is not null
                && !OperationLog.Contains(accessOutput, StringComparison.Ordinal))
            {
                OperationLog = CoreTools.Translate("Earlier access-related output: {0}", accessOutput)
                    + Environment.NewLine + OperationLog;
            }
            FailOperation(failureTitle, reason);
            HasAccessRecovery = (isInstallation || isAppleDownload)
                && (ex.IsAccessFailure || possibleAccessFailure || XcodesError.IsPossibleAccessFailure(ex.Message)
                    || XcodesError.IsPossibleAccessFailure(OperationLog));
            return false;
        }
        catch (Exception ex)
        {
            Logger.Error($"{title} failed");
            Logger.Error(ex);
            FailOperation(failureTitle, ex.Message);
            return false;
        }
        finally
        {
            lock (_outputLock)
            {
                if (ReferenceEquals(_outputOperation, cancellation))
                {
                    _outputOperation = null;
                    _pendingOutput.Clear();
                    _pendingOutputCharacters = 0;
                    _pendingOutputOmitted = false;
                    _possibleAccessFailure = false;
                    _accessFailureOutput = null;
                    _signInAppleId = null;
                }
            }
            _operationCancellation = null;
            IsOperationRunning = false;
            SetBrowserOperationTarget(null);
            Runtimes.SetOperationTarget(null);
            _operationIsInstallation = false;
            _operationUsesXcodebuild = false;
            _operationDownloadsPublicRuntime = false;
        }
    }

    private void StartOperation(
        string title, bool isInstallation, bool usesXcodebuild, bool downloadsPublicRuntime,
        BrowserOperationTarget? browserTarget, XcodeSimulatorRuntime? runtimeTarget, CancellationTokenSource operation)
    {
        _operationIsInstallation = isInstallation;
        _operationUsesXcodebuild = usesXcodebuild;
        _operationDownloadsPublicRuntime = downloadsPublicRuntime;
        lock (_outputLock)
        {
            _outputOperation = operation;
            _signInAppleId = usesXcodebuild || downloadsPublicRuntime ? null : Account.Username;
            _pendingOutput.Clear();
            _pendingOutputCharacters = 0;
            _pendingOutputOmitted = false;
            _outputScheduled = false;
            _possibleAccessFailure = false;
            _accessFailureOutput = null;
        }
        _log.Clear();
        _logCharacters = 0;
        _logOmitted = false;
        OperationLog = "";
        OperationTitle = title;
        OperationStatus = downloadsPublicRuntime
            ? CoreTools.Translate("Starting Apple runtime download...")
            : usesXcodebuild ? CoreTools.Translate("Starting xcodebuild...") : CoreTools.Translate("Starting...");
        OperationStep = "";
        OperationProgress = 0;
        OperationIsIndeterminate = true;
        IsLogExpanded = false;
        HasAccessRecovery = false;
        _retryRelease = null;
        _retryReplacing = null;
        _retryDownloadTarget = null;
        Notice.IsOpen = false;
        HasOperation = true;
        IsOperationRunning = true;
        SetBrowserOperationTarget(browserTarget);
        Runtimes.SetOperationTarget(runtimeTarget);
        OnPropertyChanged(nameof(CanOpenSimulatorRuntimes));
    }

    private void SetBrowserOperationTarget(BrowserOperationTarget? target)
    {
        _browserOperationTarget = target;
        foreach (XcodeBrowserItemViewModel item in BrowserItems)
            item.SetOperationTarget(target?.Matches(item) == true);
    }

    private void FailOperation(string title, string reason)
    {
        OperationTitle = title;
        OperationStatus = "";
        OperationStep = "";
        IsLogExpanded = true;
        ShowNotice(title, reason, InfoBarSeverity.Error);
    }

    /// <summary>Called from the thread the operation runs on, so what it shows is posted to the interface thread.</summary>
    private async Task<string?> AskAsync(CancellationTokenSource operation, XcodesPrompt prompt, CancellationToken cancellation)
    {
        string? appleId;
        lock (_outputLock)
        {
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_outputOperation, operation))
                return null;
            appleId = _signInAppleId;
        }
        // This job can run after the question or operation ended.
        Dispatcher.UIThread.Post(() =>
        {
            if (!cancellation.IsCancellationRequested && IsOperationRunning && ReferenceEquals(_operationCancellation, operation))
                OperationStatus = CoreTools.Translate("Waiting for your answer...");
        });
        string? answer = await Ask(prompt with { AppleId = appleId }, cancellation).ConfigureAwait(false);
        lock (_outputLock)
        {
            if (cancellation.IsCancellationRequested || !ReferenceEquals(_outputOperation, operation))
                return null;
            if (prompt.Kind == XcodesPromptKind.AppleId && !string.IsNullOrWhiteSpace(answer))
                _signInAppleId = answer.Trim();
        }
        return answer;
    }

    /// <summary>Called from the thread the operation runs on; the lines are shown on the interface thread, in order.</summary>
    private void QueueOutput(CancellationTokenSource operation, XcodesOutputLine line)
    {
        bool possibleAccessFailure = XcodesError.IsPossibleAccessFailure(line.Text);
        if (line.Text.Length > MaxOutputLineLength)
            line = line with { Text = LongLineOmitted };
        bool schedule = false;
        lock (_outputLock)
        {
            if (!ReferenceEquals(_outputOperation, operation))
                return;
            if (possibleAccessFailure)
            {
                _possibleAccessFailure = true;
                if (line.Text != LongLineOmitted)
                    _accessFailureOutput ??= line.Text;
            }
            _pendingOutput.Enqueue(line);
            _pendingOutputCharacters += line.Text.Length;
            while (_pendingOutput.Count > MaxLogLines || _pendingOutputCharacters > MaxLogCharacters)
            {
                _pendingOutputCharacters -= _pendingOutput.Dequeue().Text.Length;
                _pendingOutputOmitted = true;
            }
            if (!_outputScheduled)
            {
                _outputScheduled = true;
                schedule = true;
            }
        }
        if (schedule)
            Dispatcher.UIThread.Post(ShowPendingOutput);
    }

    /// <summary>
    /// Shows what the operation has written so far. The end of an operation does this itself instead of waiting for the
    /// posted jobs, which can run after the continuation that follows them and would then find it over.
    /// </summary>
    private void ShowPendingOutput()
    {
        List<XcodesOutputLine> pending;
        bool omitted;
        lock (_outputLock)
        {
            pending = [.. _pendingOutput];
            _pendingOutput.Clear();
            _pendingOutputCharacters = 0;
            omitted = _pendingOutputOmitted;
            _pendingOutputOmitted = false;
            _outputScheduled = false;
        }
        if (!IsOperationRunning)
            return;
        if (omitted)
        {
            _logOmitted = true;
            if (pending.Count > 0 && pending[0].ReplacesPrevious)
                pending[0] = pending[0] with { ReplacesPrevious = false };
        }
        foreach (XcodesOutputLine line in pending)
            AppendOutput(line);
        string text = string.Join(Environment.NewLine, _log);
        OperationLog = _logOmitted
            ? CoreTools.Translate("[Earlier output was omitted.]") + Environment.NewLine + text
            : text;
    }

    private void AppendOutput(XcodesOutputLine line)
    {
        string text = line.Text == LongLineOmitted
            ? CoreTools.Translate("[A long output line was omitted.]")
            : line.Text.TrimEnd();
        if (!IsOperationRunning || text.Length == 0)
            return;

        if (XcodesOutputParser.TryParseProgress(text, out XcodeProgress progress))
        {
            OperationStep = CoreTools.Translate("Step {0} of {1}", progress.Step, progress.StepCount);
            OperationStatus = progress.Text;
            OperationIsIndeterminate = progress.Fraction is null;
            if (progress.Fraction is { } fraction)
                OperationProgress = fraction * 100;
        }
        else if ((_operationUsesXcodebuild || _operationDownloadsPublicRuntime)
            && XcodesOutputParser.TryParseRuntimeProgress(text, out double percent))
        {
            string value = percent.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture);
            OperationStatus = text.StartsWith("Copying runtime image: ", StringComparison.Ordinal)
                ? CoreTools.Translate("Copying runtime image: {0}%", value)
                : text.StartsWith("Downloading runtime image: ", StringComparison.Ordinal)
                    ? CoreTools.Translate("Downloading runtime image: {0}%", value) : text;
            OperationIsIndeterminate = false;
            OperationProgress = percent;
        }
        else if (_operationUsesXcodebuild && text.Contains("Installing", StringComparison.OrdinalIgnoreCase))
        {
            OperationStatus = CoreTools.Translate("Installing simulator runtime...");
            OperationIsIndeterminate = true;
        }
        else if ((_operationUsesXcodebuild || _operationDownloadsPublicRuntime) && text == "Verifying copied runtime image...")
        {
            OperationStatus = CoreTools.Translate("Verifying copied runtime image...");
            OperationIsIndeterminate = true;
        }
        else if (_operationDownloadsPublicRuntime && text == "Downloading runtime image...")
        {
            OperationStatus = CoreTools.Translate("Downloading runtime image...");
            OperationIsIndeterminate = true;
        }

        // A line that only reports progress replaces the one before it, as it would in a terminal.
        if (line.ReplacesPrevious && _log.Count > 0)
        {
            _logCharacters -= _log[^1].Length;
            _log[^1] = text;
        }
        else
        {
            _log.Add(text);
        }
        _logCharacters += text.Length;
        while (_log.Count > MaxLogLines || _logCharacters > MaxLogCharacters)
        {
            _logCharacters -= _log[0].Length;
            _log.RemoveAt(0);
            _logOmitted = true;
        }
    }

    /// <summary>Requests cancellation of the running operation.</summary>
    [RelayCommand]
    public void CancelOperation() => _operationCancellation?.Cancel();

    [RelayCommand]
    private async Task RequestCancelOperation()
    {
        CancellationTokenSource? operation = _operationCancellation;
        if (operation is not null && IsOperationRunning && await ConfirmCancel()
            && ReferenceEquals(_operationCancellation, operation))
            operation.Cancel();
    }

    private async Task<bool> ConfirmCancelInWindowAsync()
    {
        if (MainWindow.Instance is not { } owner)
            return false;
        ImmersiveConfirmationDialog dialog = CreateCancelDialog(_operationDownloadsPublicRuntime);
        await dialog.ShowDialog(owner);
        return dialog.Result is true;
    }

    internal static ImmersiveConfirmationDialog CreateCancelDialog(bool downloadsPublicRuntime)
    {
        var dialog = new ImmersiveConfirmationDialog(
            CoreTools.Translate("Cancel this operation?"),
            Paragraph(downloadsPublicRuntime
                ? CoreTools.Translate("Stopping the download may leave an incomplete file; it cannot be resumed.")
                : CoreTools.Translate("Stopping requests cancellation; system-managed work may continue afterwards.")),
            CoreTools.Translate("Stop operation"),
            CoreTools.Translate("Keep going"))
        {
            FocusPrimaryButton = false,
        };
        return dialog;
    }

    [RelayCommand]
    private void DismissOperation()
    {
        if (!IsOperationRunning)
            HasOperation = false;
    }

    /// <summary>
    /// Asks whether to cancel the running operation before quitting. Xcode-managed system work may continue.
    /// </summary>
    internal bool CanQuitAfterCancellation => !_operationIsInstallation || !IsOperationRunning;

    public async Task<bool> ConfirmQuitAsync()
    {
        if (!IsOperationRunning)
            return true;
        if (MainWindow.Instance is not { } owner)
            return false;

        // Quitting can ask while the window is hidden (macOS, tray), and a dialog in a hidden window never completes.
        if (!owner.IsVisible || owner.WindowState == WindowState.Minimized)
            owner.ShowFromTray();

        var dialog = new ImmersiveConfirmationDialog(
            OperationTitle,
            Paragraph(_operationIsInstallation
                ? CoreTools.Translate("Quitting requests cancellation. Once administrator setup starts, wait for it to finish before quitting.")
                : _operationUsesXcodebuild
                ? CoreTools.Translate("Quitting requests cancellation, but macOS or Xcode may continue processing the runtime.")
                : _operationDownloadsPublicRuntime
                    ? CoreTools.Translate("Quitting stops the download and may leave an incomplete file.")
                : CoreTools.Translate(
                    "Quitting UniGetUI stops this. If it was downloading, what was downloaded is kept, so it can usually carry on from where it stopped.")),
            CoreTools.Translate("Stop and quit"),
            CoreTools.Translate("Keep going"));
        await dialog.ShowDialog(owner);
        if (dialog.Result is not true)
            return false;

        CancelOperation();
        for (int waited = 0; IsOperationRunning && waited < 100; waited++)
            await Task.Delay(100);
        if (CanQuitAfterCancellation)
            return true;
        ShowNotice(CoreTools.Translate("Xcode is still finishing"),
            CoreTools.Translate("Wait for the Xcode installation to finish before quitting."), InfoBarSeverity.Warning);
        return false;
    }

    // ─── Uninstalling ────────────────────────────────────────────────────────

    [RelayCommand]
    private Task Uninstall(InstalledXcodeItemViewModel? item)
    {
        if (item is null)
            return Task.CompletedTask;

        return RunExclusiveAsync(async () =>
        {
            if (await ConfirmUninstall(item))
                await RevealInFinderAsync(item);
        });
    }

    private static async Task<bool> ConfirmUninstallInWindowAsync(
        InstalledXcodeItemViewModel item)
        => MainWindow.Instance is { } owner && await ConfirmUninstallAsync(item, owner);

    private static async Task<bool> ConfirmUninstallAsync(
        InstalledXcodeItemViewModel item, MainWindow owner)
    {
        ImmersiveConfirmationDialog dialog = CreateUninstallDialog(item);
        await dialog.ShowDialog(owner);
        return dialog.Result is true;
    }

    internal static ImmersiveConfirmationDialog CreateUninstallDialog(InstalledXcodeItemViewModel item)
    {
        var body = new StackPanel
        {
            Spacing = 12,
            MaxWidth = 520,
            Children =
            {
               Paragraph(CoreTools.Translate(
                   "UniGetUI does not delete Xcode itself, so that nothing you still need is removed by mistake.")),
               Paragraph(CoreTools.Translate(
                   "Open Finder and move the Xcode at {0} to the Trash yourself. Nothing is removed by UniGetUI.",
                   item.Location)),
            },
        };
        if (item.IsSelected)
            body.Children.Add(Warning(CoreTools.Translate(
               "It is the Xcode that command line tools use. Select another one afterwards, or they will stop working.")));

        return new ImmersiveConfirmationDialog(
            CoreTools.Translate("Uninstall Xcode {0}?", item.Version), XcodeDialogContent.Scroll(body),
            CoreTools.Translate("Show in Finder"), CoreTools.Translate("Cancel"))
        {
            FocusPrimaryButton = false,
        };
    }

    // ─── Opening ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task Open(InstalledXcodeItemViewModel? item)
    {
        if (item is null) return;
        try
        {
            await OpenXcode(item.Location);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not open {item.Location}: {ex.Message}");
            ShowNotice(CoreTools.Translate("Could not open Xcode"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private static async Task OpenXcodeCoreAsync(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException(CoreTools.Translate("The Xcode at {0} is no longer available.", path));

        var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
        start.ArgumentList.Add("-n");
        start.ArgumentList.Add(path);
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException(CoreTools.Translate("The system could not open the Xcode at {0}.", path));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(CoreTools.Translate("Could not confirm that Xcode opened. It may still open."));
        }
        if (process.ExitCode != 0)
            throw new InvalidOperationException(CoreTools.Translate("The system could not open the Xcode at {0}.", path));
    }

    [RelayCommand]
    private async Task RevealInFinderAsync(InstalledXcodeItemViewModel? item)
    {
        if (item is null) return;

        try
        {
            await ShowPathInFinder(item.Location);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                   or Win32Exception or TimeoutException)
        {
            Logger.Warn($"Could not show {item.Location} in Finder: {ex.Message}");
            ShowNotice(CoreTools.Translate("Could not show the Xcode in Finder"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private static Task ShowPathInFinderCoreAsync(string path)
        => ShowPathInFinderCoreAsync(path, start => RunFinderProcessAsync(start, TimeSpan.FromSeconds(15)));

    internal static async Task ShowPathInFinderCoreAsync(string path, Func<ProcessStartInfo, Task<int>> run)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException(CoreTools.Translate("The Xcode at {0} is no longer available.", path));

        await ShowExistingPathInFinderAsync(path, run).ConfigureAwait(false);
    }

    private async Task RevealDownloadedArchiveAsync(string path)
    {
        try
        {
            await ShowArchiveInFinder(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                   or Win32Exception or TimeoutException)
        {
            Logger.Warn($"Could not show the Xcode archive in Finder: {ex.Message}");
            ShowNotice(CoreTools.Translate("Could not show the Xcode archive in Finder"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private static Task ShowArchiveInFinderCoreAsync(string path)
        => ShowArchiveInFinderCoreAsync(path, start => RunFinderProcessAsync(start, TimeSpan.FromSeconds(15)));

    internal static async Task ShowArchiveInFinderCoreAsync(string path, Func<ProcessStartInfo, Task<int>> run)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException(CoreTools.Translate("The Xcode archive at {0} is no longer available.", path));

        await ShowExistingPathInFinderAsync(path, run).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task RevealSimulatorRuntimeInFinderAsync(XcodeSimulatorRuntimeItemViewModel? item)
    {
        if (!TryGetSimulatorRuntimePath(item, out string path))
            return;

        try
        {
            await ShowSimulatorPathInFinder(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                   or Win32Exception or TimeoutException)
        {
            Logger.Warn($"Could not show simulator runtime {path} in Finder: {ex.Message}");
            ShowNotice(CoreTools.Translate("Could not show the simulator runtime in Finder"), ex.Message, InfoBarSeverity.Error);
        }
    }

    private static Task ShowSimulatorPathInFinderCoreAsync(string path)
        => ShowSimulatorPathInFinderCoreAsync(path, start => RunFinderProcessAsync(start, TimeSpan.FromSeconds(15)));

    internal static async Task ShowSimulatorPathInFinderCoreAsync(string path, Func<ProcessStartInfo, Task<int>> run)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
            throw new FileNotFoundException(CoreTools.Translate("The simulator runtime at {0} is no longer available.", path));

        await ShowExistingPathInFinderAsync(path, run).ConfigureAwait(false);
    }

    private static async Task ShowExistingPathInFinderAsync(string path, Func<ProcessStartInfo, Task<int>> run)
    {
        var start = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
        start.ArgumentList.Add("-R");
        start.ArgumentList.Add(path);
        if (await run(start).ConfigureAwait(false) != 0)
            throw new InvalidOperationException(CoreTools.Translate("Finder could not be opened."));
    }

    internal static async Task<int> RunFinderProcessAsync(ProcessStartInfo start, TimeSpan timeout)
    {
        using Process process = Process.Start(start)
            ?? throw new InvalidOperationException(CoreTools.Translate("Finder could not be opened."));
        await WaitForFinderExitAsync(process.WaitForExitAsync, timeout).ConfigureAwait(false);
        return process.ExitCode;
    }

    internal static async Task WaitForFinderExitAsync(Func<CancellationToken, Task> waitForExit, TimeSpan duration)
    {
        using var timeout = new CancellationTokenSource(duration);
        try
        {
            await waitForExit(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException(CoreTools.Translate("Could not confirm that Finder opened. It may still open."));
        }
    }

    [RelayCommand]
    private Task CopyPath(InstalledXcodeItemViewModel? item)
    {
        return item is null ? Task.CompletedTask : CopyPathAsync(item.Location);
    }

    [RelayCommand]
    private Task CopySimulatorRuntimePath(XcodeSimulatorRuntimeItemViewModel? item)
        => TryGetSimulatorRuntimePath(item, out string path) ? CopyPathAsync(path) : Task.CompletedTask;

    private bool TryGetSimulatorRuntimePath(XcodeSimulatorRuntimeItemViewModel? item, out string path)
    {
        if (item is { HasLocalPath: true })
        {
            path = item.Path;
            return true;
        }

        path = "";
        if (item is not null)
            ShowNotice(CoreTools.Translate("Simulator runtime path unavailable"),
                CoreTools.Translate("This simulator runtime has no absolute local path."), InfoBarSeverity.Error);
        return false;
    }

    private async Task CopyPathAsync(string path)
    {
        try
        {
            await CopyText(path);
            ShowNotice(CoreTools.Translate("Path copied"), path, InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            Logger.Error("Could not copy the path.");
            Logger.Error(ex);
            ShowNotice(CoreTools.Translate("Could not copy the path"), ex.Message, InfoBarSeverity.Error);
        }
    }

    [RelayCommand]
    private static void OpenReleaseNotes(XcodeBrowserItemViewModel? item)
    {
        if (item?.Details?.ReleaseNotes is { } notes)
            CoreTools.Launch(notes.AbsoluteUri);
    }

    private static Task CopyTextToClipboardAsync(string text)
    {
        var clipboard = MainWindow.Instance?.Clipboard
            ?? throw new InvalidOperationException(CoreTools.Translate("The clipboard is unavailable."));
        return clipboard.SetTextAsync(text);
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static TextBlock Paragraph(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private static string RuntimeInstallReason(string reason) => reason switch
    {
        "Simulator runtimes can only be managed on macOS." =>
            CoreTools.Translate("Simulator runtimes can only be managed on macOS."),
        "This simulator runtime cannot be managed by the selected Xcode." =>
            CoreTools.Translate("This simulator runtime cannot be managed by the selected Xcode."),
        "This simulator runtime has no supported public download." =>
            CoreTools.Translate("This simulator runtime has no supported public download."),
        "The Apple runtime download timed out." =>
            CoreTools.Translate("The Apple runtime download timed out."),
        "Apple did not return a simulator runtime disk image." =>
            CoreTools.Translate("Apple did not return a simulator runtime disk image."),
        "Apple returned an unsupported runtime image encoding." =>
            CoreTools.Translate("Apple returned an unsupported runtime image encoding."),
        "The simulator runtime image has an invalid or unsupported size." =>
            CoreTools.Translate("The simulator runtime image has an invalid or unsupported size."),
        "The simulator runtime image download was incomplete." =>
            CoreTools.Translate("The simulator runtime image download was incomplete."),
        "The selected developer tools have no usable directory." =>
            CoreTools.Translate("The selected developer tools have no usable directory."),
        "The selected developer tools did not report an Xcode version." =>
            CoreTools.Translate("The selected developer tools did not report an Xcode version."),
        "The simulator runtime request is invalid." =>
            CoreTools.Translate("The simulator runtime request is invalid."),
        "The selected Xcode changed after confirmation. Try again." =>
            CoreTools.Translate("The selected Xcode changed after confirmation. Try again."),
        "The selected Xcode cannot explicitly choose the runtime architecture." =>
            CoreTools.Translate("The selected Xcode cannot explicitly choose the runtime architecture."),
        "The selected Xcode cannot export a simulator runtime with the requested build and architecture." =>
            CoreTools.Translate("The selected Xcode cannot export a simulator runtime with the requested build and architecture."),
        "Choose an existing absolute folder for the simulator runtime image." =>
            CoreTools.Translate("Choose an existing absolute folder for the simulator runtime image."),
        "Xcode did not export exactly one simulator runtime disk image." =>
            CoreTools.Translate("Xcode did not export exactly one simulator runtime disk image."),
        "Xcode exported an unsafe simulator runtime disk image." =>
            CoreTools.Translate("Xcode exported an unsafe simulator runtime disk image."),
        "Xcode exported an empty or unreadable simulator runtime disk image." =>
            CoreTools.Translate("Xcode exported an empty or unreadable simulator runtime disk image."),
        "The exported simulator runtime disk image changed while it was being copied." =>
            CoreTools.Translate("The exported simulator runtime disk image changed while it was being copied."),
        "The copied simulator runtime disk image is not a regular file." =>
            CoreTools.Translate("The copied simulator runtime disk image is not a regular file."),
        "The copied simulator runtime disk image could not be verified." =>
            CoreTools.Translate("The copied simulator runtime disk image could not be verified."),
        "Using this runtime requires Xcode 16.1 or later to be selected." =>
            CoreTools.Translate("Using this runtime requires Xcode 16.1 or later to be selected."),
        "Using an Apple Silicon runtime requires Xcode 26 or later to be selected." =>
            CoreTools.Translate("Using an Apple Silicon runtime requires Xcode 26 or later to be selected."),
        "Xcode finished, but the selected runtime build and architecture could not be "
            + "confirmed as ready. The system may still be processing it; refresh the runtime list." =>
            CoreTools.Translate("Xcode finished, but the selected runtime build and architecture could not be confirmed as ready. The system may still be processing it; refresh the runtime list."),
        _ => reason,
    };

    private static TextBlock Warning(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontWeight = FontWeight.SemiBold,
    };

    private void ShowNotice(string title, string message, InfoBarSeverity severity)
    {
        _versionLoadNoticeShown = false;
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.ActionButtonText = "";
        Notice.ActionButtonCommand = null;
        Notice.BodyCommand = null;
        Notice.IsClosable = true;
        Notice.IsOpen = true;
    }
}
