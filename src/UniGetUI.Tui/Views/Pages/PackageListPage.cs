using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageOperations;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Theme;

namespace UniGetUI.Tui.Views.Pages;

internal enum PackagePageKind
{
    Installed,
    Updates,
    Discover,
}

/// <summary>
/// A live, read-only package list backed by one of the engine's package loaders
/// (Installed / Updates / Discover). Columns are rendered as fixed-width monospace strings,
/// exploiting the terminal's character grid for alignment. The page subscribes to its loader on
/// attach and unsubscribes on detach; loader events (which arrive on background threads) are
/// marshalled to the UI thread and coalesced into a single refresh.
/// </summary>
internal sealed class PackageListPage : UserControl, IFocusablePage
{
    private sealed record PackageRow(string Text, IPackage Package)
    {
        public override string ToString() => Text;
    }

    private readonly PackagePageKind _kind;
    private readonly Func<AbstractPackageLoader?> _loaderFn;
    private readonly (string Title, int Width)[] _schema;

    // Rows live inside a ListBox → ListBoxItem chain whose Consolonia theme adds a 1-char ListBox
    // border plus 1-char ListBoxItem padding on the left. The column-header TextBlock is a sibling
    // with no such indent, so it must be nudged right by the same amount to line the titles up with
    // the row columns.
    private const int ListRowLeftIndent = 2; // ListBox border (1) + ListBoxItem padding (1)

    private readonly TextBox _search;
    private readonly TextBlock _status;
    private readonly TextBlock _columnHeader;
    private readonly ListBox _list;
    private readonly StackPanel _details;

    private List<PackageRow> _rows = new();
    private AbstractPackageLoader? _subscribed;
    private string _lastQuery = string.Empty;
    private volatile bool _dirty;
    private bool _startingOp;

    // Non-Discover pages filter the in-memory list on every keystroke. A full filter/sort/ListBox
    // rebuild on the dispatcher per keypress starves keyboard input on large lists, so coalesce
    // typing through a short debounce window before scheduling a refresh.
    private readonly DispatcherTimer? _filterDebounceTimer;

    public PackageListPage(PackagePageKind kind)
    {
        _kind = kind;
        Func<AbstractPackageLoader?> loaderFn;
        (string Title, int Width)[] schema;
        switch (kind)
        {
            case PackagePageKind.Installed:
                loaderFn = () => InstalledPackagesLoader.Instance;
                schema = new[] { ("Name", 26), ("Id", 30), ("Version", 16), ("Source", 18) };
                break;
            case PackagePageKind.Updates:
                loaderFn = () => UpgradablePackagesLoader.Instance;
                schema = new[] { ("Name", 24), ("Id", 26), ("Installed", 13), ("Available", 13), ("Source", 14) };
                break;
            default:
                loaderFn = () => DiscoverablePackagesLoader.Instance;
                schema = new[] { ("Name", 28), ("Id", 32), ("Version", 14), ("Source", 16) };
                break;
        }

        _loaderFn = loaderFn;
        _schema = schema;

        string title = kind switch
        {
            PackagePageKind.Installed => "Installed Packages",
            PackagePageKind.Updates => "Available Updates",
            _ => "Discover Packages",
        };

        _search = new TextBox
        {
            Watermark = kind == PackagePageKind.Discover
                ? "package name…  (press Enter to search)"
                : "type to filter the list",
        };

        _status = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#C0C0C0")), Margin = new Thickness(0, 0, 0, 1),
        };

        _columnHeader = new TextBlock
        {
            Text = HeaderText(),
            Foreground = DevolutionsPalette.BrandBrush,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(ListRowLeftIndent, 0, 0, 0),
        };

        _list = new ListBox
        {
            Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<PackageRow>(
                (row, _) => new TextBlock { Text = row?.Text ?? string.Empty },
                supportsRecycling: true),
        };
        _list.SelectionChanged += (_, _) => UpdateDetails();
        _list.KeyDown += OnListKeyDown;
        // Single-letter action keys (i/u) would otherwise trigger the ListBox's built-in type-ahead
        // search, which runs off TextInput. Intercept those characters on the list's TextInput tunnel
        // so they drive operations instead of moving the selection.
        _list.AddHandler(TextInputEvent, OnListTextInput, RoutingStrategies.Tunnel);

        _details = new StackPanel { Spacing = 0 };
        ClearDetails();

        Content = BuildLayout(title);

        _search.KeyDown += OnSearchKeyDown;
        if (kind != PackagePageKind.Discover)
        {
            _filterDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            _filterDebounceTimer.Tick += OnFilterDebounceTick;
            _search.TextChanged += OnFilterTextChanged;
        }
    }

    // Restart the debounce window on each keystroke. Refresh is only scheduled once typing pauses
    // for the timer interval, coalescing bursts into a single filter/sort/render pass.
    private void OnFilterTextChanged(object? sender, TextChangedEventArgs e)
    {
        _filterDebounceTimer?.Stop();
        _filterDebounceTimer?.Start();
    }

    private void OnFilterDebounceTick(object? sender, EventArgs e)
    {
        _filterDebounceTimer?.Stop();
        ScheduleRefresh();
    }

    private Control BuildLayout(string title)
    {
        var searchRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 1) };
        var searchLabel = new TextBlock
        {
            Text = _kind == PackagePageKind.Discover ? "Search " : "Filter ",
            Foreground = DevolutionsPalette.BrandBrush,
            VerticalAlignment = VerticalAlignment.Center,
        };
        DockPanel.SetDock(searchLabel, Dock.Left);
        searchRow.Children.Add(searchLabel);
        searchRow.Children.Add(_search);

        var header = new StackPanel { Spacing = 0 };
        header.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = DevolutionsPalette.BrandBrush,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 1),
        });
        header.Children.Add(searchRow);
        header.Children.Add(_status);
        header.Children.Add(_columnHeader);

        var detailsBorder = new Border
        {
            BorderThickness = new Thickness(0, 1, 0, 0),
            BorderBrush = new SolidColorBrush(Color.Parse("#3A3A3A")),
            Padding = new Thickness(0, 1, 0, 0),
            Margin = new Thickness(0, 1, 0, 0),
            Child = _details,
        };

        var root = new DockPanel { LastChildFill = true, Margin = new Thickness(2, 1, 2, 1) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(detailsBorder, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(detailsBorder);
        root.Children.Add(_list);
        return root;
    }

    /// <summary>Moves keyboard focus to this page's primary input (the search box).</summary>
    public bool FocusPrimary() => _search.Focus();

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter when _kind == PackagePageKind.Discover:
            {
                string query = (_search.Text ?? string.Empty).Trim();
                if (query.Length > 0)
                {
                    _lastQuery = query;
                    _status.Text = $"Searching for \"{query}\"…";
                    _ = DiscoverablePackagesLoader.Instance.ReloadPackages(query);
                }
                else
                {
                    _status.Text = "Type a search query first.";
                }

                e.Handled = true;
                break;
            }
            case Key.Enter:
            case Key.Down:
                FocusList();
                e.Handled = true;
                break;
        }
    }

    private void FocusList()
    {
        if (_rows.Count == 0) return;
        TuiFocus.SeatListFocus(_list);
    }

    // The action this page's "act" key (i/u) performs on the selected package, or null if none.
    private OperationType? ActionForKey(Key key) => (_kind, key) switch
    {
        (PackagePageKind.Discover, Key.I) => OperationType.Install,
        (PackagePageKind.Updates, Key.U) => OperationType.Update,
        (PackagePageKind.Installed, Key.U) => OperationType.Uninstall,
        _ => null,
    };

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.B)
        {
            e.Handled = true;
            AddSelectedToBundle();
            return;
        }

        if (ActionForKey(e.Key) is { } action)
        {
            e.Handled = true;
            _ = StartOperationAsync(action);
        }
    }

    private void OnListTextInput(object? sender, TextInputEventArgs e)
    {
        // Suppress type-ahead for the characters we bind as action keys so they don't move selection.
        if (e.Text is "b" or "B") e.Handled = true;
        else if (e.Text is "i" or "I" && _kind == PackagePageKind.Discover) e.Handled = true;
        else if (e.Text is "u" or "U" && _kind is PackagePageKind.Updates or PackagePageKind.Installed)
            e.Handled = true;
    }

    private async void AddSelectedToBundle()
    {
        if ((_list.SelectedItem as PackageRow)?.Package is not { } package) return;
        try
        {
            await PackageBundlesLoader.Instance.AddPackagesAsync(new[] { package });
            _status.Text = $"Added {package.Name} to the bundle.";
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not add to bundle: {ex.Message}";
            Logger.Error(ex);
        }
    }

    // Menu-bar entry points. They mirror the keyboard actions, acting on the current selection so
    // the top menu and the in-list hotkeys stay in lockstep.
    internal void MenuOperation(OperationType type)
    {
        bool supported = (_kind, type) switch
        {
            (PackagePageKind.Discover, OperationType.Install) => true,
            (PackagePageKind.Updates, OperationType.Update) => true,
            (PackagePageKind.Installed, OperationType.Uninstall) => true,
            _ => false,
        };
        if (supported) _ = StartOperationAsync(type);
        else _status.Text = $"{type} isn't available on this page.";
    }

    internal void MenuAddToBundle() => AddSelectedToBundle();

    internal void MenuReload()
    {
        if (_kind == PackagePageKind.Discover)
        {
            if (_lastQuery.Length > 0) _ = DiscoverablePackagesLoader.Instance.ReloadPackages(_lastQuery);
            else _status.Text = "Type a search query first.";
            return;
        }

        if (_loaderFn() is { } loader) _ = loader.ReloadPackages();
    }

    private async Task StartOperationAsync(OperationType type)
    {
        if (_startingOp) return;
        if ((_list.SelectedItem as PackageRow)?.Package is not { } package) return;

        try
        {
            _startingOp = true;
            _status.Text = $"Preparing to {type.ToString().ToLowerInvariant()} {package.Name}…";

            // Simulation mode (UNIGETUI_TUI_SIMULATE=1): never touch the system — enqueue a fake op
            // through the same registry path so the Operations page behaves identically.
            if (SimulatedOperation.IsEnabled)
            {
                TuiOperationRegistry.Start(new SimulatedOperation(type.ToString(), package.Name));
                TuiShell.Navigate("operations");
                return;
            }

            // M3 runs the existing non-interactive redirected pipeline only; elevation and interactive
            // installers are explicitly not yet supported in the TUI, so force them off.
            InstallOptions opts = await InstallOptionsFactory.LoadApplicableAsync(
                package, elevated: false, interactive: false);

            AbstractOperation op = type switch
            {
                OperationType.Install => new InstallPackageOperation(package, opts),
                OperationType.Update => new UpdatePackageOperation(package, opts),
                OperationType.Uninstall => new UninstallPackageOperation(package, opts),
                _ => throw new InvalidOperationException($"Unsupported operation {type}"),
            };

            TuiOperationRegistry.Start(op);
            TuiShell.Navigate("operations");
        }
        catch (Exception ex)
        {
            _status.Text = $"Could not start operation: {ex.Message}";
            Logger.Error(ex);
        }
        finally
        {
            _startingOp = false;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Subscribe();
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _filterDebounceTimer?.Stop();
        Unsubscribe();
    }

    private void Subscribe()
    {
        AbstractPackageLoader? loader = _loaderFn();
        if (loader is null || ReferenceEquals(loader, _subscribed)) return;

        Unsubscribe();
        loader.PackagesChanged += OnPackagesChanged;
        loader.StartedLoading += OnLoaderChanged;
        loader.FinishedLoading += OnLoaderChanged;
        _subscribed = loader;
    }

    private void Unsubscribe()
    {
        if (_subscribed is null) return;
        _subscribed.PackagesChanged -= OnPackagesChanged;
        _subscribed.StartedLoading -= OnLoaderChanged;
        _subscribed.FinishedLoading -= OnLoaderChanged;
        _subscribed = null;
    }

    private void OnPackagesChanged(object? sender, PackagesChangedEvent e) => ScheduleRefresh();

    // Loader events arrive on background threads and can fire in rapid bursts during a load.
    // Mark dirty and post a pump; the first pump per dispatcher cycle clears the flag and the
    // rest no-op, coalescing the burst into a single refresh.
    private void OnLoaderChanged(object? sender, EventArgs e) => ScheduleRefresh();

    private void ScheduleRefresh()
    {
        _dirty = true;
        Dispatcher.UIThread.Post(PumpRefresh, DispatcherPriority.Background);
    }

    private void PumpRefresh()
    {
        if (!_dirty) return;
        _dirty = false;
        Refresh();
    }

    private void Refresh()
    {
        AbstractPackageLoader? loader = _loaderFn();
        IReadOnlyList<IPackage> snapshot = loader?.Packages ?? (IReadOnlyList<IPackage>)Array.Empty<IPackage>();
        string filter = (_search.Text ?? string.Empty).Trim();

        IEnumerable<IPackage> query = snapshot;
        if (_kind != PackagePageKind.Discover && filter.Length > 0)
        {
            query = query.Where(p =>
                ContainsCI(p.Name, filter)
                || ContainsCI(p.Id, filter)
                || ContainsCI(SafeSourceName(p), filter));
        }

        var rows = query
            .OrderBy(p => p.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .Select(p => new PackageRow(RowText(p), p))
            .ToList();

        long? selectedHash = (_list.SelectedItem as PackageRow) is { } prev ? SafeHash(prev.Package) : null;

        _rows = rows;
        _list.ItemsSource = _rows;

        if (selectedHash is long hash)
        {
            PackageRow? match = _rows.FirstOrDefault(r => SafeHash(r.Package) == hash);
            if (match is not null) _list.SelectedItem = match;
        }

        UpdateStatus(loader, filter);
        UpdateDetails();
    }

    private void UpdateStatus(AbstractPackageLoader? loader, string filter)
    {
        bool loading = loader?.IsLoading ?? false;
        string noun = _kind == PackagePageKind.Updates ? "update" : "package";
        int n = _rows.Count;
        string plural = n == 1 ? string.Empty : "s";

        if (loading)
        {
            _status.Text = $"Loading…  ({n} {noun}{plural} so far)";
        }
        else if (n == 0)
        {
            _status.Text = _kind switch
            {
                PackagePageKind.Discover => _lastQuery.Length == 0
                    ? "Type a query and press Enter to search every manager."
                    : $"No results for \"{_lastQuery}\".",
                _ => filter.Length > 0 ? "No matches for the current filter." : $"No {noun}s found.",
            };
        }
        else
        {
            string suffix = filter.Length > 0 && _kind != PackagePageKind.Discover ? "  (filtered)" : string.Empty;
            string action = _kind switch
            {
                PackagePageKind.Discover => "   ·   i install   ·   b add to bundle",
                PackagePageKind.Updates => "   ·   u update   ·   b add to bundle",
                PackagePageKind.Installed => "   ·   u uninstall   ·   b add to bundle",
                _ => string.Empty,
            };
            _status.Text = $"{n} {noun}{plural}{suffix}{action}";
        }
    }

    private void ClearDetails()
    {
        _details.Children.Clear();
        _details.Children.Add(new TextBlock
        {
            Text = "Select a package to see its details.", Foreground = new SolidColorBrush(Color.Parse("#BBBBBB")),
        });
    }

    private void UpdateDetails()
    {
        int index = _list.SelectedIndex;
        if (index < 0 || index >= _rows.Count)
        {
            ClearDetails();
            return;
        }

        IPackage p = _rows[index].Package;
        _details.Children.Clear();

        void Line(string label, string value)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("14,*") };
            var l = new TextBlock { Text = label, Foreground = DevolutionsPalette.BrandBrush };
            var v = new TextBlock { Text = value, TextWrapping = TextWrapping.NoWrap };
            Grid.SetColumn(l, 0);
            Grid.SetColumn(v, 1);
            grid.Children.Add(l);
            grid.Children.Add(v);
            _details.Children.Add(grid);
        }

        Line("Name", Safe(() => p.Name));
        Line("Id", Safe(() => p.Id));
        if (_kind == PackagePageKind.Updates)
        {
            Line("Installed", Safe(() => p.VersionString));
            Line("Available", Safe(() => p.NewVersionString));
        }
        else
        {
            Line("Version", Safe(() => p.VersionString));
        }

        Line("Source", Safe(() => p.Source.Name));
        Line("Manager", Safe(() => p.Manager.DisplayName));
    }

    private string RowText(IPackage p)
    {
        string[] values = _kind switch
        {
            PackagePageKind.Updates => new[]
            {
                Safe(() => p.Name), Safe(() => p.Id), Safe(() => p.VersionString), Safe(() => p.NewVersionString),
                Safe(() => p.Source.Name),
            },
            _ => new[]
            {
                Safe(() => p.Name), Safe(() => p.Id), Safe(() => p.VersionString), Safe(() => p.Source.Name),
            },
        };

        return string.Join(" ", values.Select((v, i) => PadFit(v, _schema[i].Width)));
    }

    private string HeaderText()
        => string.Join(" ", _schema.Select(s => PadFit(s.Title, s.Width)));

    private static bool ContainsCI(string? haystack, string needle)
        => haystack is not null && haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    private static string SafeSourceName(IPackage p)
    {
        try
        {
            return p.Source.Name ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static long SafeHash(IPackage p)
    {
        try
        {
            return p.GetHash();
        }
        catch
        {
            return 0;
        }
    }

    private static string Safe(Func<string?> getter)
    {
        try
        {
            return (getter() ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ').Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string PadFit(string value, int width)
    {
        value ??= string.Empty;
        if (value.Length > width)
            value = width <= 1 ? value[..width] : value[..(width - 1)] + "…";
        return value.PadRight(width);
    }
}
