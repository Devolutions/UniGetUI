using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Theme;
using UniGetUI.Tui.Views.Dialogs;

namespace UniGetUI.Tui.Views.Pages;

/// <summary>
/// The desktop "UniGetUI Log" and "Package Manager logs" pages in one: a source selector (UniGetUI or any
/// manager, with a verbose variant), the five log levels, a text filter, and copy / export / reload.
/// The logger exposes no change event, so the UniGetUI log is polled once per second while shown.
/// </summary>
internal sealed class LogsPage : UserControl, ITuiPage
{
    private sealed record LogRow(string Text, IBrush Brush)
    {
        public override string ToString() => Text;
    }

    private static IPackageManager? _requestedManager;

    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly TextBox _filter;
    private readonly DispatcherTimer _timer;
    private readonly List<(string Label, IPackageManager? Manager, bool Verbose)> _sources = [];
    private int _source;
#if DEBUG
    private int _level = 5;
#else
    private int _level = 4;
#endif
    private int _lastCount = -1;
    private List<LogRow> _rows = [];

    public LogsPage()
    {
        _sources.Add((CoreTools.Translate("UniGetUI Log"), null, false));
        foreach (var m in TuiEngine.Managers)
        {
            _sources.Add((m.DisplayName, m, false));
            _sources.Add((m.DisplayName + " (" + CoreTools.Translate("Verbose") + ")", m, true));
        }

        _status = new TextBlock { Foreground = TuiPalette.TextMuted };
        _filter = new TextBox { PlaceholderText = CoreTools.Translate("Filter log lines") };
        _filter.TextChanged += (_, _) => Render(force: true);
        _filter.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Return or Key.Down)
            {
                if (_rows.Count > 0) TuiFocus.SeatListFocus(_list);
                e.Handled = true;
            }
        };
        _filter.AddHandler(TextInputEvent, (_, e) => TuiInputGuard.HandleTextInput(_filter, e, "log filter"), RoutingStrategies.Tunnel);

        _list = new ListBox
        {
            Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<LogRow>((row, _) => new TextBlock
            {
                Text = row?.Text ?? string.Empty,
                Foreground = row?.Brush ?? TuiPalette.Text,
                TextWrapping = TextWrapping.Wrap,
            }, supportsRecycling: false),
        };
        _list.AddHandler(TextInputEvent, OnListTextInput, RoutingStrategies.Tunnel);
        _list.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control)
            {
                _ = CopyAsync();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);

        var root = new DockPanel { LastChildFill = true, Margin = new Thickness(1, 0, 1, 0) };
        var header = new StackPanel();
        header.Children.Add(TuiChrome.PageTitle(CoreTools.Translate("Logs")));
        var filterRow = new DockPanel { LastChildFill = true };
        var label = new TextBlock { Text = "Filter ", Foreground = TuiPalette.Brand, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
        DockPanel.SetDock(label, Dock.Left);
        filterRow.Children.Add(label);
        filterRow.Children.Add(_filter);
        header.Children.Add(filterRow);
        header.Children.Add(_status);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(_list);
        Content = root;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Render(force: false);
    }

    /// <summary>Deep link used by a manager's settings ("View {0} logs").</summary>
    public static void RequestManager(IPackageManager manager) => _requestedManager = manager;

    public IReadOnlyList<TuiKeyHint> KeyHints => TuiChrome.Hints(("l", "Source"), ("v", "Level"), ("c", "Copy"), ("e", "Export"), ("r", "Reload"), ("/", "Filter"));

    public IReadOnlyList<TuiAction> Actions =>
    [
        new(CoreTools.Translate("Choose log") + "…", "l", ChooseSourceAsync),
        new(CoreTools.Translate("Log level") + "…", "v", ChooseLevelAsync, () => _sources[_source].Manager is null),
        new(CoreTools.Translate("Copy to clipboard"), "c / Ctrl+C", CopyAsync),
        new(CoreTools.Translate("Export to a file"), "e", ExportAsync),
        new(CoreTools.Translate("Reload"), "r", () =>
        {
            Render(force: true);
            return Task.CompletedTask;
        }),
    ];

    public bool FocusPrimary() => _rows.Count > 0 ? TuiFocus.SeatListFocus(_list) : _filter.Focus();

    public bool FocusSearch() => _filter.Focus();

    public void Reload() => Render(force: true);

    public void OnShown()
    {
        if (_requestedManager is { } m)
        {
            int index = _sources.FindIndex(s => s.Manager == m && !s.Verbose);
            if (index >= 0) _source = index;
            _requestedManager = null;
        }

        Render(force: true);
    }

    public string CurrentSourceLabel => _sources[_source].Label;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        OnShown();
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    private void OnListTextInput(object? sender, TextInputEventArgs e)
    {
        switch (e.Text)
        {
            case "l": _ = ChooseSourceAsync(); break;
            case "v": _ = ChooseLevelAsync(); break;
            case "c": _ = CopyAsync(); break;
            case "e": _ = ExportAsync(); break;
            case "r": Render(force: true); break;
            case "/": _filter.Focus(); break;
            default: return;
        }

        e.Handled = true;
    }

    private async Task ChooseSourceAsync()
    {
        int? picked = await TuiPrompts.ChooseAsync(CoreTools.Translate("Choose log"), _sources.Select(s => new TuiChoice(s.Label)).ToList(), _source);
        if (picked is int i)
        {
            _source = i;
            Render(force: true);
        }
    }

    private async Task ChooseLevelAsync()
    {
        string[] levels =
        [
            CoreTools.Translate("1 - Errors"), CoreTools.Translate("2 - Warnings"), CoreTools.Translate("3 - Information (less)"),
            CoreTools.Translate("4 - Information (more)"), CoreTools.Translate("5 - information (debug)"),
        ];
        int? picked = await TuiPrompts.ChooseAsync(CoreTools.Translate("Log level"), levels.Select(l => new TuiChoice(l)).ToList(), _level - 1);
        if (picked is int i)
        {
            _level = i + 1;
            Render(force: true);
        }
    }

    private IEnumerable<LogRow> BuildRows()
    {
        var (_, manager, verbose) = _sources[_source];
        if (manager is null)
        {
            foreach (LogEntry entry in Logger.GetLogs())
            {
                if (entry.Content.Length == 0 || ShouldSkip(entry.Severity, _level)) continue;
                yield return new LogRow($"[{entry.Time}] {entry.Content}", SeverityBrush(entry.Severity));
            }

            yield break;
        }

        yield return new LogRow($"Manager {manager.DisplayName} with version:", TuiPalette.Brand);
        yield return new LogRow(manager.Status.Version, TuiPalette.Text);
        foreach (var task in manager.TaskLogger.Operations.ToArray())
        {
            foreach (string line in task.AsColoredString(verbose))
            {
                if (line.Length == 0) continue;
                // The first character is a colour code (0-9) used by the desktop log views.
                char code = line[0];
                string text = char.IsDigit(code) ? line[1..] : line;
                yield return new LogRow(text, code switch
                {
                    '1' => TuiPalette.Text,
                    '2' => TuiPalette.Brand,
                    '3' => TuiPalette.Warning,
                    '4' => TuiPalette.Error,
                    _ => TuiPalette.TextMuted,
                });
            }
        }
    }

    private void Render(bool force)
    {
        int count = _sources[_source].Manager is null ? Logger.GetLogs().Length : -2;
        if (!force && count == _lastCount) return;
        _lastCount = count;

        string filter = (_filter.Text ?? string.Empty).Trim();
        _rows = BuildRows()
            .Where(r => filter.Length == 0 || r.Text.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        bool wasAtEnd = _list.SelectedIndex < 0 || _list.SelectedIndex >= _list.ItemCount - 1;
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        bool listHadFocus = focused is not null && (ReferenceEquals(focused, _list) || _list.IsVisualAncestorOf(focused));
        int selected = _list.SelectedIndex;
        _list.ItemsSource = _rows;
        if (_rows.Count > 0)
        {
            int index = wasAtEnd ? _rows.Count - 1 : Math.Clamp(selected, 0, _rows.Count - 1);
            _list.SelectedIndex = index;
            _list.ScrollIntoView(index);
            // Replacing the items destroyed the focused row: put keyboard focus back on the list.
            if (listHadFocus)
                Dispatcher.UIThread.Post(() => (_list.ContainerFromIndex(index) as Control ?? _list).Focus(), DispatcherPriority.Loaded);
        }
        _status.Text = $"{_sources[_source].Label}  ·  "
                       + (_sources[_source].Manager is null ? CoreTools.Translate("Log level") + $": {_level}  ·  " : "")
                       + CoreTools.Translate("{0} lines", _rows.Count);
    }

    private static bool ShouldSkip(LogEntry.SeverityLevel severity, int level) => level switch
    {
        1 => severity != LogEntry.SeverityLevel.Error,
        2 => severity is LogEntry.SeverityLevel.Debug or LogEntry.SeverityLevel.Info or LogEntry.SeverityLevel.Success,
        3 => severity is LogEntry.SeverityLevel.Debug or LogEntry.SeverityLevel.Info,
        4 => severity == LogEntry.SeverityLevel.Debug,
        _ => false,
    };

    private string AllText()
    {
        var sb = new StringBuilder();
        foreach (var row in _rows) sb.AppendLine(row.Text);
        return sb.ToString();
    }

    private Task CopyAsync() => TuiClipboard.CopyAsync(this, _sources[_source].Label, AllText());

    private async Task ExportAsync()
    {
        string? path = await TuiPrompts.AskTextAsync(CoreTools.Translate("Export to a file"), CoreTools.Translate("Save to file:"),
            Path.Join(TuiPackageActions.DefaultDownloadDirectory(), "UniGetUI log.txt"));
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            string full = path.Trim().Trim('"');
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(full))!);
            await File.WriteAllTextAsync(full, AllText());
            TuiNotifications.Success(CoreTools.Translate("Export to a file"), full);
        }
        catch (Exception ex)
        {
            TuiNotifications.Error(CoreTools.Translate("Export to a file"), ex.Message);
        }
    }

    private static IBrush SeverityBrush(LogEntry.SeverityLevel severity) => severity switch
    {
        LogEntry.SeverityLevel.Error => TuiPalette.Error,
        LogEntry.SeverityLevel.Warning => TuiPalette.Warning,
        LogEntry.SeverityLevel.Success => TuiPalette.Success,
        LogEntry.SeverityLevel.Debug => TuiPalette.TextDim,
        _ => TuiPalette.Text,
    };
}
