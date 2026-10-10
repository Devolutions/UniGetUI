using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Models;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
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
    private readonly Dictionary<string, IReadOnlyList<IManagerSource>> _sources = new(StringComparer.OrdinalIgnoreCase);
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

    partial void OnGroupByCatalogChanged(bool value) => RebuildGroups();

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
    private int _viewModeIndex = 3;

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
    public string EmptyMessage => CoreTools.Translate(_allPackages.Count > 0
        ? "No packages are available with the current filters."
        : "No packages to display.");

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
    }

    private bool IsSelectedByFilters(CatalogTileViewModel tile) =>
        _catalogSelection.TryGetValue(tile.CatalogId, out var catalog) && catalog.IsSelected
        && _catalogSources.TryGetValue(SourceKey(tile.Entry), out var source) && source.IsSelected;

    private readonly Dictionary<string, CatalogFilterNode> _catalogSelection = new(StringComparer.OrdinalIgnoreCase);

    public void LoadCatalogs(IReadOnlyList<CatalogDefinition> catalogs)
    {
        var previous = CatalogNodes.ToDictionary(n => n.Definition.Id, n => n.IsSelected, StringComparer.OrdinalIgnoreCase);
        _catalogSelection.Clear();
        CatalogNodes.Clear();
        _allPackages.Clear();
        _updatingCatalogSelection = true;
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
                {
                    var tile = new CatalogTileViewModel(entry, catalog);
                    _allPackages.Add(tile);
                    var manager = FindManager(entry);
                    var source = FindSource(entry);
                    if (manager is null || source is null) continue;
                    var package = InstalledPackagesLoader.Instance?.Packages.FirstOrDefault(entry.MatchesInstalled)
                        ?? new Package(entry.Name, entry.Id, entry.Version, source, manager);
                    _ = tile.LoadIconAsync(package);
                }
            }
        }
        finally { _updatingCatalogSelection = false; }
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

    [RelayCommand]
    private async Task EditAsync()
    {
        try
        {
            var owner = MainWindow.Instance
                ?? throw new InvalidOperationException("The catalog editor requires an application window.");
            owner.Navigate(PageType.CatalogEditor);
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            Logger.Error("Could not open the software catalog editor.");
            Logger.Error(ex);
            ErrorMessage = CoreTools.Translate("The catalog editor could not be opened: {0}", ex.Message);
        }
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        ErrorMessage = "";
        UpdateStates();
        try
        {
            var catalogs = await SoftwareCatalog.LoadAsync();
            _sources.Clear();

            foreach (var manager in catalogs.SelectMany(c => c.Packages).Select(FindManager).OfType<IPackageManager>().Distinct())
            {
                if (!manager.IsReady()) continue;
                _sources[manager.Id] = manager.Capabilities.SupportsCustomSources
                    ? await Task.Run(manager.SourcesHelper.GetSources)
                    : [manager.DefaultSource];
            }

            LoadCatalogs(catalogs);
        }
        catch (Exception ex)
        {
            Logger.Error("Could not load the software catalog.");
            Logger.Error(ex);
            LoadCatalogs([]);
            ErrorMessage = CoreTools.Translate("The software catalog could not be loaded. Ask an administrator to check {0}, then try again.", SoftwareCatalog.FilePath);
        }
        finally
        {
            IsLoading = false;
            UpdateStates();
        }
    }

    private static IPackageManager? FindManager(CatalogEntry entry) =>
        PEInterface.Managers.FirstOrDefault(entry.MatchesManager);

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
            var entries = _allPackages.Select(t => t.Entry).ToArray();
            foreach (var group in entries.GroupBy(p => SourceKey(p).Manager))
            {
                var root = new SourceTreeNode
                {
                    PackageName = FindManager(group.First())?.DisplayName ?? group.First().ManagerName,
                    IsExpanded = true,
                };
                foreach (var entry in group.DistinctBy(SourceKey))
                {
                    var key = SourceKey(entry);
                    var child = new SourceTreeNode
                    {
                        PackageName = entry.Source,
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
        var installed = new CatalogPackageIdentitySet(loader?.Packages ?? [], installedInventory: true);
        var pending = new CatalogPackageIdentitySet(
            Avalonia.Infrastructure.AvaloniaOperationRegistry.OperationViewModels
                .Select(o => o.Operation)
                .OfType<PackageOperation>()
                .Where(o => o.Status is OperationStatus.InQueue or OperationStatus.Running)
                .Select(o => o.Package));
        foreach (var tile in _allPackages)
        {
            var manager = FindManager(tile.Entry);
            var source = FindSource(tile.Entry);
            string? unavailableReason = manager is null
                ? CoreTools.Translate("The package manager {0} is not available on this platform.", tile.Entry.ManagerName)
                : !manager.IsEnabled()
                    ? CoreTools.Translate("The package manager {0} is disabled. Enable it in Package Managers.", manager.DisplayName)
                    : !manager.IsReady()
                        ? CoreTools.Translate("The package manager {0} is not available or is still initializing.", manager.DisplayName)
                        : source is null
                            ? CoreTools.Translate("The source {0} is not enabled for {1}. Add it in Package Managers.", tile.Entry.Source, manager.DisplayName)
                            : null;
            tile.UpdateState(
                installed.Contains(tile.Entry),
                unavailableReason,
                !IsLoading && loader is { HasPendingInitialLoad: false, IsLoading: false }
                    && manager is { LastInstalledListingFailed: false },
                pending.Contains(tile.Entry));
        }
        ApplyAvailabilityFilter();
    }

    internal void ApplyAvailabilityFilter()
    {
        string query = Query.Trim();
        var visible = _allPackages.Where(p => (!HideUnavailablePackages || p.UnavailableReason is null)
            && IsSelectedByFilters(p)
            && MatchesQuery(p.Entry, query)).ToArray();
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
    private CatalogSortField _sortField = CatalogSortField.Name;

    [ObservableProperty]
    private bool _sortAscending = true;

    public string SortFieldName => SortField switch
    {
        CatalogSortField.Id => CoreTools.Translate("Id"),
        CatalogSortField.Source => CoreTools.Translate("Source"),
        _ => CoreTools.Translate("Name"),
    };

    partial void OnSortFieldChanged(CatalogSortField value) => ApplyAvailabilityFilter();
    partial void OnSortAscendingChanged(bool value) => ApplyAvailabilityFilter();

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
            OnPropertyChanged(nameof(SortAscending));
            SortField = field;
        }
    }

    private static string SortKey(CatalogSortField field, CatalogTileViewModel t) => field switch
    {
        CatalogSortField.Id => t.Entry.Id,
        CatalogSortField.Source => t.Entry.ManagerName + "/" + t.Entry.Source,
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

    [RelayCommand]
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
            if (tile.IsInstalled || tile.UnavailableReason is not null || !tile.InventoryKnown || tile.IsPending) return;
            await PackagesPageViewModel.LaunchInstall([package]);
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
    private bool _isInstalled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TitleOpacity))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private string? _unavailableReason;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _inventoryKnown;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private bool _isPending;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    [NotifyPropertyChangedFor(nameof(StatusText))]
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
