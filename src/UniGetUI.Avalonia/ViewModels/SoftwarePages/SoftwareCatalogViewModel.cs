using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Models;
using UniGetUI.Avalonia.Views;
using UniGetUI.Avalonia.Views.Pages;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Telemetry;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.ViewModels.Pages;

public partial class SoftwareCatalogViewModel : ViewModelBase
{
    private Dictionary<string, IReadOnlyList<IManagerSource>> _sources = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<CatalogTileViewModel> _allPackages = [];
    private readonly Dictionary<(string Manager, string Source), SourceTreeNode> _catalogSources = [];
    private bool _updatingSourceSelection;
    private bool _updatingCatalogSelection;
    private readonly Dictionary<(string Manager, string Source), bool> _sourceSelectionMemory = [];
    public ObservableCollection<CatalogTileViewModel> Packages { get; } = [];
    public ObservableCollection<CatalogGroupViewModel> Groups { get; } = [];
    public ObservableCollection<CatalogFilterNode> CatalogNodes { get; } = [];
    public ObservableCollection<SourceTreeNode> SourceNodes { get; } = [];

    [ObservableProperty]
    private bool _groupByCatalog = true;

    partial void OnGroupByCatalogChanged(bool value) => ApplyAvailabilityFilter();

    [ObservableProperty]
    private bool _hideUnavailablePackages = true;

    [ObservableProperty]
    private string _query = "";

    [ObservableProperty]
    private bool _isFilterPaneOpen;

    // Live width of the filter pane, kept in sync by the page so the toolbar's Filters button
    // reserves the pane's width and the remaining buttons align with the package list.
    private double _trackedFilterPaneWidth = 220.0;
    public double TrackedFilterPaneWidth
    {
        get => _trackedFilterPaneWidth;
        set
        {
            if (Math.Abs(_trackedFilterPaneWidth - value) < 0.5) return;
            _trackedFilterPaneWidth = value;
            if (IsFilterPaneOpen) OnPropertyChanged(nameof(FilterPaneColumnWidth));
        }
    }

    public double FilterPaneColumnWidth => IsFilterPaneOpen ? _trackedFilterPaneWidth : 0.0;

    partial void OnIsFilterPaneOpenChanged(bool value) =>
        OnPropertyChanged(nameof(FilterPaneColumnWidth));

    [ObservableProperty]
    private SearchMode _searchMode = SearchMode.Both;

    public bool SearchMode_Name { get => SearchMode == SearchMode.Name; set { if (value) SearchMode = SearchMode.Name; } }
    public bool SearchMode_Id { get => SearchMode == SearchMode.Id; set { if (value) SearchMode = SearchMode.Id; } }
    public bool SearchMode_Both { get => SearchMode == SearchMode.Both; set { if (value) SearchMode = SearchMode.Both; } }
    public bool SearchMode_Exact { get => SearchMode == SearchMode.Exact; set { if (value) SearchMode = SearchMode.Exact; } }

    partial void OnSearchModeChanged(SearchMode value)
    {
        OnPropertyChanged(nameof(SearchMode_Name));
        OnPropertyChanged(nameof(SearchMode_Id));
        OnPropertyChanged(nameof(SearchMode_Both));
        OnPropertyChanged(nameof(SearchMode_Exact));
        ApplyAvailabilityFilter();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsListView))]
    [NotifyPropertyChangedFor(nameof(IsGridView))]
    [NotifyPropertyChangedFor(nameof(IsIconsView))]
    [NotifyPropertyChangedFor(nameof(IsTilesView))]
    private int _viewModeIndex = LoadViewModeIndex();

    // Shares the per-page view and sort settings with the other package pages; Icons by default.
    private const string ViewModeSettingKey = "SoftwarePages.SoftwareCatalogPage";
    private const int DefaultViewModeIndex = 2;

    private static int LoadViewModeIndex()
    {
        if (!Settings.DictionaryContainsKey<string, int>(Settings.K.PackageListViewMode, ViewModeSettingKey))
            return DefaultViewModeIndex;
        int saved = Settings.GetDictionaryItem<string, int>(Settings.K.PackageListViewMode, ViewModeSettingKey);
        return saved is >= 0 and <= 3 ? saved : DefaultViewModeIndex;
    }

    partial void OnViewModeIndexChanged(int value)
    {
        if (value is >= 0 and <= 3)
            Settings.SetDictionaryItem(Settings.K.PackageListViewMode, ViewModeSettingKey, value);
    }

    public bool IsListView => ViewModeIndex == 0;
    public bool IsGridView => ViewModeIndex == 1;
    public bool IsIconsView => ViewModeIndex == 2;
    public bool IsTilesView => ViewModeIndex == 3;

    partial void OnQueryChanged(string value) => ApplyAvailabilityFilter();
    partial void OnHideUnavailablePackagesChanged(bool value) => ApplyAvailabilityFilter();

    public bool HasHiddenPackages
    {
        get
        {
            if (!HideUnavailablePackages) return false;
            string query = Query.Trim();
            return _allPackages.Any(p => p.UnavailableReason is not null
                && IsSelectedByFilters(p)
                && MatchesQuery(p.Entry, query));
        }
    }
    public string EmptyMessage => _notConfigured
        ? CoreTools.Translate("No software catalog has been set up on this device yet.")
        : CoreTools.Translate(_allPackages.Count > 0
            ? "No packages are available with the current filters."
            : "No packages to display.");

    // No catalog file at all is the normal state on most devices, not an error.
    private bool _notConfigured;

    // The path and technical reason under the headline; selectable so it can be copied.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBackgroundDetail))]
    private string _backgroundDetail = "";

    public bool HasBackgroundDetail => BackgroundDetail.Length > 0 && HasBackgroundText;

    public bool IsEmpty => !IsLoading && !HasError && Packages.Count == 0;

    public string BackgroundText => HasError
        ? ErrorMessage
        : !IsEmpty ? ""
        : HasHiddenPackages
            ? CoreTools.Translate("Packages with unavailable sources are hidden. Change Filters to show them.")
            : EmptyMessage;

    public bool HasBackgroundText => BackgroundText.Length > 0;

    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        NotifyBackgroundTextChanged();
    }

    private void NotifyBackgroundTextChanged()
    {
        OnPropertyChanged(nameof(BackgroundText));
        OnPropertyChanged(nameof(HasBackgroundText));
        OnPropertyChanged(nameof(HasBackgroundDetail));
        OnPropertyChanged(nameof(ShowCreateCatalog));
    }

    private bool IsSelectedByFilters(CatalogTileViewModel tile) =>
        _catalogSelection.TryGetValue(tile.CatalogId, out var catalog) && catalog.IsSelected
        && _catalogSources.TryGetValue(SourceKey(tile.Entry), out var source) && source.IsSelected;

    private readonly Dictionary<string, CatalogFilterNode> _catalogSelection = new(StringComparer.OrdinalIgnoreCase);

    private string? _loadedSignature;

    // A stable fingerprint of the loaded catalogs, to skip rebuilding tiles when nothing changed.
    private static readonly JsonTypeInfo<CatalogDocument> DocumentTypeInfo = CatalogJsonContext.Default.CatalogDocument;

    private static string Signature(IReadOnlyList<CatalogDefinition> catalogs) =>
        JsonSerializer.Serialize(new CatalogDocument { Version = 1, Catalogs = [.. catalogs] }, DocumentTypeInfo);

    // Icons need the entry's manager and source; tiles that lacked them earlier get another try.
    private void EnsureIcons()
    {
        foreach (var tile in _allPackages)
        {
            if (tile.IconRequested) continue;
            var manager = FindManager(tile.Entry);
            var source = FindSource(tile.Entry);
            if (manager is null || source is null) continue;
            tile.IconRequested = true;
            var package = InstalledPackagesLoader.Instance?.Packages.FirstOrDefault(tile.Entry.MatchesInstalled)
                ?? new Package(tile.Entry.Name, tile.Entry.Id, tile.Entry.Version, source, manager);
            _ = tile.LoadIconAsync(package);
        }
    }

    public void LoadCatalogs(IReadOnlyList<CatalogDefinition> catalogs)
    {
        _loadedSignature = Signature(catalogs);
        var previous = CatalogNodes.ToDictionary(n => n.Definition.Id, n => n.IsSelected, StringComparer.OrdinalIgnoreCase);
        _catalogSelection.Clear();
        CatalogNodes.Clear();
        _allPackages.Clear();
        _updatingCatalogSelection = true;
        // A pkg feed's source is named by its catalog's id; people know it by the catalog's name.
        var feedNames = catalogs.Where(c => c.PkgFeed is not null)
            .DistinctBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(c => c.Id, c => c.Name, StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var catalog in catalogs)
            {
                var node = new CatalogFilterNode(catalog) { IsSelected = previous.GetValueOrDefault(catalog.Id, true) };
                node.PropertyChanged += (_, e) =>
                {
                    if (!_updatingCatalogSelection && e.PropertyName == nameof(CatalogFilterNode.IsSelected))
                        ApplyAvailabilityFilter();
                };
                CatalogNodes.Add(node);
                _catalogSelection.TryAdd(catalog.Id, node);
                foreach (var entry in catalog.Packages)
                    _allPackages.Add(new CatalogTileViewModel(entry, catalog)
                    {
                        Owner = this,
                        SourceLabel = FindManager(entry)?.Id is { } managerId && PkgManagerIds.Contains(managerId)
                            && feedNames.TryGetValue(entry.Source, out string? feedName) ? feedName : entry.Source,
                    });
            }
        }
        finally { _updatingCatalogSelection = false; }
        EnsureIcons();
        BuildSourceNodes();
        UpdateStates();
        OnPropertyChanged(nameof(IsEmpty));
        NotifyBackgroundTextChanged();
    }

    [RelayCommand]
    private void SelectAllCatalogs() => SetCatalogSelection(true);

    [RelayCommand]
    private void ClearCatalogSelection() => SetCatalogSelection(false);

    private void SetCatalogSelection(bool selected)
    {
        _updatingCatalogSelection = true;
        try
        {
            foreach (var node in CatalogNodes) node.IsSelected = selected;
        }
        finally { _updatingCatalogSelection = false; }
        ApplyAvailabilityFilter();
    }

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string _errorMessage = "";

    public bool HasError => ErrorMessage.Length > 0;

    partial void OnErrorMessageChanged(string value)
    {
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(IsEmpty));
        NotifyBackgroundTextChanged();
        if (value.Length > 0)
            Avalonia.Infrastructure.AccessibilityAnnouncementService.Announce(
                value, global::Avalonia.Automation.AutomationLiveSetting.Assertive);
    }

    // Administrators who enabled the Catalog Editor can start a catalog from here; everyone else only installs.
    public bool IsEditorEnabled { get; } =
        Settings.Get(Settings.K.EnableSoftwareCatalog) && Settings.Get(Settings.K.EnableCatalogEditor);

    // On a device with no catalog yet, the empty page offers to create one.
    public bool ShowCreateCatalog => IsEditorEnabled && _notConfigured && IsEmpty;

    [RelayCommand]
    private void OpenEditor() => MainWindow.Instance?.Navigate(PageType.CatalogEditor);

    [RelayCommand]
    private async Task NewCatalogAsync()
    {
        if (MainWindow.Instance?.DataContext is MainWindowViewModel main)
            await main.StartNewCatalogAsync();
    }

    private bool _waitingForManagers;

    // What the catalog file looked like when it was last loaded: existence, size and write time.
    private (bool Exists, long Length, DateTime WriteTime)? _fileStamp;

    private static (bool, long, DateTime) ReadFileStamp()
    {
        try
        {
            var file = new FileInfo(SoftwareCatalog.FilePath);
            return file.Exists ? (true, file.Length, file.LastWriteTimeUtc) : (false, 0, default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (false, 0, default);
        }
    }

    /// <summary>
    /// Reloads the catalog when its file was created, changed or removed since the last load, so
    /// an administrator's deployment shows up without leaving the page.
    /// </summary>
    public void CheckForCatalogChanges()
    {
        if (IsLoading || _fileStamp is not { } loaded) return;
        if (ReadFileStamp() != loaded) _ = RefreshAsync();
    }

    private void OnManagersLoaded(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!_waitingForManagers) return;
        _waitingForManagers = false;
        PEInterface.ManagersLoaded -= OnManagersLoaded;
        IsLoading = false;
        _ = RefreshAsync();
    });

    [RelayCommand]
    public async Task RefreshAsync()
    {
        // Until managers finish starting, every entry would look unavailable (and be hidden), so
        // keep showing the loading state and refresh once they are ready.
        if (!PEInterface.AreManagersLoaded)
        {
            IsLoading = true;
            if (!_waitingForManagers)
            {
                _waitingForManagers = true;
                PEInterface.ManagersLoaded += OnManagersLoaded;
                if (PEInterface.AreManagersLoaded) OnManagersLoaded(null, EventArgs.Empty);
            }
            return;
        }
        if (IsLoading) return;
        IsLoading = true;
        ErrorMessage = "";
        // Stamp before reading: a change made while loading is caught by the next check.
        _fileStamp = ReadFileStamp();
        UpdateStates();
        try
        {
            var catalogs = await ExpandPkgFeedsAsync(await SoftwareCatalog.LoadAsync());
            _notConfigured = false;
            BackgroundDetail = "";

            // Fill a new map and swap it in at the end: the state timer reads _sources between
            // these slow awaits, and a half-filled map would mark tiles "source not enabled".
            var sources = new Dictionary<string, IReadOnlyList<IManagerSource>>(StringComparer.OrdinalIgnoreCase);
            foreach (var manager in catalogs.SelectMany(c => c.Packages).Select(FindManager).OfType<IPackageManager>().Distinct())
            {
                if (!manager.IsReady()) continue;
                sources[manager.Id] = manager.Capabilities.SupportsCustomSources
                    ? await Task.Run(manager.SourcesHelper.GetSources)
                    : [manager.DefaultSource];
            }
            _sources = sources;

            // Rebuilding identical tiles would reset the scroll position and make icons blink.
            if (_allPackages.Count > 0 && Signature(catalogs) == _loadedSignature)
                EnsureIcons();
            else
                LoadCatalogs(catalogs);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            Logger.Info($"No software catalog at {SoftwareCatalog.FilePath}.");
            _notConfigured = true;
            LoadCatalogs([]);
            BackgroundDetail = CoreTools.Translate("Expected at {0}", SoftwareCatalog.FilePath);
        }
        catch (Exception ex)
        {
            Logger.Error("Could not load the software catalog.");
            Logger.Error(ex);
            _notConfigured = false;
            LoadCatalogs([]);
            BackgroundDetail = SoftwareCatalog.FilePath + "\n" + ex.Message;
            ErrorMessage = ex switch
            {
                JsonException => CoreTools.Translate("The software catalog is not valid. Ask an administrator to fix it."),
                UnauthorizedAccessException or IOException =>
                    CoreTools.Translate("The software catalog could not be read. Ask an administrator to check its permissions."),
                _ => CoreTools.Translate("The software catalog could not be loaded. Ask an administrator to check {0}, then try again.", SoftwareCatalog.FilePath),
            };
        }
        finally
        {
            IsLoading = false;
            UpdateStates();
        }
    }

    private static IPackageManager? FindManager(CatalogEntry entry) =>
        PEInterface.Managers.FirstOrDefault(entry.MatchesManager);

    // The Installer packages manager (macOS .pkg, Windows .exe and .msi), which serves each catalog's pkgFeed as a source named by the catalog's id.
    private static readonly string[] PkgManagerIds = ["pkg", "installer"];

    /// <summary>
    /// Adds every package of a catalog's pkgFeed to that catalog, so that publishing a .pkg in the
    /// feed is all it takes to offer it. Entries written in the catalog itself come first and win.
    /// </summary>
    internal static async Task<CatalogDefinition[]> ExpandPkgFeedsAsync(CatalogDefinition[] catalogs)
    {
        if (catalogs.All(c => c.PkgFeed is null)
            || PEInterface.Managers.FirstOrDefault(m => PkgManagerIds.Contains(m.Id)) is not { } manager
            || !manager.IsReady())
            return catalogs;
        // A refresh shows what IT just published, not the feed listing kept for icon lookups.
        var published = await Task.Run(() =>
        {
            manager.RefreshPackageIndexes();
            return manager.FindPackages("");
        });
        return catalogs.Select(c => c.PkgFeed is null ? c : new CatalogDefinition
        {
            Id = c.Id,
            Name = c.Name,
            PkgFeed = c.PkgFeed,
            AllowUnsignedPackages = c.AllowUnsignedPackages,
            Packages =
            [
                .. c.Packages,
                .. published
                    .Where(p => string.Equals(p.Source.Name, c.Id, StringComparison.OrdinalIgnoreCase)
                        && !c.Packages.Any(e => e.Matches(p)))
                    .Select(CatalogEntry.FromPackage)
                    .OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase),
            ],
        }).ToArray();
    }

    private static (string Manager, string Source) SourceKey(CatalogEntry entry) =>
        ((FindManager(entry)?.Id ?? entry.ManagerName).ToUpperInvariant(), entry.Source.ToUpperInvariant());

    private void BuildSourceNodes()
    {
        SourceNodes.Clear();
        foreach (var pair in _catalogSources) _sourceSelectionMemory[pair.Key] = pair.Value.IsSelected;
        var previous = _sourceSelectionMemory.ToDictionary(p => p.Key, p => p.Value);
        _catalogSources.Clear();
        _updatingSourceSelection = true;
        try
        {
            foreach (var group in _allPackages.ToArray().GroupBy(t => SourceKey(t.Entry).Manager))
            {
                var root = new SourceTreeNode
                {
                    PackageName = FindManager(group.First().Entry)?.DisplayName ?? group.First().Entry.ManagerName,
                    IsExpanded = true,
                };
                foreach (var tile in group.DistinctBy(t => SourceKey(t.Entry)))
                {
                    var entry = tile.Entry;
                    var key = SourceKey(entry);
                    var child = new SourceTreeNode
                    {
                        PackageName = tile.SourceLabel,
                        Source = entry.Source,
                        IsSelected = previous.GetValueOrDefault(key, true),
                    };
                    root.Children.Add(child);
                    _catalogSources.Add(key, child);
                    child.PropertyChanged += (_, e) =>
                    {
                        if (_updatingSourceSelection || e.PropertyName != nameof(SourceTreeNode.IsSelected)) return;
                        _updatingSourceSelection = true;
                        try { root.IsSelected = root.Children.All(c => c.IsSelected); }
                        finally { _updatingSourceSelection = false; }
                        ApplyAvailabilityFilter();
                    };
                }
                root.IsSelected = root.Children.All(c => c.IsSelected);
                root.PropertyChanged += (_, e) =>
                {
                    if (_updatingSourceSelection || e.PropertyName != nameof(SourceTreeNode.IsSelected)) return;
                    _updatingSourceSelection = true;
                    try
                    {
                        foreach (var child in root.Children) child.IsSelected = root.IsSelected;
                    }
                    finally { _updatingSourceSelection = false; }
                    ApplyAvailabilityFilter();
                };
                SourceNodes.Add(root);
            }
        }
        finally { _updatingSourceSelection = false; }
    }

    [RelayCommand]
    private void SelectAllSources() => SetSourceSelection(true);

    [RelayCommand]
    private void ClearSourceSelection() => SetSourceSelection(false);

    private void SetSourceSelection(bool selected)
    {
        _updatingSourceSelection = true;
        try
        {
            foreach (var root in SourceNodes)
            {
                root.IsSelected = selected;
                foreach (var child in root.Children) child.IsSelected = selected;
            }
        }
        finally { _updatingSourceSelection = false; }
        ApplyAvailabilityFilter();
    }

    private CatalogPackageIdentitySet? _lastInstalled;

    private IManagerSource? FindSource(CatalogEntry entry) =>
        FindManager(entry) is { } manager && _sources.TryGetValue(manager.Id, out var sources)
            ? sources.FirstOrDefault(s => string.Equals(s.Name, entry.Source, StringComparison.OrdinalIgnoreCase))
            : null;

    public void UpdateStates()
    {
        if (_allPackages.Count == 0)
        {
            ApplyAvailabilityFilter();
            return;
        }
        var loader = InstalledPackagesLoader.Instance;
        // While the installed list reloads it is empty; keep the last complete one so installed
        // tiles don't flip to a disabled Install for the whole reload.
        bool inventoryLive = loader is { HasPendingInitialLoad: false, IsLoading: false };
        var installed = inventoryLive
            ? _lastInstalled = new CatalogPackageIdentitySet(loader!.Packages, installedInventory: true)
            : _lastInstalled ?? new CatalogPackageIdentitySet([], installedInventory: true);
        var upgradable = new CatalogPackageIdentitySet(
            UpgradablePackagesLoader.Instance?.Packages ?? [], installedInventory: true);
        var pending = new CatalogPackageIdentitySet(
            Avalonia.Infrastructure.AvaloniaOperationRegistry.OperationViewModels
                .Select(o => o.Operation)
                .OfType<PackageOperation>()
                .Where(o => o.Status is OperationStatus.InQueue or OperationStatus.Running)
                .Select(o => o.Package));
        bool sourcesMissing = false;
        foreach (var tile in _allPackages)
        {
            var manager = FindManager(tile.Entry);
            var source = FindSource(tile.Entry);
            if (manager is not null && manager.IsReady() && !_sources.ContainsKey(manager.Id))
                sourcesMissing = true;
            string? unavailableReason = manager is null
                ? CoreTools.Translate("The package manager {0} is not available on this platform.", tile.Entry.ManagerName)
                : !manager.IsEnabled()
                    ? CoreTools.Translate("The package manager {0} is disabled. Enable it in Package Managers.", manager.DisplayName)
                    : !manager.IsReady()
                        ? CoreTools.Translate("The package manager {0} is not available or is still initializing.", manager.DisplayName)
                        : source is not null ? null
                        // Every manager lists at least its default source: none means the listing failed.
                        : _sources.TryGetValue(manager.Id, out var listed) && listed.Count == 0
                            ? CoreTools.Translate("The sources of {0} could not be listed. Check the log, then try again.", manager.DisplayName)
                            : CoreTools.Translate("The source {0} is not enabled for {1}. Add it in Package Managers.", tile.Entry.Source, manager.DisplayName);
            tile.HasUpdate = upgradable.Contains(tile.Entry);
            tile.UpdateState(
                installed.Contains(tile.Entry),
                unavailableReason,
                (inventoryLive || _lastInstalled is not null) && manager is { LastInstalledListingFailed: false },
                pending.Contains(tile.Entry));
        }
        ApplyAvailabilityFilter();
        if (sourcesMissing && !IsLoading && PEInterface.AreManagersLoaded)
            _ = RefreshAsync();
    }

    internal void ApplyAvailabilityFilter()
    {
        string query = Query.Trim();
        var visible = _allPackages.Where(p => (!HideUnavailablePackages || p.UnavailableReason is null)
            && IsSelectedByFilters(p)
            && MatchesQuery(p.Entry, query)).ToArray();
        // Without catalog groups, a package listed in several catalogs would show up repeatedly.
        if (!GroupByCatalog)
            visible = visible.DistinctBy(p => (p.Entry.Id.ToUpperInvariant(), SourceKey(p.Entry))).ToArray();
        visible = SortTiles(visible);
        if (!Packages.SequenceEqual(visible))
        {
            Packages.Clear();
            foreach (var package in visible)
                Packages.Add(package);
        }
        RebuildGroups();
        OnPropertyChanged(nameof(HasHiddenPackages));
        OnPropertyChanged(nameof(EmptyMessage));
        OnPropertyChanged(nameof(IsEmpty));
        NotifyBackgroundTextChanged();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortFieldName))]
    private CatalogSortField _sortField = LoadSortField();

    [ObservableProperty]
    private bool _sortAscending =
        !Settings.GetDictionaryItem<string, bool>(Settings.K.PackageListSortDescending, ViewModeSettingKey);

    private static CatalogSortField LoadSortField()
    {
        int saved = Settings.GetDictionaryItem<string, int>(Settings.K.PackageListSortFieldIndex, ViewModeSettingKey);
        return Enum.IsDefined((CatalogSortField)saved) ? (CatalogSortField)saved : CatalogSortField.Name;
    }

    public string SortFieldName => SortField switch
    {
        CatalogSortField.Id => CoreTools.Translate("Id"),
        CatalogSortField.Source => CoreTools.Translate("Source"),
        _ => CoreTools.Translate("Name"),
    };

    partial void OnSortFieldChanged(CatalogSortField value)
    {
        Settings.SetDictionaryItem(Settings.K.PackageListSortFieldIndex, ViewModeSettingKey, (int)value);
        ApplyAvailabilityFilter();
    }

    partial void OnSortAscendingChanged(bool value)
    {
        SaveSortDirection(value);
        ApplyAvailabilityFilter();
    }

    private static void SaveSortDirection(bool ascending) =>
        Settings.SetDictionaryItem(Settings.K.PackageListSortDescending, ViewModeSettingKey, !ascending);

    [RelayCommand] private void SortByName() => SortField = CatalogSortField.Name;
    [RelayCommand] private void SortById() => SortField = CatalogSortField.Id;
    [RelayCommand] private void SortBySource() => SortField = CatalogSortField.Source;
    [RelayCommand] private void SetSortAscending() => SortAscending = true;
    [RelayCommand] private void SetSortDescending() => SortAscending = false;

    // Clicking the active column's header flips the direction; a new column starts ascending.
    public void ToggleSort(CatalogSortField field)
    {
        if (SortField == field) SortAscending = !SortAscending;
        else
        {
            _sortAscending = true;
            SaveSortDirection(true);
            OnPropertyChanged(nameof(SortAscending));
            SortField = field;
        }
    }

    private static string SortKey(CatalogSortField field, CatalogTileViewModel t) => field switch
    {
        CatalogSortField.Id => t.Entry.Id,
        CatalogSortField.Source => t.ManagerDisplayName + "/" + t.SourceLabel,
        _ => t.Entry.Name,
    };

    public static int CompareTiles(CatalogSortField field, CatalogTileViewModel a, CatalogTileViewModel b)
        => StringComparer.CurrentCultureIgnoreCase.Compare(SortKey(field, a), SortKey(field, b));

    private CatalogTileViewModel[] SortTiles(CatalogTileViewModel[] tiles)
    {
        var field = SortField;
        string Key(CatalogTileViewModel t) => SortKey(field, t);
        var comparer = StringComparer.CurrentCultureIgnoreCase;
        return (SortAscending ? tiles.OrderBy(Key, comparer) : tiles.OrderByDescending(Key, comparer)).ToArray();
    }

    private void RebuildGroups()
    {
        var groups = GroupByCatalog
            ? CatalogNodes
                .Select(n => Packages.Where(p => string.Equals(p.CatalogId, n.Definition.Id, StringComparison.OrdinalIgnoreCase)).ToArray())
                .Where(g => g.Length > 0)
                .Select(g => new CatalogGroupViewModel(g[0].CatalogName, g, true))
                .ToArray()
            : Packages.Count > 0 ? [new CatalogGroupViewModel("", Packages.ToArray(), false)] : [];
        if (Groups.Count == groups.Length
            && Groups.Zip(groups).All(p => p.First.Name == p.Second.Name
                && p.First.HasHeader == p.Second.HasHeader
                && p.First.Packages.SequenceEqual(p.Second.Packages)))
            return;
        Groups.Clear();
        foreach (var group in groups) Groups.Add(group);
    }

    private bool MatchesQuery(CatalogEntry entry, string query) => query.Length == 0 || SearchMode switch
    {
        SearchMode.Name => entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase),
        SearchMode.Id => entry.Id.Contains(query, StringComparison.OrdinalIgnoreCase),
        SearchMode.Exact => string.Equals(entry.Name, query, StringComparison.OrdinalIgnoreCase)
            || string.Equals(entry.Id, query, StringComparison.OrdinalIgnoreCase),
        _ => entry.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
            || entry.Id.Contains(query, StringComparison.OrdinalIgnoreCase),
    };

    // Tiles share this command; each tile's IsBusy guards its own button, so other tiles stay usable.
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task InstallAsync(CatalogTileViewModel? tile)
    {
        UpdateStates();
        if (tile is null || !tile.CanInstall) return;
        tile.IsBusy = true;
        try
        {
            var manager = FindManager(tile.Entry)
                ?? throw new InvalidOperationException("The catalog package manager is unavailable.");
            var matches = await Task.Run(() => manager.FindPackages(tile.Entry.Id));
            var package = matches.FirstOrDefault(tile.Entry.Matches)
                ?? throw new InvalidOperationException($"Package {tile.Entry.Id} was not found in {tile.Entry.ManagerName}/{tile.Entry.Source}.");
            UpdateStates();
            if (tile.UnavailableReason is { } reason)
            {
                MainWindow.Instance?.ShowBanner(CoreTools.Translate("Unable to install {0}", tile.Entry.Name), reason,
                    MainWindow.RuntimeNotificationLevel.Error);
                return;
            }
            // Installed or queued meanwhile: nothing left to do.
            if (tile.IsInstalled || !tile.InventoryKnown || tile.IsPending) return;
            await PackagesPageViewModel.LaunchInstall([package], version: PinnedVersion(tile.Entry, manager));
        }
        catch (Exception ex)
        {
            Logger.Error($"Could not install catalog package {tile.Entry.Id}.");
            Logger.Error(ex);
            MainWindow.Instance?.ShowBanner(
                CoreTools.Translate("Unable to install {0}", tile.Entry.Name),
                CoreTools.Translate("The catalog package could not be found or installed. Check that its identifier and source are correct, then try again."),
                MainWindow.RuntimeNotificationLevel.Error);
        }
        finally
        {
            tile.IsBusy = false;
            UpdateStates();
        }
    }
}

public enum CatalogSortField { Name, Id, Source }

public partial class SoftwareCatalogViewModel
{
    private static string? PinnedVersion(CatalogEntry entry, IPackageManager manager)
    {
        if (entry.Version.Length == 0) return null;
        if (manager.Capabilities.SupportsCustomVersions) return entry.Version;
        Logger.Warn($"{manager.DisplayName} cannot install a specific version; installing the latest {entry.Id} instead of {entry.Version}.");
        return null;
    }

    private static IPackage? FindInstalled(CatalogEntry entry) =>
        InstalledPackagesLoader.Instance?.Packages.FirstOrDefault(entry.MatchesInstalled);

    internal async Task UpdateTileAsync(CatalogTileViewModel tile)
    {
        if (FindInstalled(tile.Entry) is not { } installed) return;
        await InstalledPackagesPage.LaunchUpdate(installed);
        UpdateStates();
    }

    internal async Task UninstallTileAsync(CatalogTileViewModel tile)
    {
        if (FindInstalled(tile.Entry) is not { } installed || MainWindow.Instance is not { } window) return;
        if (!await UninstallConfirmationDialog.ConfirmAsync(window, installed)) return;
        await InstalledPackagesPage.LaunchUninstall([installed]);
        UpdateStates();
    }

    internal async Task ShowTileDetailsAsync(CatalogTileViewModel tile)
    {
        if (MainWindow.Instance is not { } window) return;
        // The details window's main action follows the tile: update, uninstall, or install (the pin as version).
        IPackage? package = null;
        string? pin = null;
        var installed = FindInstalled(tile.Entry);
        var role = OperationType.Install;
        if (installed is not null)
        {
            var upgradable = tile.HasUpdate ? installed.GetUpgradablePackage() : null;
            package = upgradable ?? installed;
            role = upgradable is null ? OperationType.Uninstall : OperationType.Update;
        }
        else if (FindManager(tile.Entry) is { } manager && FindSource(tile.Entry) is { } source)
        {
            package = new Package(tile.Entry.Name, tile.Entry.Id, tile.Entry.Version, source, manager);
            pin = tile.Entry.Version.Length > 0 && manager.Capabilities.SupportsCustomVersions ? tile.Entry.Version : null;
        }
        if (package is null)
        {
            window.ShowBanner(CoreTools.Translate("Package unavailable"),
                tile.UnavailableReason ?? CoreTools.Translate("Enable this package's manager and source to view its details."),
                MainWindow.RuntimeNotificationLevel.Error);
            return;
        }
        var dialog = new PackageDetailsWindow(package, role, TEL_InstallReferral.DIRECT_SEARCH) { InstallVersion = pin };
        await dialog.ShowDialog(window);
        if (!dialog.ShouldProceedWithOperation) return;
        switch (role)
        {
            case OperationType.Install:
                await InstallCommand.ExecuteAsync(tile);
                break;
            case OperationType.Update:
                await InstalledPackagesPage.LaunchUpdate(installed);
                break;
            case OperationType.Uninstall:
                await InstalledPackagesPage.LaunchUninstall([installed!]);
                break;
        }
        UpdateStates();
    }
}

public partial class CatalogFilterNode(CatalogDefinition definition) : ObservableObject
{
    public CatalogDefinition Definition { get; } = definition;
    public string Name => Definition.Name;

    [ObservableProperty]
    private bool _isSelected = true;
}

public sealed class CatalogGroupViewModel(string name, IReadOnlyList<CatalogTileViewModel> packages, bool hasHeader)
{
    public string Name { get; } = name;
    public IReadOnlyList<CatalogTileViewModel> Packages { get; } = packages;
    public bool HasHeader { get; } = hasHeader;
}

public partial class CatalogTileViewModel(CatalogEntry entry, CatalogDefinition? catalog = null) : ViewModelBase
{
    public CatalogEntry Entry { get; } = entry;
    public string CatalogId { get; } = catalog?.Id ?? "";
    public string CatalogName { get; } = catalog?.Name ?? "";

    // The file may name the manager by id or alias ("winps"); show the name users know.
    public string ManagerDisplayName { get; } =
        PEInterface.Managers.FirstOrDefault(entry.MatchesManager)?.DisplayName ?? entry.ManagerName;

    // The source as people know it: a pkg feed shows its catalog's name rather than the id naming it.
    public string SourceLabel { get; init; } = entry.Source;

    public string SourceDisplay => $"{ManagerDisplayName}: {SourceLabel}";

    private readonly bool _canUninstall =
        PEInterface.Managers.FirstOrDefault(entry.MatchesManager)?.Capabilities.CanUninstall ?? true;

    public bool ShowUninstall => IsInstalled && _canUninstall;

    // An administrator can pin the version the catalog installs; empty means the latest.
    public bool HasPinnedVersion => Entry.Version.Length > 0;
    public string PinnedVersionText => HasPinnedVersion ? CoreTools.Translate("Version {0}", Entry.Version) : "";

    // Views that show no status line surface it, with the source and pin, in the tooltip.
    public string ToolTipText => string.Join("\n",
        new[] { SourceDisplay, PinnedVersionText, StatusText }.Where(line => line.Length > 0));

    internal bool IconRequested { get; set; }

    internal SoftwareCatalogViewModel? Owner { get; init; }

    // An installed catalog package can be updated, uninstalled or inspected instead of being a dead end.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowUpdate))]
    [NotifyPropertyChangedFor(nameof(ShowInstalledLabel))]
    private bool _hasUpdate;

    public bool ShowUpdate => IsInstalled && HasUpdate && !IsPending;
    public bool ShowInstalledLabel => IsInstalled && !ShowUpdate;

    [RelayCommand]
    private Task Update() => Owner?.UpdateTileAsync(this) ?? Task.CompletedTask;

    [RelayCommand]
    private Task Uninstall() => Owner?.UninstallTileAsync(this) ?? Task.CompletedTask;

    [RelayCommand]
    private Task Details() => Owner?.ShowTileDetailsAsync(this) ?? Task.CompletedTask;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCustomIcon))]
    private Bitmap? _iconBitmap;

    public bool HasCustomIcon => IconBitmap is not null;

    internal async Task LoadIconAsync(IPackage package)
    {
        try
        {
            var bitmap = await PackageWrapper.LoadSharedIconAsync(package);
            await Dispatcher.UIThread.InvokeAsync(() => IconBitmap = bitmap);
        }
        catch (Exception ex)
        {
            Logger.Warn($"Could not load the catalog icon for {Entry.Id}.");
            Logger.Warn(ex);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowInstall))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(ShowUpdate))]
    [NotifyPropertyChangedFor(nameof(ShowInstalledLabel))]
    [NotifyPropertyChangedFor(nameof(ShowUninstall))]
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleOpacity))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private string? _unavailableReason;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private bool _inventoryKnown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    [NotifyPropertyChangedFor(nameof(ShowUpdate))]
    [NotifyPropertyChangedFor(nameof(ShowInstalledLabel))]
    private bool _isPending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(ToolTipText))]
    private bool _isBusy;

    public bool ShowInstall => !IsInstalled;
    public bool CanInstall => !IsInstalled && UnavailableReason is null && InventoryKnown && !IsPending && !IsBusy;
    public double TitleOpacity => UnavailableReason is null ? 1 : 0.45;
    public string StatusText => UnavailableReason
        ?? (IsBusy ? CoreTools.Translate("Loading packages")
            : IsPending ? CoreTools.Translate("Installing")
            : !InventoryKnown ? CoreTools.Translate("Waiting for installed package information. Refresh Installed Packages if this persists.")
            : "");

    public void UpdateState(bool installed, string? unavailableReason, bool inventoryKnown, bool pending)
    {
        IsInstalled = installed;
        UnavailableReason = unavailableReason;
        InventoryKnown = inventoryKnown;
        IsPending = pending;
    }
}
