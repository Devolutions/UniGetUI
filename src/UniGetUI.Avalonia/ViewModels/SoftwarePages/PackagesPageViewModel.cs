using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.Views.Controls;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Telemetry;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Avalonia.ViewModels.Pages;

public enum SearchMode { Both, Name, Id, Exact, Similar }

public enum PackageViewMode { List = 0, Grid = 1, Icons = 2 }

public enum ReloadReason
{
    FirstRun,
    Automated,
    Manual,
    External,
}

public struct PackagesPageData
{
    public bool DisableAutomaticPackageLoadOnStart;
    public bool MegaQueryBlockEnabled;
    public bool PackagesAreCheckedByDefault;
    public bool ShowLastLoadTime;
    public bool DisableSuggestedResultsRadio;
    public bool DisableFilterOnQueryChange;
    public bool DisableReload;

    public OperationType PageRole;
    public AbstractPackageLoader Loader;

    public string PageName;
    public string PageTitle;
    public string IconName;   // SVG filename without extension, e.g. "search"

    public string NoPackages_BackgroundText;
    public string NoPackages_SourcesText;
    public string NoPackages_SubtitleText_Base;
    public string MainSubtitle_StillLoading;
    public string NoMatches_BackgroundText;
}

/// <summary>
/// Represents a node in the sources tree (replaces WinUI TreeViewNode).
/// </summary>
public class SourceTreeNode : INotifyPropertyChanged
{
    public string? PackageName { get; set; }
    public string? PackageID { get; init; }
    public string? Version { get; init; }
    public string? Source { get; init; }
    public List<SourceTreeNode> Children { get; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { _isSelected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
    }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded))); }
    }
}

public partial class PackagesPageViewModel : ViewModelBase
{
    public double FilterPaneColumnWidth => IsFilterPaneOpen ? 220.0 : 0.0;
    partial void OnIsFilterPaneOpenChanged(bool _) => OnPropertyChanged(nameof(FilterPaneColumnWidth));

    // ─── Static config (set once in constructor) ──────────────────────────────
    public readonly string PageName;
    public readonly bool MegaQueryBoxEnabled;
    public readonly bool DisableFilterOnQueryChange;
    public readonly bool DisableReload;
    public readonly bool RoleIsUpdateLike;
    public bool SimilarSearchEnabled { get; private set; }
    public readonly string NoPackagesText;
    public readonly string NoMatchesText;
    public readonly string SearchBoxPlaceholder;
    private readonly string _noPackagesSubtitleBase;
    private readonly string _stillLoadingSubtitle;
    private readonly bool _showLastCheckedTime;
    private DateTime _lastLoadTime = DateTime.Now;

    protected AbstractPackageLoader Loader;

    // ─── Observable properties ────────────────────────────────────────────────
    [ObservableProperty] private string _pageTitle = "";
    [ObservableProperty] private string _pageIconPath = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private bool _backgroundTextVisible;
    [ObservableProperty] private string _backgroundText = "";
    [ObservableProperty] private bool _sourcesPlaceholderVisible = true;
    [ObservableProperty] private bool _sourcesTreeVisible;
    [ObservableProperty] private bool _megaQueryVisible;
    [ObservableProperty] private string _megaQueryText = "";
    [ObservableProperty] private string _globalQueryText = "";
    [ObservableProperty] private bool _newVersionHeaderVisible;
    [ObservableProperty] private bool _reloadButtonVisible;
    [ObservableProperty] private bool _isFilterPaneOpen;
    [ObservableProperty] private PackageViewMode _viewMode;
    [ObservableProperty] private int _sortFieldIndex;
    [ObservableProperty] private bool _sortAscending = true;
    [ObservableProperty] private bool _instantSearch = true;
    [ObservableProperty] private bool _upperLowerCase;
    [ObservableProperty] private bool _ignoreSpecialChars = true;
    [ObservableProperty] private SearchMode _searchMode = SearchMode.Both;
    [ObservableProperty] private bool? _allPackagesChecked;
    [ObservableProperty] private string _nameHeaderText = "";
    [ObservableProperty] private string _idHeaderText = "";
    [ObservableProperty] private string _versionHeaderText = "";
    [ObservableProperty] private string _newVersionHeaderText = "";
    [ObservableProperty] private string _sourceHeaderText = "";

    // ─── Collections ──────────────────────────────────────────────────────────
    public ObservablePackageCollection FilteredPackages { get; } = new();
    public AvaloniaList<SourceTreeNode> SourceNodes { get; } = new();
    public AvaloniaList<object> ToolBarItems { get; } = new();

    // ─── Internal state ───────────────────────────────────────────────────────
    private string _searchQuery = "";
    public string QueryBackup { get; set; } = "";

    private readonly ObservableCollection<PackageWrapper> _wrappedPackages = new();
    protected List<IPackageManager> UsedManagers = [];
    protected ConcurrentDictionary<IPackageManager, List<IManagerSource>> UsedSourcesForManager = new();
    protected ConcurrentDictionary<IPackageManager, SourceTreeNode> RootNodeForManager = new();
    protected ConcurrentDictionary<IManagerSource, SourceTreeNode> NodesForSources = new();
    private readonly SourceTreeNode _localPackagesNode = new() { PackageName = "local" };

    // ─── Events (replace abstract methods) ───────────────────────────────────
    public event Action<ReloadReason>? PackagesLoaded;
    public event Action? PackageCountUpdated;
    public event Action<IPackage>? ShowingContextMenu;
    public event Action? FocusListRequested;

    // ─── Events: view-side dialog/navigation requests ─────────────────────────
    /// <summary>Fired when the ViewModel wants to navigate to the Help page.</summary>
    public event Action? HelpRequested;
    /// <summary>Fired when the ViewModel wants to show the Manage-Ignored-Updates dialog.</summary>
    public event Action? ManageIgnoredRequested;
    /// <summary>
    /// Fired when the ViewModel has built a share URL.
    /// Arguments: (packageName, url). Both null means "nothing to share".
    /// </summary>
    public event Action<string?, string?>? SharePackageRequested;

    // ─── Constructor ─────────────────────────────────────────────────────────
    public PackagesPageViewModel(PackagesPageData data)
    {
        PageName = data.PageName;
        PageTitle = data.PageTitle;
        PageIconPath = $"avares://UniGetUI.Avalonia/Assets/Symbols/{data.IconName}.svg";
        DisableFilterOnQueryChange = data.DisableFilterOnQueryChange;
        MegaQueryBoxEnabled = data.MegaQueryBlockEnabled;
        DisableReload = data.DisableReload;
        _showLastCheckedTime = data.ShowLastLoadTime;
        NoPackagesText = data.NoPackages_BackgroundText;
        NoMatchesText = data.NoMatches_BackgroundText;
        _noPackagesSubtitleBase = data.NoPackages_SubtitleText_Base;
        _stillLoadingSubtitle = data.MainSubtitle_StillLoading;
        SimilarSearchEnabled = !data.DisableSuggestedResultsRadio;
        RoleIsUpdateLike = data.PageRole == OperationType.Update;
        NewVersionHeaderVisible = RoleIsUpdateLike;
        ReloadButtonVisible = !DisableReload;
        SearchBoxPlaceholder = CoreTools.Translate("Search for packages");

        AllPackagesChecked = data.PackagesAreCheckedByDefault;

        Loader = data.Loader;
        Loader.StartedLoading += Loader_StartedLoading;
        Loader.FinishedLoading += Loader_FinishedLoading;
        Loader.PackagesChanged += Loader_PackagesChanged;

        _wrappedPackages.CollectionChanged += (_, _) => { /* invalidate query cache if needed */ };

        InstantSearch = !Settings.GetDictionaryItem<string, bool>(Settings.K.DisableInstantSearch, PageName);

        var savedMode = Settings.GetDictionaryItem<string, int>(Settings.K.PackageListViewMode, PageName);
        ViewMode = Enum.IsDefined(typeof(PackageViewMode), savedMode)
            ? (PackageViewMode)savedMode
            : PackageViewMode.List;

        _localPackagesNode.PackageName = CoreTools.Translate("Local");

        if (Loader.IsLoading)
            Loader_StartedLoading(this, EventArgs.Empty);
        else
        {
            Loader_FinishedLoading(this, EventArgs.Empty);
            FilterPackages();
        }
        Loader_PackagesChanged(this, new(false, [], []));

        UpdateHeaderTexts();

        if (MegaQueryBoxEnabled)
        {
            MegaQueryVisible = true;
            BackgroundTextVisible = false;
        }

        // Toolbar is generated by the View after construction (see AbstractPackagesPage ctor)
    }

    public Button AddToolbarButton(string svgName, string label, Action onClick, bool showLabel = true)
    {
        var icon = new SvgIcon
        {
            Path = $"avares://UniGetUI.Avalonia/Assets/Symbols/{svgName}.svg",
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        content.Children.Add(icon);
        if (showLabel)
        {
            content.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        var btn = new Button
        {
            Height = 36,
            Padding = new Thickness(8, 4),
            CornerRadius = new CornerRadius(4),
            Content = content,
        };
        ToolTip.SetTip(btn, label);
        btn.Click += (_, _) => onClick();
        ToolBarItems.Add(btn);
        return btn;
    }

    /// <summary>Adds a thin vertical separator to the toolbar.</summary>
    public void AddToolbarSeparator()
    {
        ToolBarItems.Add(new Separator
        {
            Width = 1,
            Height = 30,
            Margin = new Thickness(4, 4),
            Background = Application.Current?.FindResource("AppBorderBrush") as IBrush
                         ?? new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
        });
    }

    public async Task ShowInfoDialog(Window owner, string title, string message)
    {
        object? bgResource = null;
        Application.Current?.Resources.TryGetResource("AppWindowBackground", Application.Current.ActualThemeVariant, out bgResource);
        var dialog = new Window
        {
            Width = 460,
            Height = 180,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Title = title,
            Background = bgResource as IBrush,
        };

        var okBtn = new Button
        {
            Content = CoreTools.Translate("OK"),
            MinWidth = 80,
            CornerRadius = new CornerRadius(4),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        okBtn.Classes.Add("accent");
        okBtn.Click += (_, _) => dialog.Close();

        var root = new Grid
        {
            Margin = new Thickness(20),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 12,
        };
        var titleBlock = new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
        };
        var msgBlock = new TextBlock
        {
            Text = message,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
        };
        Grid.SetRow(titleBlock, 0);
        Grid.SetRow(msgBlock, 1);
        Grid.SetRow(okBtn, 2);
        root.Children.Add(titleBlock);
        root.Children.Add(msgBlock);
        root.Children.Add(okBtn);
        dialog.Content = root;

        await dialog.ShowDialog(owner);
    }

    // ─── Loader events ────────────────────────────────────────────────────────
    private void Loader_PackagesChanged(object? sender, PackagesChangedEvent e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Loader_PackagesChanged(sender, e));
            return;
        }

        if (e.ProceduralChange)
        {
            foreach (var pkg in e.AddedPackages)
            {
                if (_wrappedPackages.Any(w => w.Package.Equals(pkg))) continue;
                _wrappedPackages.Add(new PackageWrapper(pkg, this));
                AddPackageToSourcesList(pkg);
            }
            var toRemove = _wrappedPackages.Where(w => e.RemovedPackages.Contains(w.Package)).ToList();
            foreach (var wrapper in toRemove) { wrapper.Dispose(); _wrappedPackages.Remove(wrapper); }
        }
        else
        {
            foreach (var w in _wrappedPackages) w.Dispose();
            _wrappedPackages.Clear();
            ClearSourcesList();
            foreach (var pkg in Loader.Packages)
            {
                _wrappedPackages.Add(new PackageWrapper(pkg, this));
                AddPackageToSourcesList(pkg);
            }
        }
        FilterPackages();
    }

    private void Loader_FinishedLoading(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Loader_FinishedLoading(sender, e));
            return;
        }
        IsLoading = false;
        _lastLoadTime = DateTime.Now;
        FilterPackages();
        PackagesLoaded?.Invoke(ReloadReason.External);
    }

    private void Loader_StartedLoading(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Loader_StartedLoading(sender, e));
            return;
        }
        IsLoading = true;
        UpdateSubtitle();
    }

    // ─── Search & filter ──────────────────────────────────────────────────────
    partial void OnGlobalQueryTextChanged(string value)
    {
        _searchQuery = value;
        if (MegaQueryBoxEnabled)
        {
            if (string.IsNullOrEmpty(value))
            {
                MegaQueryText = "";
                MegaQueryVisible = true;
                Loader?.ClearPackages(emitFinishSignal: false);
            }
            else
            {
                MegaQueryText = value;
                MegaQueryVisible = false;
            }
            return;
        }
        if (!DisableFilterOnQueryChange && InstantSearch)
            FilterPackages();
    }

    [RelayCommand]
    public void SubmitSearch()
    {
        string query = _searchQuery = GlobalQueryText = MegaQueryText.Trim();
        MegaQueryVisible = false;

        if (Loader is DiscoverablePackagesLoader discoverLoader)
        {
            Loader.ClearPackages(emitFinishSignal: false);
            _ = discoverLoader.ReloadPackages(query);
        }
        else
        {
            FilterPackages(fromQuery: true);
        }
    }

    public void FilterPackages(bool fromQuery = false)
    {
        var filters = new List<Func<string, string>>();
        if (!UpperLowerCase) filters.Add(FilterHelpers.NormalizeCase);
        if (IgnoreSpecialChars) filters.Add(FilterHelpers.NormalizeSpecialCharacters);

        string query = _searchQuery;
        foreach (var f in filters) query = f(query);

        Func<IPackage, bool> matchFunc = SearchMode switch
        {
            SearchMode.Name => pkg => FilterHelpers.NameContains(pkg, query, filters),
            SearchMode.Id => pkg => FilterHelpers.IdContains(pkg, query, filters),
            SearchMode.Exact => pkg => FilterHelpers.NameOrIdExactMatch(pkg, query, filters),
            SearchMode.Similar => _ => true,
            _ => pkg => FilterHelpers.NameOrIdContains(pkg, query, filters),
        };

        var selectedSources = GetSelectedSourceNodes();
        Func<IPackage, bool> sourceFilter = SourceNodes.Count == 0
            ? _ => true   // sources not yet loaded — show everything
            : selectedSources.Count == 0
                ? _ => false  // sources loaded but none selected — show nothing
                : pkg => selectedSources.Any(n =>
                    n.PackageName.TrimEnd('.', ' ') == pkg.Source.Manager.DisplayName
                    || n.PackageName.TrimEnd('.', ' ') == pkg.Source.Name);

        var results = FilteredPackages.ApplyToList(
            _wrappedPackages.Where(w => matchFunc(w.Package) && sourceFilter(w.Package))
        ).ToList();

        FilteredPackages.Clear();
        foreach (var w in results) FilteredPackages.Add(w);

        UpdateSubtitle();
        PackageCountUpdated?.Invoke();

        if (FilteredPackages.Count == 0)
        {
            BackgroundText = string.IsNullOrWhiteSpace(query) ? NoPackagesText : NoMatchesText;
            BackgroundTextVisible = !MegaQueryBoxEnabled || !string.IsNullOrWhiteSpace(query);
        }
        else
        {
            BackgroundTextVisible = false;
        }
    }

    // ─── Package loading ──────────────────────────────────────────────────────
    public async Task LoadPackages(ReloadReason reason = ReloadReason.External)
    {
        if (!Loader.IsLoading && (!Loader.IsLoaded
            || reason is ReloadReason.External or ReloadReason.Manual or ReloadReason.Automated))
        {
            Loader.ClearPackages(emitFinishSignal: false);
            await Loader.ReloadPackages();
        }
    }

    // ─── Sorting ──────────────────────────────────────────────────────────────
    public string SortFieldName => CoreTools.Translate(SortFieldIndex switch
    {
        1 => "Id",
        2 => "Version",
        3 => "New version",
        4 => "Source",
        _ => "Name",
    });

    partial void OnSortFieldIndexChanged(int value)
    {
        FilteredPackages.SortBy(value switch
        {
            1 => ObservablePackageCollection.Sorter.Id,
            2 => ObservablePackageCollection.Sorter.Version,
            3 => ObservablePackageCollection.Sorter.NewVersion,
            4 => ObservablePackageCollection.Sorter.Source,
            _ => ObservablePackageCollection.Sorter.Name,
        });
        OnPropertyChanged(nameof(SortFieldName));
        FilterPackages();
    }

    partial void OnSortAscendingChanged(bool value)
    {
        FilteredPackages.SetSortDirection(value);
        FilterPackages();
    }

    // ─── Selection ────────────────────────────────────────────────────────────
    partial void OnInstantSearchChanged(bool value)
        => Settings.SetDictionaryItem(Settings.K.DisableInstantSearch, PageName, !value);

    partial void OnUpperLowerCaseChanged(bool value) => FilterPackages();
    partial void OnIgnoreSpecialCharsChanged(bool value) => FilterPackages();
    partial void OnSearchModeChanged(SearchMode value)
    {
        OnPropertyChanged(nameof(SearchMode_Both));
        OnPropertyChanged(nameof(SearchMode_Name));
        OnPropertyChanged(nameof(SearchMode_Id));
        OnPropertyChanged(nameof(SearchMode_Exact));
        OnPropertyChanged(nameof(SearchMode_Similar));
        FilterPackages();
    }

    // One bool property per mode — used for two-way RadioButton bindings.
    // Mutual exclusion is enforced by the ViewModel: setting any one to true
    // changes SearchMode, which notifies all five properties.
    public bool SearchMode_Both { get => SearchMode == SearchMode.Both; set { if (value) SearchMode = SearchMode.Both; } }
    public bool SearchMode_Name { get => SearchMode == SearchMode.Name; set { if (value) SearchMode = SearchMode.Name; } }
    public bool SearchMode_Id { get => SearchMode == SearchMode.Id; set { if (value) SearchMode = SearchMode.Id; } }
    public bool SearchMode_Exact { get => SearchMode == SearchMode.Exact; set { if (value) SearchMode = SearchMode.Exact; } }
    public bool SearchMode_Similar { get => SearchMode == SearchMode.Similar; set { if (value) SearchMode = SearchMode.Similar; } }

    partial void OnAllPackagesCheckedChanged(bool? value)
    {
        if (value == true) FilteredPackages.SelectAll();
        else if (value == false) FilteredPackages.ClearSelection();
    }

    // ─── Sources ──────────────────────────────────────────────────────────────
    public void AddPackageToSourcesList(IPackage package)
    {
        IManagerSource source = package.Source;
        if (!UsedManagers.Contains(source.Manager))
        {
            UsedManagers.Add(source.Manager);
            var node = new SourceTreeNode
            {
                PackageName = source.Manager.DisplayName,
                PackageID = package.Id,
                Version = package.VersionString,
                Source = package.Source.Name
            };

            var existing = GetAllSourceNodes();
            if (existing.Count == 0 || existing.Count(n => n.IsSelected) >= existing.Count / 2)
                node.IsSelected = true;

            AddRootSourceNode(node);
            RootNodeForManager.TryAdd(source.Manager, node);
            UsedSourcesForManager.TryAdd(source.Manager, []);
            SourcesPlaceholderVisible = false;
            SourcesTreeVisible = true;
        }

        if ((!UsedSourcesForManager.ContainsKey(source.Manager)
             || !UsedSourcesForManager[source.Manager].Contains(source))
            && source.Manager.Capabilities.SupportsCustomSources)
        {
            UsedSourcesForManager[source.Manager].Add(source);
            var item = new SourceTreeNode
            {
                PackageName = source.Manager.DisplayName,
                PackageID = package.Id,
                Version = package.VersionString,
                Source = package.Source.Name
            };
            NodesForSources.TryAdd(source, item);

            if (source.IsVirtualManager)
            {
                _localPackagesNode.Children.Add(item);
                if (!GetAllSourceNodes().Contains(_localPackagesNode))
                {
                    AddRootSourceNode(_localPackagesNode);
                    _localPackagesNode.IsSelected = true;
                }
            }
            else
            {
                RootNodeForManager[source.Manager].Children.Add(item);
            }
        }
    }

    public void ClearSourcesList()
    {
        foreach (var node in SourceNodes)
            node.PropertyChanged -= OnRootSourceNodePropertyChanged;
        UsedManagers.Clear();
        SourceNodes.Clear();
        UsedSourcesForManager.Clear();
        RootNodeForManager.Clear();
        NodesForSources.Clear();
        _localPackagesNode.Children.Clear();
    }

    private void AddRootSourceNode(SourceTreeNode node)
    {
        node.PropertyChanged += OnRootSourceNodePropertyChanged;
        SourceNodes.Add(node);
    }

    private void OnRootSourceNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SourceTreeNode.IsSelected))
            FilterPackages();
    }
    private List<SourceTreeNode> GetAllSourceNodes() => SourceNodes.ToList();
    private List<SourceTreeNode> GetSelectedSourceNodes() => SourceNodes.Where(n => n.IsSelected).ToList();

    public void SetSourceNodeSelected(SourceTreeNode node, bool selected) => node.IsSelected = selected;
    public void ClearSourceSelection() { foreach (var n in SourceNodes) n.IsSelected = false; }
    public void SelectAllSources() { foreach (var n in SourceNodes) n.IsSelected = true; }

    // ─── Header texts ─────────────────────────────────────────────────────────
    public void UpdateHeaderTexts()
    {
        bool isList = ViewMode == PackageViewMode.List;
        NameHeaderText = isList ? CoreTools.Translate("Package Name") : "";
        IdHeaderText = isList ? CoreTools.Translate("Package ID") : "";
        VersionHeaderText = isList ? CoreTools.Translate("Version") : "";
        NewVersionHeaderText = isList ? CoreTools.Translate("New version") : "";
        SourceHeaderText = isList ? CoreTools.Translate("Source") : "";
    }

    public bool IsListViewMode => ViewMode == PackageViewMode.List;
    public bool IsGridViewMode => ViewMode == PackageViewMode.Grid;
    public bool IsIconsViewMode => ViewMode == PackageViewMode.Icons;

    // Shim for SelectedIndex="{Binding ViewModeIndex}" in AXAML (ListBox requires int)
    public int ViewModeIndex
    {
        get => (int)ViewMode;
        set => ViewMode = (PackageViewMode)value;
    }

    partial void OnViewModeChanged(PackageViewMode value)
    {
        UpdateHeaderTexts();
        Settings.SetDictionaryItem(Settings.K.PackageListViewMode, PageName, (int)value);
        OnPropertyChanged(nameof(IsListViewMode));
        OnPropertyChanged(nameof(IsGridViewMode));
        OnPropertyChanged(nameof(IsIconsViewMode));
        OnPropertyChanged(nameof(ViewModeIndex));
    }

    // ─── Package count (called by PackageWrapper.IsChecked setter) ────────────
    public void UpdatePackageCount()
    {
        UpdateSubtitle();
        PackageCountUpdated?.Invoke();
    }

    // ─── Subtitle ─────────────────────────────────────────────────────────────
    public void UpdateSubtitle()
    {
        if (Loader.IsLoading)
        {
            Subtitle = _stillLoadingSubtitle;
            return;
        }

        if (Loader.Any())
        {
            int selected = FilteredPackages.GetCheckedPackages().Count;
            string r = CoreTools.Translate(
                "{0} packages were found, {1} of which match the specified filters.",
                FilteredPackages.Count,
                _wrappedPackages.Count
            ) + " (" + CoreTools.Translate("{0} selected", selected) + ")";

            if (_showLastCheckedTime)
                r += " " + CoreTools.Translate("(Last checked: {0})", _lastLoadTime.ToString(CultureInfo.CurrentCulture));

            Subtitle = r;
        }
        else
        {
            Subtitle = _noPackagesSubtitleBase + (_showLastCheckedTime
                ? " " + CoreTools.Translate("(Last checked: {0})", _lastLoadTime.ToString(CultureInfo.CurrentCulture))
                : "");
        }
    }

    // ─── Commands ─────────────────────────────────────────────────────────────
    [RelayCommand] private async Task Reload() => await LoadPackages(ReloadReason.Manual);
    [RelayCommand] private void SelectAllSources_Cmd() { SelectAllSources(); FilterPackages(); }
    [RelayCommand] private void ClearSourceSelection_Cmd() { ClearSourceSelection(); FilterPackages(); }
    [RelayCommand] private void RequestHelp() => HelpRequested?.Invoke();
    [RelayCommand] private void RequestManageIgnored() => ManageIgnoredRequested?.Invoke();

    [RelayCommand]
    public void RequestShare(IPackage? package)
    {
        if (package is null || package.Source.IsVirtualManager)
        {
            SharePackageRequested?.Invoke(null, null);
            return;
        }
        var url = "https://marticliment.com/unigetui/share?"
            + "name=" + System.Web.HttpUtility.UrlEncode(package.Name)
            + "&id=" + System.Web.HttpUtility.UrlEncode(package.Id)
            + "&sourceName=" + System.Web.HttpUtility.UrlEncode(package.Source.Name)
            + "&managerName=" + System.Web.HttpUtility.UrlEncode(package.Manager.Name);
        SharePackageRequested?.Invoke(package.Name, url);
    }

    // ─── Sort commands ────────────────────────────────────────────────────────
    [RelayCommand] private void SortByName() => SortFieldIndex = 0;
    [RelayCommand] private void SortById() => SortFieldIndex = 1;
    [RelayCommand] private void SortByVersion() => SortFieldIndex = 2;
    [RelayCommand] private void SortByNewVersion() => SortFieldIndex = 3;
    [RelayCommand] private void SortBySource() => SortFieldIndex = 4;
    [RelayCommand] private void SetSortAscending() => SortAscending = true;
    [RelayCommand] private void SetSortDescending() => SortAscending = false;

    [RelayCommand]
    private void SubmitMegaQuery(string query)
    {
        MegaQueryVisible = false;
        _searchQuery = query?.Trim() ?? "";
        FilterPackages(fromQuery: true);
    }

    // ─── Keyboard / search-box actions (called by the View's interface impls) ──
    public void TriggerReload()
    {
        if (!DisableReload)
            _ = LoadPackages(ReloadReason.Manual);
    }

    public void ToggleSelectAll()
    {
        if (AllPackagesChecked != true)
        {
            AllPackagesChecked = true;
            FilteredPackages.SelectAll();
        }
        else
        {
            AllPackagesChecked = false;
            FilteredPackages.ClearSelection();
        }
    }

    public void HandleSearchSubmitted()
    {
        if (MegaQueryBoxEnabled) SubmitSearch();
        else FilterPackages(fromQuery: true);
    }

    // ─── Operation launchers ─────────────────────────────────────────────────
    public static async Task LaunchInstall(
        IEnumerable<IPackage> packages,
        bool? elevated = null,
        bool? interactive = null,
        bool? no_integrity = null)
    {
        foreach (var pkg in packages)
        {
            var opts = await InstallOptionsFactory.LoadApplicableAsync(
                pkg, elevated: elevated, interactive: interactive, no_integrity: no_integrity);
            var op = new InstallPackageOperation(pkg, opts);
            op.OperationSucceeded += (_, _) => TelemetryHandler.InstallPackage(pkg, TEL_OP_RESULT.SUCCESS, TEL_InstallReferral.DIRECT_SEARCH);
            op.OperationFailed += (_, _) => TelemetryHandler.InstallPackage(pkg, TEL_OP_RESULT.FAILED, TEL_InstallReferral.DIRECT_SEARCH);
            AvaloniaOperationRegistry.Add(op);
            _ = op.MainThread();
        }
    }

    // ─── Focus (triggers view to focus the list) ──────────────────────────────
    public void RequestFocusList() => FocusListRequested?.Invoke();

    // ─── FilterHelpers (inner static class) ──────────────────────────────────
    internal static class FilterHelpers
    {
        public static string NormalizeCase(string input) => input.ToLower();

        public static string NormalizeSpecialCharacters(string input)
        {
            input = input.Replace("-", "").Replace("_", "").Replace(" ", "")
                         .Replace("@", "").Replace("\t", "").Replace(".", "")
                         .Replace(",", "").Replace(":", "");
            foreach (var (replacement, chars) in new (char, string)[]
            {
                ('a',"àáäâ"),('e',"èéëê"),('i',"ìíïî"),('o',"òóöô"),
                ('u',"ùúüû"),('y',"ýÿ"),('c',"ç"),('ñ',"n"),
            })
                foreach (char c in chars) input = input.Replace(c, replacement);
            return input;
        }

        public static bool NameContains(IPackage pkg, string q, List<Func<string, string>> f)
        { var n = pkg.Name; foreach (var x in f) n = x(n); return n.Contains(q); }

        public static bool IdContains(IPackage pkg, string q, List<Func<string, string>> f)
        { var id = pkg.Id; foreach (var x in f) id = x(id); return id.Contains(q); }

        public static bool NameOrIdContains(IPackage pkg, string q, List<Func<string, string>> f)
            => NameContains(pkg, q, f) || IdContains(pkg, q, f);

        public static bool NameOrIdExactMatch(IPackage pkg, string q, List<Func<string, string>> f)
        {
            var id = pkg.Id; foreach (var x in f) id = x(id); if (q == id) return true;
            var n = pkg.Name; foreach (var x in f) n = x(n); return q == n;
        }
    }
}
