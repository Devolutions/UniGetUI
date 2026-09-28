using System.ComponentModel;
using System.Globalization;
using System.Linq.Expressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Theme;
using UniGetUI.Tui.Views.Controls;
using UniGetUI.Tui.Views.Dialogs;

namespace UniGetUI.Tui.Views.Pages;

internal enum PackagePageKind
{
    Discover,
    Updates,
    Installed,
    Bundles,
}

internal enum PackageSearchMode
{
    Both,
    Name,
    Id,
    Exact,
    Similar,
}

/// <summary>One row of a package list. Mirrors the package's checked state and tag live.</summary>
internal sealed class PackageRow : INotifyPropertyChanged
{
    public PackageRow(IPackage package)
    {
        Package = package;
        Package.PropertyChanged += OnPackageChanged;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IPackage Package { get; }
    public string Check => Package.IsChecked ? "[x]" : "[ ]";

    public string State => Package.Tag switch
    {
        PackageTag.AlreadyInstalled => "inst",
        PackageTag.IsUpgradable => "upd",
        PackageTag.Pinned => "pin",
        PackageTag.OnQueue => "queue",
        PackageTag.BeingProcessed => "busy",
        PackageTag.Failed => "fail",
        PackageTag.Unavailable => "n/a",
        _ => "",
    };

    public string Name => Clean(Package.Name);
    public string Id => Clean(Package.Id);
    public string Version => Clean(Package.VersionString);
    public string NewVersion => Clean(Package.NewVersionString);
    public string Source => Clean(SafeSource(Package));

    /// <summary>Installer host (e.g. github.com), filled in once the package details are loaded.</summary>
    public string InstallerHost { get; private set; } = "…";

    /// <summary>Installer download size, filled in once the package details are loaded.</summary>
    public string DownloadSize { get; private set; } = "…";

    private static readonly SemaphoreSlim DetailsGate = new(4, 4);
    private bool _detailsRequested;

    /// <summary>Loads the details behind the optional host/size columns (desktop column preload).</summary>
    public async Task LoadColumnDetailsAsync()
    {
        if (_detailsRequested) return;
        _detailsRequested = true;
        await DetailsGate.WaitAsync();
        try
        {
            await Package.Details.Load();
            string host = InstallerHostDisplay.FromUrls([Package.Details.InstallerUrl?.ToString()]);
            string size = Package.Details.InstallerSize > 0 ? CoreTools.FormatAsSize(Package.Details.InstallerSize) : "\u2014";
            Dispatcher.UIThread.Post(() =>
            {
                InstallerHost = host.Length > 0 ? host : "\u2014";
                DownloadSize = size;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(InstallerHost)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DownloadSize)));
            });
        }
        catch (Exception ex)
        {
            Logger.Warn(ex);
        }
        finally
        {
            DetailsGate.Release();
        }
    }

    public void Detach() => Package.PropertyChanged -= OnPackageChanged;

    private void OnPackageChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IPackage.IsChecked))
            Dispatcher.UIThread.Post(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Check))));
        else if (e.PropertyName is nameof(IPackage.Tag))
            Dispatcher.UIThread.Post(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State))));
    }

    internal static string SafeSource(IPackage p)
    {
        try
        {
            return p.Source.AsString_DisplayName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string Clean(string? value) => (value ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();

    public override string ToString() => Name;
}

/// <summary>
/// The Discover / Updates / Installed / Bundles pages: a live, multi-select package list backed by
/// one of the engine's loaders, with the desktop app's search modes, source filters, sorting,
/// selection model and per-page actions (main action, variants, options, details, ignore, export…).
/// </summary>
internal sealed class PackageListPage : UserControl, ITuiPage
{
    private readonly PackagePageKind _kind;
    private readonly string _pageName;
    private readonly TextBox _search;
    private readonly TextBlock _searchPlaceholder;
    private readonly TextBlock _title;
    private readonly TextBlock _titleNote;
    private readonly TextBlock _status;
    private readonly TuiPanel _header;
    private readonly TuiChip _modeChip;
    private readonly TuiChip _sourcesChip;
    private readonly TuiChip _sortChip;
    private readonly StackPanel _chips;
    private readonly DataGrid _grid;
    private readonly Grid _bottom;
    private readonly TuiPanel _detailsBox;
    private readonly TextBlock _detailsName;
    private readonly TextBlock _detailsPosition;
    private readonly Grid _detailsFields;
    private readonly TextBlock _detailsEmpty;
    private readonly TextBlock _idValue = Value();
    private readonly TextBlock _versionValue = Value();
    private readonly TextBlock _versionArrow = new() { Text = "  \u2192  ", Foreground = TuiPalette.TextMuted };
    private readonly TextBlock _newVersionValue = new() { Foreground = TuiPalette.NewVersion, FontWeight = FontWeight.Bold };
    private readonly TextBlock _sourceValue = Value();
    private readonly TextBlock _managerValue = Value();
    private readonly TuiPanel _actionsBox;
    private readonly Grid _actions = new();
    private readonly TextBlock _summaryLine;
    private int _actionColumns = -1;
    private bool _compact;
    private readonly DispatcherTimer _filterDebounce;
    private readonly HashSet<string> _hiddenSources = new(StringComparer.OrdinalIgnoreCase);

    private List<PackageRow> _rows = [];
    private AbstractPackageLoader? _subscribed;
    private bool _dirty;
    private DateTime _lastLoadTime = DateTime.Now;
    private string _submittedQuery = string.Empty;

    public PackageListPage(PackagePageKind kind)
    {
        _kind = kind;
        _pageName = kind switch
        {
            PackagePageKind.Discover => "SoftwarePages.DiscoverSoftwarePage",
            PackagePageKind.Updates => "SoftwarePages.SoftwareUpdatesPage",
            PackagePageKind.Installed => "SoftwarePages.InstalledPackagesPage",
            _ => "SoftwarePages.PackageBundlesPage",
        };

        SearchMode = kind == PackagePageKind.Discover ? PackageSearchMode.Similar : PackageSearchMode.Both;
        InstantSearch = !Settings.GetDictionaryItem<string, bool>(Settings.K.DisableInstantSearch, _pageName);
        SortField = Math.Clamp(Settings.GetDictionaryItem<string, int>(Settings.K.PackageListSortFieldIndex, _pageName), 0, 4);
        if (SortField == 3 && kind != PackagePageKind.Updates) SortField = 0;
        SortAscending = !Settings.GetDictionaryItem<string, bool>(Settings.K.PackageListSortDescending, _pageName);

        _title = TuiPanel.TitleText("");
        _titleNote = new TextBlock { Foreground = TuiPalette.Warning };
        _search = new TextBox();
        // Consolonia's TextBox draws its own placeholder in the chooser colour, which is nearly invisible on
        // the edit surface in most themes, so the page draws the placeholder over the empty box itself.
        _searchPlaceholder = new TextBlock
        {
            Text = kind == PackagePageKind.Discover
                ? CoreTools.Translate("Search for packages") + "  (Enter to search)"
                : CoreTools.Translate("Filter the list"),
            Foreground = TuiPalette.Watermark,
            IsHitTestVisible = false,
        };
        _search.AddHandler(TextInputEvent, (_, e) => TuiInputGuard.HandleTextInput(_search, e, "package search"),
            RoutingStrategies.Tunnel);
        _search.KeyDown += OnSearchKeyDown;
        _search.TextChanged += (_, _) =>
        {
            _searchPlaceholder.IsVisible = string.IsNullOrEmpty(_search.Text);
            if (_kind == PackagePageKind.Discover || !InstantSearch) return;
            _filterDebounce!.Stop();
            _filterDebounce.Start();
        };

        _filterDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _filterDebounce.Tick += (_, _) =>
        {
            _filterDebounce.Stop();
            ScheduleRefresh();
        };

        _status = new TextBlock { Foreground = TuiPalette.TextMuted, TextTrimming = TextTrimming.CharacterEllipsis };
        _modeChip = new TuiChip(CoreTools.Translate("Mode"));
        _sourcesChip = new TuiChip(CoreTools.Translate("Sources"));
        _sortChip = new TuiChip(CoreTools.Translate("Sort"));
        _chips = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        _chips.Children.Add(_modeChip);
        _chips.Children.Add(_sourcesChip);
        _chips.Children.Add(_sortChip);
        _modeChip.Activated += () => _ = ShowSearchOptionsAsync();
        _sourcesChip.Activated += () => _ = ShowSourceFilterAsync();
        _sortChip.Activated += () => _ = ShowSortMenuAsync();
        _header = new TuiPanel(BuildToolbar()) { Summary = _status };
        _header.SetTitle(_title, _titleNote);
        _grid = BuildGrid();
        _grid.SelectionChanged += (_, _) => UpdateDetails();
        _grid.AddHandler(KeyDownEvent, OnGridKeyDown, RoutingStrategies.Tunnel);
        _grid.AddHandler(TextInputEvent, OnGridTextInput, RoutingStrategies.Tunnel);
        _grid.DoubleTapped += (_, _) => _ = ShowDetailsAsync();
        // F3 (search mode) works from the list and the filter box alike.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Handled || e.Key != Key.F3 || e.KeyModifiers != KeyModifiers.None) return;
            _ = ShowSearchOptionsAsync();
            e.Handled = true;
        }, RoutingStrategies.Bubble);
        _detailsName = new TextBlock { Foreground = TuiPalette.Text, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
        _detailsPosition = new TextBlock { Foreground = TuiPalette.TextMuted };
        _detailsEmpty = new TextBlock { Text = CoreTools.Translate("Select a package to see its details."), Foreground = TuiPalette.TextMuted };
        _detailsFields = BuildDetailsFields();
        var detailsBody = new Panel { Height = 3 };
        detailsBody.Children.Add(_detailsFields);
        detailsBody.Children.Add(_detailsEmpty);
        _detailsBox = new TuiPanel(detailsBody) { Summary = _detailsPosition };
        _detailsBox.SetTitle(_detailsName);
        _actionsBox = new TuiPanel(CoreTools.Translate("Actions"), _actions) { Margin = new Thickness(1, 0, 0, 0) };
        _summaryLine = new TextBlock { Foreground = TuiPalette.TextMuted, TextTrimming = TextTrimming.CharacterEllipsis, IsVisible = false };
        _bottom = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Grid.SetColumn(_actionsBox, 1);
        _bottom.Children.Add(_detailsBox);
        _bottom.Children.Add(_actionsBox);

        Content = BuildLayout();
        SizeChanged += (_, _) => UpdateBottomLayout();
        UpdateTitle();
        UpdateDetails();
    }

    public PackagePageKind Kind => _kind;
    public PackageSearchMode SearchMode { get; private set; }
    public bool CaseSensitive { get; private set; }
    public bool IgnoreSpecialCharacters { get; private set; }
    public bool InstantSearch { get; private set; }
    public int SortField { get; private set; }
    public bool SortAscending { get; private set; }

    private AbstractPackageLoader Loader => _kind switch
    {
        PackagePageKind.Discover => DiscoverablePackagesLoader.Instance,
        PackagePageKind.Updates => UpgradablePackagesLoader.Instance,
        PackagePageKind.Installed => InstalledPackagesLoader.Instance,
        _ => PackageBundlesLoader.Instance,
    };

    private OperationType Role => _kind switch
    {
        PackagePageKind.Updates => OperationType.Update,
        PackagePageKind.Installed => OperationType.Uninstall,
        PackagePageKind.Bundles => OperationType.None,
        _ => OperationType.Install,
    };

    public IReadOnlyList<PackageRow> VisibleRows => _rows;

    public IPackage? FocusedPackage => (_grid.SelectedItem as PackageRow)?.Package;

    // ─── Layout ────────────────────────────────────────────────────────────

    private Control BuildLayout()
    {
        var root = new DockPanel { LastChildFill = true, Margin = new Thickness(1, 0, 1, 0) };
        // A blank row between the page tabs and the header panel.
        _header.Margin = new Thickness(0, 1, 0, 0);
        DockPanel.SetDock(_header, Dock.Top);
        // Bottom: the focused package (left) and the page's keys (right), or one summary line when short.
        var bottom = new StackPanel();
        bottom.Children.Add(_bottom);
        bottom.Children.Add(_summaryLine);
        DockPanel.SetDock(bottom, Dock.Bottom);
        root.Children.Add(_header);
        root.Children.Add(bottom);
        root.Children.Add(_grid);
        return root;
    }

    /// <summary>The header panel's toolbar: the bracketed filter box, then the mode / sources / sort chips.</summary>
    private Control BuildToolbar()
    {
        var label = new TextBlock
        {
            Text = (_kind == PackagePageKind.Discover ? CoreTools.Translate("Search") : CoreTools.Translate("Filter")) + " ",
            Foreground = TuiPalette.Brand,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var open = new TextBlock { Text = "[", Foreground = TuiPalette.TextDim };
        var close = new TextBlock { Text = "]", Foreground = TuiPalette.TextDim };
        var field = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(open, Dock.Left);
        DockPanel.SetDock(close, Dock.Right);
        field.Children.Add(open);
        field.Children.Add(close);
        var box = new Panel();
        box.Children.Add(_search);
        box.Children.Add(_searchPlaceholder);
        field.Children.Add(box);

        // Label | filter box (at most 66 columns) | free space | chips.
        _chips.Margin = new Thickness(3, 0, 0, 0);
        var toolbar = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,100*,*,Auto") };
        toolbar.ColumnDefinitions[1].MaxWidth = 66;
        Grid.SetColumn(field, 1);
        Grid.SetColumn(_chips, 3);
        toolbar.Children.Add(label);
        toolbar.Children.Add(field);
        toolbar.Children.Add(_chips);
        // Narrow terminals keep the chips but drop their labels, so the filter box stays usable.
        toolbar.SizeChanged += (_, e) => FitChips(e.NewSize.Width);
        return toolbar;
    }

    private void FitChips(double width)
    {
        bool labels = width - TuiChip.WidthOf(_chips, withLabels: true) >= 40;
        foreach (TuiChip chip in _chips.Children.OfType<TuiChip>()) chip.ShowLabel = labels;
    }

    private static TextBlock Value() => new() { Foreground = TuiPalette.Text, TextTrimming = TextTrimming.CharacterEllipsis };

    private static TextBlock FieldLabel(string text)
        => new() { Text = CoreTools.Translate(text).ToUpper(CultureInfo.CurrentCulture), Foreground = TuiPalette.TextMuted, Margin = new Thickness(0, 0, 2, 0) };

    /// <summary>The details box body: ID, VERSION (old → new), then SOURCE and MANAGER on one row.</summary>
    private Grid BuildDetailsFields()
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
        };

        void Add(Control control, int row, int column)
        {
            Grid.SetRow(control, row);
            Grid.SetColumn(control, column);
            grid.Children.Add(control);
        }

        var version = new StackPanel { Orientation = Orientation.Horizontal };
        version.Children.Add(_versionValue);
        version.Children.Add(_versionArrow);
        version.Children.Add(_newVersionValue);
        var origin = new StackPanel { Orientation = Orientation.Horizontal };
        origin.Children.Add(_sourceValue);
        var managerLabel = FieldLabel("Manager");
        managerLabel.Margin = new Thickness(4, 0, 2, 0);
        origin.Children.Add(managerLabel);
        origin.Children.Add(_managerValue);

        Add(FieldLabel("Id"), 0, 0);
        Add(_idValue, 0, 1);
        Add(FieldLabel("Version"), 1, 0);
        Add(version, 1, 1);
        Add(FieldLabel("Source"), 2, 0);
        Add(origin, 2, 1);
        return grid;
    }

    /// <summary>The page's keys for the actions panel, most useful first (filled column by column).</summary>
    private IReadOnlyList<TuiKeyHint> ActionKeys()
    {
        static TuiKeyHint K(string key, string label) => new(key, CoreTools.Translate(label));
        return _kind switch
        {
            PackagePageKind.Discover =>
            [
                K("i", "Install"), K("Space", "Select"), K("Enter", "Details"),
                K("o", "Options"), K("m", "All actions"), K("b", "Add to bundle"),
                K("f", "Sources"), K("s", "Sort"), K("F3", "Mode"),
                K("/", "Search"), K("Ctrl+A", "Select all"), K("Ctrl+C", "Copy"),
            ],
            PackagePageKind.Updates =>
            [
                K("u", "Update"), K("Space", "Select"), K("Enter", "Details"),
                K("o", "Options"), K("m", "All actions"), K("g", "Ignore"),
                K("f", "Sources"), K("s", "Sort"), K("F3", "Mode"),
                K("/", "Filter"), K("x", "Uninstall"), K("b", "Add to bundle"),
            ],
            PackagePageKind.Installed =>
            [
                K("x", "Uninstall"), K("Space", "Select"), K("Enter", "Details"),
                K("u", "Update"), K("o", "Options"), K("m", "All actions"),
                K("g", "Ignore"), K("b", "Add to bundle"), K("f", "Sources"),
                K("s", "Sort"), K("F3", "Mode"), K("/", "Filter"),
            ],
            _ =>
            [
                K("i", "Install"), K("Space", "Select"), K("Enter", "Details"),
                K("o", "Options"), K("m", "All actions"), K("Del", "Remove"),
                K("n", "New bundle"), K("Ctrl+O", "Open"), K("Ctrl+S", "Save"),
                K("f", "Sources"), K("s", "Sort"), K("F3", "Mode"),
            ],
        };
    }

    private const int ActionRows = 3;
    private const int ActionColumnGap = 3;
    private const int DetailsMinWidth = 54;

    private static int ActionColumnWidth(IReadOnlyList<TuiKeyHint> column, out int keyWidth)
    {
        keyWidth = column.Max(h => h.Key.Length);
        return keyWidth + 1 + column.Max(h => h.Label.Length);
    }

    private IEnumerable<TuiKeyHint[]> ActionColumns(int columns) => ActionKeys().Chunk(ActionRows).Take(columns);

    /// <summary>Width of the actions panel with this many columns, frame, padding and margin included.</summary>
    private int ActionsWidth(int columns)
        => ActionColumns(columns).Select((c, i) => ActionColumnWidth(c, out _) + (i > 0 ? ActionColumnGap : 0)).Sum() + 4 + 1;

    /// <summary>
    /// Picks the bottom layout for the page size: the details box with a 4, 3 or 2 column actions panel, the details
    /// box alone when narrow (the page keys then go to the function-key bar), one summary line when short.
    /// </summary>
    private void UpdateBottomLayout()
    {
        double width = Bounds.Width - 2;
        bool compact = Bounds.Height is > 0 and < 22;
        int columns = 0;
        if (!compact)
            for (int c = 4; c >= 2; c--)
                if (width - ActionsWidth(c) >= DetailsMinWidth)
                {
                    columns = c;
                    break;
                }

        if (compact == _compact && columns == _actionColumns) return;
        bool hintsChanged = (columns == 0) != (_actionColumns <= 0);
        _compact = compact;
        _actionColumns = columns;
        _bottom.IsVisible = !compact;
        _summaryLine.IsVisible = compact;
        _actionsBox.IsVisible = columns > 0;
        BuildActionsPanel(columns);
        UpdateDetails();
        if (hintsChanged) TuiShell.InvalidateChrome();
    }

    private void BuildActionsPanel(int columns)
    {
        _actions.Children.Clear();
        _actions.ColumnDefinitions.Clear();
        _actions.RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", ActionRows)));
        int c = 0;
        foreach (TuiKeyHint[] column in ActionColumns(columns))
        {
            _actions.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
            ActionColumnWidth(column, out int keyWidth);
            for (int r = 0; r < column.Length; r++)
            {
                var cell = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(c > 0 ? ActionColumnGap : 0, 0, 0, 0) };
                cell.Children.Add(new TextBlock
                {
                    Text = column[r].Key.PadRight(keyWidth) + " ",
                    Foreground = TuiPalette.Brand,
                    FontWeight = FontWeight.Bold,
                });
                cell.Children.Add(new TextBlock { Text = column[r].Label, Foreground = TuiPalette.TextMuted });
                Grid.SetColumn(cell, c);
                Grid.SetRow(cell, r);
                _actions.Children.Add(cell);
            }

            c++;
        }
    }

    private DataGrid BuildGrid()
    {
        var grid = new DataGrid
        {
            MinColumnWidth = 3,
            AutoGenerateColumns = false,
            Background = Brushes.Transparent,
            BorderBrush = TuiPalette.Frame,
            CanUserSortColumns = true,
            CanUserReorderColumns = false,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Single,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        // Compiled (lambda) bindings, not name-based ones: they need no reflection, so they survive trimming and NativeAOT.
        void Column(string header, Expression<Func<PackageRow, string>> property, double width, int? sortField = null)
        {
            var column = new DataGridTextColumn
            {
                Header = header,
                Binding = CompiledBinding.Create(property),
                Width = width > 0 ? new DataGridLength(width) : new DataGridLength(1, DataGridLengthUnitType.Star),
                CanUserSort = sortField is not null,
                Tag = sortField,
            };
            grid.Columns.Add(column);
        }

        Column("Sel", r => r.Check, 4);
        Column(CoreTools.Translate("Package Name"), r => r.Name, 0, 0);
        Column(CoreTools.Translate("Package ID"), r => r.Id, 30, 1);
        Column(CoreTools.Translate("Version"), r => r.Version, 12, 2);
        if (_kind == PackagePageKind.Updates)
            Column(CoreTools.Translate("New version"), r => r.NewVersion, 12, 3);
        Column(CoreTools.Translate("Source"), r => r.Source, 18, 4);
        if (_kind != PackagePageKind.Installed && Settings.Get(Settings.K.ShowInstallerHostColumn))
            Column(CoreTools.Translate("Installer host"), r => r.InstallerHost, 18);
        if (_kind != PackagePageKind.Installed && Settings.Get(Settings.K.ShowDownloadSizeColumn))
            Column(CoreTools.Translate("Download size"), r => r.DownloadSize, 15);
        Column("", r => r.State, 6);

        // Column-header clicks drive the page's own (persisted) sort instead of the grid's.
        grid.Sorting += (_, e) =>
        {
            e.Handled = true;
            if (e.Column.Tag is int field) SetSort(field, field == SortField ? !SortAscending : true);
        };
        return grid;
    }

    // ─── ITuiPage ──────────────────────────────────────────────────────────

    /// <summary>Function-key bar hints, only while the actions panel is hidden (narrow or short terminals).</summary>
    public IReadOnlyList<TuiKeyHint> KeyHints => _actionColumns > 0 ? [] : _kind switch
    {
        PackagePageKind.Discover => TuiChrome.Hints(("Enter", "Details"), ("Space", "Select"), ("i", "Install"), ("o", "Options"), ("m", "Actions"), ("b", "Bundle"), ("f", "Sources"), ("s", "Sort"), ("/", "Search")),
        PackagePageKind.Updates => TuiChrome.Hints(("Enter", "Details"), ("Space", "Select"), ("u", "Update"), ("o", "Options"), ("m", "Actions"), ("g", "Ignore"), ("f", "Sources"), ("s", "Sort"), ("/", "Filter")),
        PackagePageKind.Installed => TuiChrome.Hints(("Enter", "Details"), ("Space", "Select"), ("x", "Uninstall"), ("u", "Update"), ("o", "Options"), ("m", "Actions"), ("g", "Ignore"), ("b", "Bundle"), ("/", "Filter")),
        _ => TuiChrome.Hints(("Enter", "Details"), ("Space", "Select"), ("i", "Install"), ("o", "Options"), ("m", "Actions"), ("Del", "Remove"), ("Ctrl+O", "Open"), ("Ctrl+S", "Save"), ("n", "New")),
    };

    public IReadOnlyList<TuiAction> Actions => BuildActions();

    public bool FocusPrimary() => _rows.Count > 0 && _kind != PackagePageKind.Discover ? FocusList() : FocusSearch();

    public bool FocusSearch()
    {
        bool ok = _search.Focus();
        _search.CaretIndex = _search.Text?.Length ?? 0;
        return ok;
    }

    public bool FocusList() => _rows.Count > 0 && TuiFocus.SeatDataGridFocus(_grid);

    public void Reload()
    {
        if (_kind == PackagePageKind.Discover)
        {
            if (_submittedQuery.Length > 0) _ = DiscoverablePackagesLoader.Instance.ReloadPackages(_submittedQuery);
            return;
        }

        if (_kind == PackagePageKind.Bundles) return;
        if (!Loader.IsLoading) _ = Loader.ReloadPackages();
    }

    public void ToggleSelectAll()
    {
        bool allChecked = _rows.Count > 0 && _rows.All(r => r.Package.IsChecked);
        foreach (var row in _rows) row.Package.IsChecked = !allChecked;
        UpdateStatus();
    }

    public void OnShown()
    {
        Subscribe();
        Refresh();
    }

    // ─── Selection helpers ────────────────────────────────────────────────

    /// <summary>Checked packages among the visible rows; the focused row when nothing is checked.</summary>
    public IReadOnlyList<IPackage> SelectedOrFocused()
    {
        var checkedPackages = _rows.Where(r => r.Package.IsChecked).Select(r => r.Package).ToList();
        if (checkedPackages.Count > 0) return checkedPackages;
        return FocusedPackage is { } focused ? [focused] : [];
    }

    public IReadOnlyList<IPackage> CheckedPackages() => _rows.Where(r => r.Package.IsChecked).Select(r => r.Package).ToList();

    public void SelectPackageById(string id)
    {
        var row = _rows.FirstOrDefault(r => r.Package.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (row is null) return;
        _grid.SelectedItem = row;
        _grid.ScrollIntoView(row, null);
    }

    // ─── Keys ──────────────────────────────────────────────────────────────

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Enter or Key.Return:
                if (_kind == PackagePageKind.Discover) SubmitSearch();
                else
                {
                    _filterDebounce.Stop();
                    Refresh();
                    FocusList();
                }

                e.Handled = true;
                break;
            case Key.Down:
                if (FocusList()) e.Handled = true;
                break;
        }
    }

    public void SubmitSearch()
    {
        string query = (_search.Text ?? string.Empty).Trim();
        _submittedQuery = query;
        if (query.Length == 0)
        {
            DiscoverablePackagesLoader.Instance.ClearPackages(emitFinishSignal: false);
            Refresh();
            return;
        }

        _status.Text = CoreTools.Translate("Searching for \"{0}\"…", query);
        DiscoverablePackagesLoader.Instance.ClearPackages(emitFinishSignal: false);
        _ = DiscoverablePackagesLoader.Instance.ReloadPackages(query);
    }

    private void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        KeyModifiers mods = e.KeyModifiers;
        switch (e.Key)
        {
            case Key.Space when mods == KeyModifiers.None:
                if (FocusedPackage is { } p)
                {
                    p.IsChecked = !p.IsChecked;
                    UpdateStatus();
                }

                e.Handled = true;
                break;
            case Key.Enter or Key.Return when mods == KeyModifiers.Control:
                _ = RunMainActionAsync(SelectedOrFocused());
                e.Handled = true;
                break;
            case Key.Enter or Key.Return when mods == KeyModifiers.Alt:
                _ = ShowOptionsAsync();
                e.Handled = true;
                break;
            case Key.Enter or Key.Return when mods == KeyModifiers.None:
                _ = ShowDetailsAsync();
                e.Handled = true;
                break;
            case Key.Delete when mods == KeyModifiers.None && _kind == PackagePageKind.Bundles:
                RemoveFromBundle(SelectedOrFocused());
                e.Handled = true;
                break;
            case Key.O when mods == KeyModifiers.Control && _kind == PackagePageKind.Bundles:
                _ = OpenBundleAsync();
                e.Handled = true;
                break;
            case Key.S when mods == KeyModifiers.Control && _kind == PackagePageKind.Bundles:
                _ = SaveBundleAsync();
                e.Handled = true;
                break;
            case Key.C when mods == KeyModifiers.Control:
                _ = CopySelectedAsync();
                e.Handled = true;
                break;
            case Key.F10 when mods == KeyModifiers.Shift:
            case Key.Apps:
                _ = ShowActionMenuAsync();
                e.Handled = true;
                break;
        }
    }

    private void OnGridTextInput(object? sender, TextInputEventArgs e)
    {
        string text = e.Text ?? string.Empty;
        if (text.Length != 1) return;
        e.Handled = true;
        if (text == " ") return;

        switch (text)
        {
            case "i" when _kind is PackagePageKind.Discover or PackagePageKind.Bundles:
                _ = RunMainActionAsync(SelectedOrFocused());
                return;
            case "u" when _kind == PackagePageKind.Updates:
                _ = RunMainActionAsync(SelectedOrFocused());
                return;
            case "u" when _kind == PackagePageKind.Installed:
                if (FocusedPackage is { } installed) _ = UpdateInstalledAsync(installed);
                return;
            case "x" when _kind is PackagePageKind.Installed or PackagePageKind.Updates:
                _ = UninstallAsync(SelectedOrFocused());
                return;
            case "o":
                _ = ShowOptionsAsync();
                return;
            case "m":
                _ = ShowActionMenuAsync();
                return;
            case "b" when _kind != PackagePageKind.Bundles:
                _ = AddToBundleAsync(SelectedOrFocused());
                return;
            case "g" when _kind is PackagePageKind.Updates or PackagePageKind.Installed:
                _ = IgnoreSelectedAsync();
                return;
            case "f":
                _ = ShowSourceFilterAsync();
                return;
            case "s":
                _ = ShowSortMenuAsync();
                return;
            case "r":
                Reload();
                return;
            case "n" when _kind == PackagePageKind.Bundles:
                _ = NewBundleAsync();
                return;
            case "/":
                FocusSearch();
                return;
        }

        // Any other printable character starts a search with it (desktop behaviour).
        if (!char.IsControl(text[0]))
        {
            _search.Text = (_search.Text ?? string.Empty) + text;
            FocusSearch();
        }
    }

    // ─── Loader subscription and refresh ──────────────────────────────────

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe();
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _filterDebounce.Stop();
    }

    private void Subscribe()
    {
        AbstractPackageLoader loader = Loader;
        if (ReferenceEquals(loader, _subscribed)) return;
        if (_subscribed is not null)
        {
            _subscribed.PackagesChanged -= OnPackagesChanged;
            _subscribed.StartedLoading -= OnLoaderEvent;
            _subscribed.FinishedLoading -= OnFinishedLoading;
        }

        loader.PackagesChanged += OnPackagesChanged;
        loader.StartedLoading += OnLoaderEvent;
        loader.FinishedLoading += OnFinishedLoading;
        _subscribed = loader;
        if (_kind == PackagePageKind.Bundles)
            TuiBundleService.UnsavedChangesStateChanged += () => Dispatcher.UIThread.Post(UpdateTitle);
    }

    private void OnPackagesChanged(object? sender, PackagesChangedEvent e) => ScheduleRefresh();

    private void OnLoaderEvent(object? sender, EventArgs e) => ScheduleRefresh();

    private void OnFinishedLoading(object? sender, EventArgs e)
    {
        _lastLoadTime = DateTime.Now;
        ScheduleRefresh();
    }

    private void ScheduleRefresh()
    {
        _dirty = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_dirty) return;
            _dirty = false;
            Refresh();
        }, DispatcherPriority.Background);
    }

    public void Refresh()
    {
        IReadOnlyList<IPackage> snapshot;
        try
        {
            snapshot = Loader.Packages;
        }
        catch (Exception ex)
        {
            Logger.Warn(ex);
            snapshot = [];
        }

        string query = (_search.Text ?? string.Empty).Trim();
        if (_kind == PackagePageKind.Discover && query != _submittedQuery) query = _submittedQuery;
        var filtered = snapshot.Where(p => MatchesQuery(p, query) && MatchesSources(p));
        List<IPackage> ordered = Sort(filtered).ToList();

        IPackage? selected = FocusedPackage;
        foreach (var old in _rows) old.Detach();
        _rows = ordered.Select(p => new PackageRow(p)).ToList();
        _grid.ItemsSource = _rows;
        if (_kind != PackagePageKind.Installed
            && (Settings.Get(Settings.K.ShowInstallerHostColumn) || Settings.Get(Settings.K.ShowDownloadSizeColumn)))
            foreach (var row in _rows.Take(200)) _ = row.LoadColumnDetailsAsync();

        if (selected is not null)
        {
            var match = _rows.FirstOrDefault(r => ReferenceEquals(r.Package, selected))
                        ?? _rows.FirstOrDefault(r => r.Package.IsEquivalentTo(selected));
            if (match is not null) _grid.SelectedItem = match;
        }

        UpdateTitle();
        UpdateStatus();
        UpdateDetails();
        TuiShell.InvalidateChrome();
    }

    private bool MatchesQuery(IPackage p, string query)
    {
        if (query.Length == 0 || SearchMode == PackageSearchMode.Similar) return true;
        string Normalize(string s)
        {
            if (!CaseSensitive) s = s.ToLowerInvariant();
            if (IgnoreSpecialCharacters) s = NormalizeSpecial(s);
            return s;
        }

        string q = Normalize(query);
        string name = Normalize(p.Name ?? string.Empty);
        string id = Normalize(p.Id ?? string.Empty);
        return SearchMode switch
        {
            PackageSearchMode.Name => name.Contains(q, StringComparison.Ordinal),
            PackageSearchMode.Id => id.Contains(q, StringComparison.Ordinal),
            PackageSearchMode.Exact => name == q || id == q,
            _ => name.Contains(q, StringComparison.Ordinal) || id.Contains(q, StringComparison.Ordinal),
        };
    }

    internal static string NormalizeSpecial(string input)
    {
        input = input.Replace("-", "").Replace("_", "").Replace(" ", "").Replace("@", "").Replace("\t", "")
            .Replace(".", "").Replace(",", "").Replace(":", "");
        foreach (var (replacement, chars) in new (char, string)[]
                 {
                     ('a', "àáäâ"), ('e', "èéëê"), ('i', "ìíïî"), ('o', "òóöô"), ('u', "ùúüû"), ('y', "ýÿ"), ('c', "ç"),
                 })
            foreach (char c in chars) input = input.Replace(c, replacement);
        return input;
    }

    private static string SourceKey(IPackage p)
        => p.Manager.Capabilities.SupportsCustomSources && !p.Source.IsVirtualManager
            ? $"{p.Manager.Name}|{p.Source.Name}"
            : p.Source.IsVirtualManager ? "|Local" : $"{p.Manager.Name}|";

    private bool MatchesSources(IPackage p) => _hiddenSources.Count == 0 || !_hiddenSources.Contains(SourceKey(p));

    private IEnumerable<IPackage> Sort(IEnumerable<IPackage> packages)
    {
        Func<IPackage, string> key = SortField switch
        {
            1 => p => p.Id ?? string.Empty,
            4 => PackageRow.SafeSource,
            _ => p => p.Name ?? string.Empty,
        };

        IOrderedEnumerable<IPackage> ordered = SortField switch
        {
            2 => SortAscending ? packages.OrderBy(p => p.NormalizedVersion) : packages.OrderByDescending(p => p.NormalizedVersion),
            3 => SortAscending ? packages.OrderBy(p => p.NormalizedNewVersion) : packages.OrderByDescending(p => p.NormalizedNewVersion),
            _ => SortAscending
                ? packages.OrderBy(key, StringComparer.OrdinalIgnoreCase)
                : packages.OrderByDescending(key, StringComparer.OrdinalIgnoreCase),
        };
        return ordered.ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase);
    }

    private void SetSort(int field, bool ascending)
    {
        SortField = field;
        SortAscending = ascending;
        Settings.SetDictionaryItem(Settings.K.PackageListSortFieldIndex, _pageName, field);
        Settings.SetDictionaryItem(Settings.K.PackageListSortDescending, _pageName, !ascending);
        Refresh();
    }

    private string SortFieldName(int field) => CoreTools.Translate(field switch
    {
        1 => "Id",
        2 => "Version",
        3 => "New version",
        4 => "Source",
        _ => "Name",
    });

    private void UpdateTitle()
    {
        _title.Text = _kind switch
        {
            PackagePageKind.Discover => CoreTools.Translate("Discover Packages"),
            PackagePageKind.Updates => CoreTools.Translate("Software Updates"),
            PackagePageKind.Installed => CoreTools.Translate("Installed Packages"),
            _ => CoreTools.Translate("Package Bundles"),
        };
        bool unsaved = _kind == PackagePageKind.Bundles && TuiBundleService.HasUnsavedChanges;
        _titleNote.Text = unsaved ? "  *" + CoreTools.Translate("unsaved changes") : string.Empty;
        _titleNote.IsVisible = unsaved;

        string mode = SearchMode switch
        {
            PackageSearchMode.Name => CoreTools.Translate("Package Name"),
            PackageSearchMode.Id => CoreTools.Translate("Package ID"),
            PackageSearchMode.Exact => CoreTools.Translate("Exact match"),
            PackageSearchMode.Similar => CoreTools.Translate("Show similar packages"),
            _ => CoreTools.Translate("Both"),
        };
        if (CaseSensitive) mode += ", " + CoreTools.Translate("case-sensitive");
        if (IgnoreSpecialCharacters) mode += ", " + CoreTools.Translate("ignoring special characters");
        _modeChip.Value = mode;
        _sourcesChip.Value = _hiddenSources.Count == 0
            ? CoreTools.Translate("All sources")
            : CoreTools.Translate("{0} hidden", _hiddenSources.Count);
        _sortChip.Value = $"{SortFieldName(SortField)} {(SortAscending ? "▲" : "▼")}";
        if (_chips.Parent is Control toolbar) FitChips(toolbar.Bounds.Width);
    }

    /// <summary>The summary in the header's top edge: counts, selection and when the list was last checked.</summary>
    private void UpdateStatus()
    {
        AbstractPackageLoader loader = Loader;
        int total = 0;
        try
        {
            total = loader.Packages.Count;
        }
        catch
        {
            // loader not ready yet
        }

        int selected = _rows.Count(r => r.Package.IsChecked);
        string last = _kind is PackagePageKind.Updates or PackagePageKind.Installed
            ? "  ·  " + CoreTools.Translate("Last checked: {0}", _lastLoadTime.ToString("t", CultureInfo.CurrentCulture))
            : string.Empty;

        if (loader.IsLoading)
        {
            _status.Text = CoreTools.Translate("Loading packages") + $"…  ({_rows.Count})";
        }
        else if (total == 0)
        {
            _status.Text = _kind switch
            {
                PackagePageKind.Discover => _submittedQuery.Length == 0
                    ? CoreTools.Translate("Search for packages to start") + "."
                    : CoreTools.Translate("No results were found matching the input criteria"),
                PackagePageKind.Updates => CoreTools.Translate("Hooray! No updates were found."),
                PackagePageKind.Installed => CoreTools.Translate("No packages were found"),
                _ => CoreTools.Translate("Add packages or open an existing package bundle"),
            } + last;
        }
        else
        {
            string count = _rows.Count == total
                ? CoreTools.Translate(_kind == PackagePageKind.Updates ? "{0} updates" : "{0} packages", total)
                : CoreTools.Translate("{0} of {1} shown", _rows.Count, total);
            _status.Text = count + "  ·  " + CoreTools.Translate("{0} selected", selected) + last;
        }
    }

    /// <summary>The details box: the focused package's name in the box edge and its fields, or one line when short.</summary>
    private void UpdateDetails()
    {
        IPackage? p = FocusedPackage;
        if (_compact)
        {
            _summaryLine.Text = p is null
                ? CoreTools.Translate("Select a package to see its details.")
                : $"{p.Name}  ·  {p.Id}  ·  {VersionText(p)}  ·  {PackageRow.SafeSource(p)}";
            return;
        }

        _detailsFields.IsVisible = p is not null;
        _detailsEmpty.IsVisible = p is null;
        if (p is null)
        {
            _detailsName.Text = CoreTools.Translate("Package details");
            _detailsBox.Summary = null;
            return;
        }

        _detailsName.Text = p.Name;
        int index = _rows.FindIndex(r => ReferenceEquals(r.Package, p));
        _detailsPosition.Text = CoreTools.Translate("{0} of {1}", index + 1, _rows.Count);
        _detailsBox.Summary = index >= 0 ? _detailsPosition : null;

        _idValue.Text = p.Id;
        _versionValue.Text = p.VersionString;
        string? newVersion = NewVersion(p);
        _versionArrow.IsVisible = _newVersionValue.IsVisible = !string.IsNullOrEmpty(newVersion);
        _newVersionValue.Text = newVersion;
        _sourceValue.Text = PackageRow.SafeSource(p);
        _managerValue.Text = p.Manager.DisplayName;
    }

    /// <summary>The version this package can be updated to (Updates, or an installed package with an update).</summary>
    private string? NewVersion(IPackage p) => _kind switch
    {
        PackagePageKind.Updates => p.NewVersionString,
        PackagePageKind.Installed => p.GetUpgradablePackage()?.NewVersionString,
        _ => null,
    };

    private string VersionText(IPackage p)
        => NewVersion(p) is { Length: > 0 } newVersion ? $"{p.VersionString} \u2192 {newVersion}" : p.VersionString;

    // ─── Actions ───────────────────────────────────────────────────────────

    private IReadOnlyList<TuiAction> BuildActions()
    {
        IPackage? focused = FocusedPackage;
        bool hasFocus = focused is not null;
        bool real = focused is not null && !focused.Source.IsVirtualManager && focused is not InvalidImportedPackage;
        var caps = focused?.Manager.Capabilities;
        var list = new List<TuiAction>();

        switch (_kind)
        {
            case PackagePageKind.Discover:
                list.Add(new(CoreTools.Translate("Install selection"), "i / Ctrl+Enter", () => RunMainActionAsync(SelectedOrFocused()), () => hasFocus));
                list.Add(new(CoreTools.Translate("Install as administrator"), null, () => InstallAsync(SelectedOrFocused(), elevated: true), () => caps?.CanRunAsAdmin == true));
                list.Add(new(CoreTools.Translate("Interactive installation"), null, () => InstallAsync(SelectedOrFocused(), interactive: true), () => caps?.CanRunInteractively == true));
                list.Add(new(CoreTools.Translate("Skip hash check"), null, () => InstallAsync(SelectedOrFocused(), skipHash: true), () => caps?.CanSkipIntegrityChecks == true));
                break;
            case PackagePageKind.Updates:
                list.Add(new(CoreTools.Translate("Update selection"), "u / Ctrl+Enter", () => RunMainActionAsync(SelectedOrFocused()), () => hasFocus));
                list.Add(new(CoreTools.Translate("Update all"), null, UpdateAllAsync, () => _rows.Count > 0));
                list.Add(new(CoreTools.Translate("Update as administrator"), null, () => UpdateAsync(SelectedOrFocused(), elevated: true), () => caps?.CanRunAsAdmin == true));
                list.Add(new(CoreTools.Translate("Interactive update"), null, () => UpdateAsync(SelectedOrFocused(), interactive: true), () => caps?.CanRunInteractively == true));
                list.Add(new(CoreTools.Translate("Skip hash check"), null, () => UpdateAsync(SelectedOrFocused(), skipHash: true), () => caps?.CanSkipIntegrityChecks == true));
                list.Add(TuiAction.Separator);
                list.Add(new(CoreTools.Translate("Uninstall package, then update it"), null, () => UninstallThenInstallAsync(focused!), () => real));
                list.Add(new(CoreTools.Translate("Uninstall package"), "x", () => UninstallAsync(SelectedOrFocused()), () => hasFocus));
                list.Add(TuiAction.Separator);
                list.Add(new(CoreTools.Translate("Ignore updates for this package"), "g", IgnoreSelectedAsync, () => hasFocus));
                list.Add(new(CoreTools.Translate("Skip this version"), null, () => SkipVersionAsync(focused!), () => hasFocus));
                list.Add(new(CoreTools.Translate("Pause updates for") + "…", null, () => PauseUpdatesAsync(focused!), () => hasFocus));
                list.Add(new(CoreTools.Translate("Manage ignored updates"), null, ManageIgnoredAsync));
                break;
            case PackagePageKind.Installed:
                list.Add(new(CoreTools.Translate("Uninstall selection"), "x / Ctrl+Enter", () => UninstallAsync(SelectedOrFocused()), () => hasFocus));
                list.Add(new(CoreTools.Translate("Uninstall as administrator"), null, () => UninstallAsync(SelectedOrFocused(), elevated: true), () => caps?.CanRunAsAdmin == true));
                list.Add(new(CoreTools.Translate("Interactive uninstall"), null, () => UninstallAsync(SelectedOrFocused(), interactive: true), () => caps?.CanRunInteractively == true));
                list.Add(new(CoreTools.Translate("Uninstall and remove data"), null, () => UninstallAsync(SelectedOrFocused(), removeData: true), () => caps?.CanRemoveDataOnUninstall == true));
                list.Add(TuiAction.Separator);
                string updateLabel = focused?.GetUpgradablePackage() is { } up
                    ? CoreTools.Translate("Update to version {0}", up.NewVersionString)
                    : CoreTools.Translate("Update");
                list.Add(new(updateLabel, "u", () => UpdateInstalledAsync(focused!), () => focused?.GetUpgradablePackage() is not null));
                list.Add(new(CoreTools.Translate("Update as administrator"), null, () => UpdateInstalledAsync(focused!, elevated: true), () => focused?.GetUpgradablePackage() is not null && caps?.CanRunAsAdmin == true));
                list.Add(new(CoreTools.Translate("Reinstall package"), null, () => ReinstallAsync(focused!), () => real));
                list.Add(new(CoreTools.Translate("Uninstall package, then reinstall it"), null, () => UninstallThenInstallAsync(focused!), () => real));
                list.Add(TuiAction.Separator);
                list.Add(new(CoreTools.Translate("Ignore updates for this package") + " / " + CoreTools.Translate("Do not ignore updates for this package anymore"), "g", IgnoreSelectedAsync, () => real));
                list.Add(new(CoreTools.Translate("Manage ignored updates"), null, ManageIgnoredAsync));
                break;
            case PackagePageKind.Bundles:
                list.Add(new(CoreTools.Translate("Install selection"), "i / Ctrl+Enter", () => RunMainActionAsync(SelectedOrFocused()), () => hasFocus));
                list.Add(new(CoreTools.Translate("Install as administrator"), null, () => InstallFromBundleAsync(SelectedOrFocused(), elevated: true), () => real && caps?.CanRunAsAdmin == true));
                list.Add(new(CoreTools.Translate("Interactive installation"), null, () => InstallFromBundleAsync(SelectedOrFocused(), interactive: true), () => real && caps?.CanRunInteractively == true));
                list.Add(new(CoreTools.Translate("Skip hash checks"), null, () => InstallFromBundleAsync(SelectedOrFocused(), skipHash: true), () => real && caps?.CanSkipIntegrityChecks == true));
                list.Add(TuiAction.Separator);
                list.Add(new(CoreTools.Translate("New bundle"), "n", NewBundleAsync));
                list.Add(new(CoreTools.Translate("Open existing bundle"), "Ctrl+O", OpenBundleAsync));
                list.Add(new(CoreTools.Translate("Save as"), "Ctrl+S", SaveBundleAsync, () => _rows.Count > 0));
                list.Add(new(CoreTools.Translate("Create .ps1 script"), null, CreateScriptAsync, () => _rows.Count > 0));
                list.Add(new(CoreTools.Translate("Remove selection from bundle"), "Del", () =>
                {
                    RemoveFromBundle(SelectedOrFocused());
                    return Task.CompletedTask;
                }, () => hasFocus));
                break;
        }

        list.Add(TuiAction.Separator);
        list.Add(new(RoleOptionsLabel(), "o / Alt+Enter", ShowOptionsAsync, () => real));
        list.Add(new(CoreTools.Translate("Package details"), "Enter", ShowDetailsAsync, () => hasFocus && focused is not InvalidImportedPackage));
        if (_kind != PackagePageKind.Bundles)
            list.Add(new(ManualLabel(), null, ShowManualCommandAsync, () => real));
        if (_kind is PackagePageKind.Updates or PackagePageKind.Installed)
            list.Add(new(CoreTools.Translate("Open install location"), null, OpenInstallLocationAsync, () => real));
        list.Add(new(CoreTools.Translate("Download installer"), null, DownloadAsync, () => real && caps?.CanDownloadInstaller == true));
        if (_kind is PackagePageKind.Discover or PackagePageKind.Installed or PackagePageKind.Updates)
            list.Add(new(CoreTools.Translate("Add selection to bundle"), "b", () => AddToBundleAsync(SelectedOrFocused()), () => hasFocus));
        if (_kind is PackagePageKind.Updates or PackagePageKind.Installed)
            list.Add(new(CoreTools.Translate("Export to CSV"), null, ExportCsvAsync, () => _rows.Count > 0));
        list.Add(new(CoreTools.Translate("Copy package info"), "Ctrl+C", CopySelectedAsync, () => hasFocus));
        list.Add(TuiAction.Separator);
        list.Add(new(CoreTools.Translate("Select all") + " / " + CoreTools.Translate("Clear selection"), "Ctrl+A", () =>
        {
            ToggleSelectAll();
            return Task.CompletedTask;
        }, () => _rows.Count > 0));
        list.Add(new(CoreTools.Translate("Search options") + "…", "F3", ShowSearchOptionsAsync));
        list.Add(new(CoreTools.Translate("Filter by source") + "…", "f", ShowSourceFilterAsync));
        list.Add(new(CoreTools.Translate("Sort by") + "…", "s", ShowSortMenuAsync));
        if (_kind != PackagePageKind.Bundles)
            list.Add(new(CoreTools.Translate("Reload"), "r / F5", () =>
            {
                Reload();
                return Task.CompletedTask;
            }));
        return list;
    }

    private string RoleOptionsLabel() => _kind switch
    {
        PackagePageKind.Updates => CoreTools.Translate("Update options"),
        PackagePageKind.Installed => CoreTools.Translate("Uninstall options"),
        _ => CoreTools.Translate("Install options"),
    };

    private string ManualLabel() => _kind switch
    {
        PackagePageKind.Updates => CoreTools.Translate("Manual update"),
        PackagePageKind.Installed => CoreTools.Translate("Manual uninstall"),
        _ => CoreTools.Translate("Manual install"),
    };

    public async Task ShowActionMenuAsync()
    {
        var actions = BuildActions();
        var choices = actions.Select(a => a.IsSeparator ? TuiChoice.Separator : new TuiChoice(a.Label, a.IsEnabled, a.Shortcut)).ToList();
        int? picked = await TuiPrompts.ChooseAsync(FocusedPackage?.Name ?? CoreTools.Translate("Actions"), choices);
        if (picked is int index) await actions[index].Run();
    }

    private Task RunMainActionAsync(IReadOnlyList<IPackage> packages) => _kind switch
    {
        PackagePageKind.Discover => InstallAsync(packages),
        PackagePageKind.Updates => UpdateAsync(packages),
        PackagePageKind.Installed => UninstallAsync(packages),
        _ => InstallFromBundleAsync(packages),
    };

    private async Task InstallAsync(IReadOnlyList<IPackage> packages, bool? elevated = null, bool? interactive = null, bool? skipHash = null)
    {
        if (packages.Count == 0) return;
        int n = await TuiPackageActions.InstallAsync(packages, elevated, interactive, skipHash);
        AfterEnqueue(n, CoreTools.Translate("Install"));
    }

    private async Task UpdateAsync(IReadOnlyList<IPackage> packages, bool? elevated = null, bool? interactive = null, bool? skipHash = null)
    {
        if (packages.Count == 0) return;
        int n = await TuiPackageActions.UpdateAsync(packages, elevated, interactive, skipHash);
        AfterEnqueue(n, CoreTools.Translate("Update"));
    }

    private async Task UpdateAllAsync()
    {
        int n = await TuiPackageActions.UpdateAllAsync();
        AfterEnqueue(n, CoreTools.Translate("Update"));
    }

    private async Task UninstallAsync(IReadOnlyList<IPackage> packages, bool? elevated = null, bool? interactive = null, bool? removeData = null)
    {
        if (packages.Count == 0) return;
        string names = string.Join(", ", packages.Take(5).Select(p => p.Name)) + (packages.Count > 5 ? $" (+{packages.Count - 5})" : "");
        if (!await TuiPrompts.ConfirmAsync(CoreTools.Translate("Uninstall"),
                CoreTools.Translate("Do you really want to uninstall the following {0} packages?", packages.Count) + "\n\n" + names))
            return;
        int n = await TuiPackageActions.UninstallAsync(packages, elevated, interactive, removeData);
        AfterEnqueue(n, CoreTools.Translate("Uninstall"));
    }

    private async Task UpdateInstalledAsync(IPackage installed, bool? elevated = null)
    {
        if (!await TuiPackageActions.UpdateInstalledAsync(installed, elevated))
        {
            TuiNotifications.Info(CoreTools.Translate("Update"), CoreTools.Translate("No updates are available for {0}", installed.Name));
            return;
        }

        AfterEnqueue(1, CoreTools.Translate("Update"));
    }

    private async Task ReinstallAsync(IPackage package)
    {
        if (await TuiPackageActions.ReinstallAsync(package)) AfterEnqueue(1, CoreTools.Translate("Reinstall"));
    }

    private async Task UninstallThenInstallAsync(IPackage package)
    {
        if (await TuiPackageActions.UninstallThenInstallAsync(package)) AfterEnqueue(1, CoreTools.Translate("Uninstall and reinstall"));
    }

    private async Task InstallFromBundleAsync(IReadOnlyList<IPackage> packages, bool? elevated = null, bool? interactive = null, bool? skipHash = null)
    {
        var targets = TuiBundleService.InstallTargets(packages);
        if (targets.Count == 0)
        {
            TuiNotifications.Info(CoreTools.Translate("Install"), CoreTools.Translate("The selected packages are already installed"));
            return;
        }

        int n = await TuiBundleService.InstallAsync(targets, elevated, interactive, skipHash);
        AfterEnqueue(n, CoreTools.Translate("Install"));
    }

    private void AfterEnqueue(int count, string verb)
    {
        if (count == 0)
        {
            TuiNotifications.Info(verb, CoreTools.Translate("Nothing to do: an operation is already pending for the selection."));
            return;
        }

        TuiNotifications.Info(verb, CoreTools.Translate("{0} operation(s) added to the queue", count));
    }

    private async Task ShowDetailsAsync()
    {
        if (FocusedPackage is not { } package || package is InvalidImportedPackage) return;
        bool proceed = await PackageDetailsDialog.ShowAsync(package, Role);
        if (proceed) await RunMainActionAsync([package]);
    }

    private async Task ShowOptionsAsync()
    {
        if (FocusedPackage is not { } package || package.Source.IsVirtualManager || package is InvalidImportedPackage) return;
        OperationType role = Role == OperationType.None ? OperationType.Install : Role;
        bool proceed = await InstallOptionsDialog.ShowForPackageAsync(package, role);
        if (_kind == PackagePageKind.Bundles) TuiBundleService.HasUnsavedChanges = true;
        if (proceed) await RunMainActionAsync([package]);
    }

    private async Task ShowManualCommandAsync()
    {
        if (FocusedPackage is not { } package) return;
        string? command = await TuiPackageActions.BuildManualCommandAsync(package, Role == OperationType.None ? OperationType.Install : Role);
        if (command is null)
        {
            TuiNotifications.Warning(ManualLabel(), CoreTools.Translate("No command line is available for this package."));
            return;
        }

        await TuiPrompts.ShowTextAsync(ManualLabel(),
            CoreTools.Translate("Run this command in a terminal to perform the operation manually:") + "\n\n" + command);
    }

    private async Task OpenInstallLocationAsync()
    {
        if (FocusedPackage is not { } package) return;
        string? path = await Task.Run(() => TuiPackageActions.InstallLocation(package));
        if (path is null)
        {
            TuiNotifications.Warning(CoreTools.Translate("Open install location"), CoreTools.Translate("The install location of this package is unknown."));
            return;
        }

        await TuiPrompts.ShowTextAsync(CoreTools.Translate("Open install location"), path,
            [(CoreTools.Translate("Open"), () => TuiPackageActions.OpenExternally(path))]);
    }

    private async Task DownloadAsync()
    {
        var packages = SelectedOrFocused().Where(p => p.Manager.Capabilities.CanDownloadInstaller && !p.Source.IsVirtualManager).ToList();
        if (packages.Count == 0) return;
        string? folder = await TuiPrompts.AskTextAsync(CoreTools.Translate("Download installer"),
            CoreTools.Translate("Download the installer(s) to this folder:"), TuiPackageActions.DefaultDownloadDirectory());
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        int n = TuiPackageActions.Download(packages, folder);
        AfterEnqueue(n, CoreTools.Translate("Download"));
    }

    private async Task AddToBundleAsync(IReadOnlyList<IPackage> packages)
    {
        if (packages.Count == 0) return;
        await TuiBundleService.AddPackagesAsync(packages);
        TuiNotifications.Success(CoreTools.Translate("Package Bundles"), CoreTools.Translate("{0} package(s) added to the bundle", packages.Count));
    }

    private async Task IgnoreSelectedAsync()
    {
        var packages = SelectedOrFocused();
        if (packages.Count == 0) return;
        if (_kind == PackagePageKind.Installed)
        {
            bool nowIgnored = false;
            foreach (var p in packages) nowIgnored = await TuiPackageActions.ToggleIgnoreUpdatesAsync(p);
            TuiNotifications.Info(CoreTools.Translate("Ignored updates"), nowIgnored
                ? CoreTools.Translate("Updates for {0} will be ignored", packages[0].Name)
                : CoreTools.Translate("Updates for {0} will no longer be ignored", packages[0].Name));
            return;
        }

        foreach (var p in packages) await TuiPackageActions.IgnoreUpdatesAsync(p);
        TuiNotifications.Info(CoreTools.Translate("Ignored updates"), CoreTools.Translate("{0} package(s) will no longer show updates", packages.Count));
    }

    private async Task SkipVersionAsync(IPackage package)
    {
        await TuiPackageActions.SkipVersionAsync(package);
        TuiNotifications.Info(CoreTools.Translate("Skip this version"), $"{package.Name} {package.NewVersionString}");
    }

    private async Task PauseUpdatesAsync(IPackage package)
    {
        var durations = TuiPackageActions.PauseDurations;
        int? picked = await TuiPrompts.ChooseAsync(CoreTools.Translate("Pause updates for"),
            durations.Select(d => new TuiChoice(d.StringRepresentation())).ToList());
        if (picked is not int i) return;
        await TuiPackageActions.PauseUpdatesAsync(package, durations[i]);
        TuiNotifications.Info(CoreTools.Translate("Pause updates for"), $"{package.Name}: {durations[i].StringRepresentation()}");
    }

    private Task ManageIgnoredAsync() => IgnoredUpdatesDialog.ShowAsync();

    private async Task ExportCsvAsync()
    {
        var packages = CheckedPackages();
        if (packages.Count == 0) packages = _rows.Select(r => r.Package).ToList();
        string suggested = Path.Join(TuiPackageActions.DefaultDownloadDirectory(),
            (_kind == PackagePageKind.Updates ? "updates" : "installed") + ".csv");
        string? path = await TuiPrompts.AskTextAsync(CoreTools.Translate("Export to CSV"), CoreTools.Translate("Save to file:"), suggested);
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            await TuiPackageActions.WriteCsvAsync(path, packages, _kind == PackagePageKind.Updates);
            TuiNotifications.Success(CoreTools.Translate("Export to CSV"), path);
        }
        catch (Exception ex)
        {
            TuiNotifications.Error(CoreTools.Translate("Export to CSV"), ex.Message);
        }
    }

    private async Task CopySelectedAsync()
    {
        var packages = SelectedOrFocused();
        if (packages.Count == 0) return;
        string text = string.Join(Environment.NewLine + Environment.NewLine, packages.Select(p => string.Join(Environment.NewLine,
            $"Name: {p.Name}", $"Id: {p.Id}", $"Version: {p.VersionString}",
            _kind == PackagePageKind.Updates ? $"New version: {p.NewVersionString}" : null,
            $"Source: {PackageRow.SafeSource(p)}", $"Manager: {p.Manager.DisplayName}").Replace(Environment.NewLine + Environment.NewLine, Environment.NewLine)));
        await TuiClipboard.CopyAsync(this, packages.Count == 1 ? packages[0].Name : $"{packages.Count} packages", text);
    }

    private async Task ShowSearchOptionsAsync()
    {
        await SearchOptionsDialog.ShowAsync(this);
        UpdateTitle();
        Refresh();
    }

    internal void SetSearchOptions(PackageSearchMode mode, bool caseSensitive, bool ignoreSpecial, bool instant)
    {
        SearchMode = _kind != PackagePageKind.Discover && mode == PackageSearchMode.Similar ? PackageSearchMode.Both : mode;
        CaseSensitive = caseSensitive;
        IgnoreSpecialCharacters = ignoreSpecial;
        InstantSearch = instant;
        Settings.SetDictionaryItem(Settings.K.DisableInstantSearch, _pageName, !instant);
    }

    private async Task ShowSourceFilterAsync()
    {
        IReadOnlyList<IPackage> all;
        try
        {
            all = Loader.Packages;
        }
        catch
        {
            all = [];
        }

        var entries = all.GroupBy(SourceKey)
            .Select(g => (Key: g.Key, Label: g.Key == "|Local" ? CoreTools.Translate("Local") : SourceLabel(g.First()), Count: g.Count()))
            .OrderBy(e => e.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (entries.Count == 0)
        {
            TuiNotifications.Info(CoreTools.Translate("Filter by source"), CoreTools.Translate("There are no packages to filter yet."));
            return;
        }

        var result = await SourceFilterDialog.ShowAsync(entries.Select(e => ($"{e.Label}  ({e.Count})", e.Key, !_hiddenSources.Contains(e.Key))).ToList());
        if (result is null) return;
        _hiddenSources.Clear();
        foreach (var (key, visible) in result)
            if (!visible) _hiddenSources.Add(key);
        Refresh();
    }

    private static string SourceLabel(IPackage p)
        => p.Manager.Capabilities.SupportsCustomSources ? $"{p.Manager.DisplayName}: {p.Source.Name}" : p.Manager.DisplayName;

    private async Task ShowSortMenuAsync()
    {
        var fields = _kind == PackagePageKind.Updates ? new[] { 0, 1, 2, 3, 4 } : new[] { 0, 1, 2, 4 };
        var choices = fields.Select(f => new TuiChoice(SortFieldName(f), Hint: f == SortField ? (SortAscending ? "▲" : "▼") : null)).ToList();
        choices.Add(TuiChoice.Separator);
        choices.Add(new TuiChoice(CoreTools.Translate("Ascending"), Hint: SortAscending ? "●" : null));
        choices.Add(new TuiChoice(CoreTools.Translate("Descending"), Hint: SortAscending ? null : "●"));
        int? picked = await TuiPrompts.ChooseAsync(CoreTools.Translate("Sort by"), choices, Array.IndexOf(fields, SortField));
        if (picked is not int i) return;
        if (i < fields.Length) SetSort(fields[i], SortAscending);
        else if (i == fields.Length + 1) SetSort(SortField, true);
        else if (i == fields.Length + 2) SetSort(SortField, false);
    }

    // ─── Bundles ───────────────────────────────────────────────────────────

    private void RemoveFromBundle(IReadOnlyList<IPackage> packages)
    {
        if (packages.Count == 0) return;
        TuiBundleService.Remove(packages);
    }

    public async Task<bool> NewBundleAsync()
    {
        if (!await ConfirmDiscardAsync()) return false;
        TuiBundleService.Clear();
        return true;
    }

    private static async Task<bool> ConfirmDiscardAsync()
    {
        if (!TuiBundleService.HasUnsavedChanges || PackageBundlesLoader.Instance.Packages.Count == 0) return true;
        return await TuiPrompts.ConfirmAsync(CoreTools.Translate("Unsaved changes"),
            CoreTools.Translate("The current bundle has unsaved changes. Do you want to discard them?"),
            CoreTools.Translate("Discard"), CoreTools.Translate("Cancel"));
    }

    public async Task OpenBundleAsync()
    {
        if (!await ConfirmDiscardAsync()) return;
        string? path = await TuiPrompts.AskTextAsync(CoreTools.Translate("Open existing bundle"),
            CoreTools.Translate("Path of the bundle file (.ubundle, .json, .yaml or .xml):"),
            Path.Join(TuiPackageActions.DefaultDownloadDirectory(), CoreTools.Translate("Package bundle") + ".ubundle"),
            p => File.Exists(p.Trim().Trim('"')) ? null : CoreTools.Translate("The file does not exist."));
        if (string.IsNullOrWhiteSpace(path)) return;
        await OpenBundleFileAsync(path.Trim().Trim('"'));
    }

    public async Task OpenBundleFileAsync(string path)
    {
        try
        {
            var (count, report) = await TuiBundleService.OpenFileAsync(path);
            TuiNotifications.Success(CoreTools.Translate("Package Bundles"), CoreTools.Translate("{0} package(s) loaded from {1}", count, Path.GetFileName(path)));
            if (!report.IsEmpty)
                await TuiPrompts.ShowTextAsync(CoreTools.Translate("Bundle security report"), TuiBundleService.DescribeReport(report));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            await TuiPrompts.InfoAsync(CoreTools.Translate("The package bundle is not valid"),
                CoreTools.Translate("The bundle you are trying to load appears to be invalid. Please check the file and try again.") + "\n\n" + ex.Message);
        }
    }

    public async Task SaveBundleAsync()
    {
        string? path = await TuiPrompts.AskTextAsync(CoreTools.Translate("Save as"),
            CoreTools.Translate("Save the bundle to (.ubundle or .json):"),
            Path.Join(TuiPackageActions.DefaultDownloadDirectory(), CoreTools.Translate("Package bundle") + ".ubundle"));
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            await TuiBundleService.SaveAsync(path.Trim().Trim('"'));
            TuiNotifications.Success(CoreTools.Translate("Package Bundles"), CoreTools.Translate("Bundle saved to {0}", path));
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            TuiNotifications.Error(CoreTools.Translate("Could not create bundle"), ex.Message);
        }
    }

    private async Task CreateScriptAsync()
    {
        string? path = await TuiPrompts.AskTextAsync(CoreTools.Translate("Create .ps1 script"),
            CoreTools.Translate("Save the PowerShell install script to:"),
            Path.Join(TuiPackageActions.DefaultDownloadDirectory(), CoreTools.Translate("Install script") + ".ps1"));
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            int n = await TuiBundleService.CreateInstallScriptAsync(path.Trim().Trim('"'));
            TuiNotifications.Success(CoreTools.Translate("Success!"), CoreTools.Translate("The installation script saved to {0}", path) + $" ({n})");
        }
        catch (Exception ex)
        {
            TuiNotifications.Error(CoreTools.Translate("An error occurred"), ex.Message);
        }
    }
}
