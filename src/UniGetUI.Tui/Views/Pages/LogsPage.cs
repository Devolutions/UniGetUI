using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using UniGetUI.Core.Logging;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Theme;

namespace UniGetUI.Tui.Views.Pages;

/// <summary>
/// A live view of the in-process UniGetUI log (<see cref="Logger.GetLogs"/>). The logger exposes no change
/// event, so the page polls on a one-second <see cref="DispatcherTimer"/> and only re-renders when the entry
/// count grows. Entries are colored by severity; Debug entries can be hidden with <c>d</c>.
/// </summary>
internal sealed class LogsPage : UserControl, IFocusablePage
{
    private sealed record LogRow(string Text, LogEntry.SeverityLevel Severity)
    {
        public override string ToString() => Text;
    }

    private readonly ListBox _list;
    private readonly TextBlock _status;
    private readonly TextBox _filter;
    private readonly DispatcherTimer _timer;

    private int _lastCount = -1;
    private bool _showDebug;

    // Log retrieval runs off the UI thread; these coordinate a single in-flight fetch and coalesce
    // overlapping requests on the dispatcher (no locking needed — all access is on the UI thread).
    private bool _attached;
    private bool _refreshRunning;
    private bool _refreshQueued;
    private bool _pendingForce;

    public LogsPage()
    {
        _status = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#C0C0C0")), Margin = new Thickness(0, 0, 0, 1),
        };

        _filter = new TextBox { Watermark = "type to filter log lines  (Enter/↓ to list)" };
        _filter.TextChanged += (_, _) => RequestRefresh(force: true);
        _filter.KeyDown += OnFilterKeyDown;

        _list = new ListBox
        {
            Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<LogRow>(
                (row, _) => new TextBlock
                {
                    Text = row?.Text ?? string.Empty,
                    Foreground = SeverityBrush(row?.Severity ?? LogEntry.SeverityLevel.Info),
                },
                supportsRecycling: true),
        };
        _list.KeyDown += OnListKeyDown;
        // Suppress ListBox type-ahead for the 'd' action letter so it toggles debug instead of jumping.
        _list.AddHandler(TextInputEvent, OnListTextInput, RoutingStrategies.Tunnel);

        var root = new DockPanel { LastChildFill = true, Margin = new Thickness(2, 1, 2, 1) };
        var header = new StackPanel { Spacing = 0 };
        header.Children.Add(new TextBlock
        {
            Text = "Logs",
            Foreground = DevolutionsPalette.BrandBrush,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 1),
        });
        var filterRow = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 1) };
        var filterLabel = new TextBlock
        {
            Text = "Filter ",
            Foreground = DevolutionsPalette.BrandBrush,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
        DockPanel.SetDock(filterLabel, Dock.Left);
        filterRow.Children.Add(filterLabel);
        filterRow.Children.Add(_filter);
        header.Children.Add(filterRow);
        header.Children.Add(_status);
        DockPanel.SetDock(header, Dock.Top);

        var hint = new TextBlock
        {
            Text = "d toggle debug entries   ·   live (refreshes every second)",
            Foreground = new SolidColorBrush(Color.Parse("#C0C0C0")),
            Margin = new Thickness(0, 1, 0, 0),
        };
        DockPanel.SetDock(hint, Dock.Bottom);

        root.Children.Add(header);
        root.Children.Add(hint);
        root.Children.Add(_list);
        Content = root;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => RequestRefresh();
    }

    public bool FocusPrimary() => _filter.Focus();

    private void OnFilterKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Enter or Key.Return or Key.Down)
        {
            TuiFocus.SeatListFocus(_list);
            e.Handled = true;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _lastCount = -1;
        RequestRefresh(force: true);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        _timer.Stop();
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.D)
        {
            _showDebug = !_showDebug;
            e.Handled = true;
            RequestRefresh(force: true);
        }
    }

    private void OnListTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text is "d" or "D") e.Handled = true;
    }

    // UI thread. Records a refresh request; if a fetch is already running, mark it queued (preserving
    // any forced flag) so a fresh fetch runs once the current one completes.
    private void RequestRefresh(bool force = false)
    {
        if (force) _pendingForce = true;
        if (_refreshRunning)
        {
            _refreshQueued = true;
            return;
        }

        StartRefresh();
    }

    private void StartRefresh()
    {
        _refreshRunning = true;
        bool force = _pendingForce;
        _pendingForce = false;
        _ = RunRefreshAsync(force);
    }

    // Logger.GetLogs() can block under logger lock contention while operation threads log heavily.
    // Pull it off the dispatcher; the await resumes on the UI thread (captured sync context) so the
    // render and the in-flight bookkeeping below stay single-threaded.
    private async Task RunRefreshAsync(bool force)
    {
        LogEntry[] logs;
        try
        {
            logs = await Task.Run(Logger.GetLogs);
        }
        catch (Exception ex)
        {
            Logger.Error(ex);
            FinishRefresh();
            return;
        }

        if (_attached) ApplyLogs(logs, force);
        FinishRefresh();
    }

    private void FinishRefresh()
    {
        _refreshRunning = false;
        if (!_refreshQueued || !_attached)
        {
            _refreshQueued = false;
            return;
        }

        _refreshQueued = false;
        StartRefresh();
    }

    private void ApplyLogs(LogEntry[] logs, bool force)
    {
        if (!force && logs.Length == _lastCount) return;
        _lastCount = logs.Length;

        string filter = (_filter.Text ?? string.Empty).Trim();
        var rows = logs
            .Where(l => _showDebug || l.Severity != LogEntry.SeverityLevel.Debug)
            .Where(l => filter.Length == 0
                        || (l.Content?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false))
            .Select(l => new LogRow($"{l.Time:HH:mm:ss}  {l.Content}", l.Severity))
            .ToList();

        _list.ItemsSource = rows;
        _status.Text = $"{rows.Count} entr{(rows.Count == 1 ? "y" : "ies")} shown"
                       + (filter.Length > 0 ? "  ·  filtered" : string.Empty)
                       + (_showDebug ? "  ·  debug visible" : "  ·  debug hidden")
                       + $"  ·  session log: {Logger.GetSessionLogPath()}";

        if (rows.Count > 0)
            _list.ScrollIntoView(rows[^1]);
    }

    private static IBrush SeverityBrush(LogEntry.SeverityLevel s) => s switch
    {
        LogEntry.SeverityLevel.Error => DevolutionsPalette.ErrorTextBrush,
        LogEntry.SeverityLevel.Warning => new SolidColorBrush(Color.Parse("#FFB454")),
        LogEntry.SeverityLevel.Success => DevolutionsPalette.SuccessTextBrush,
        LogEntry.SeverityLevel.Debug => new SolidColorBrush(Color.Parse("#BBBBBB")),
        _ => new SolidColorBrush(Color.Parse("#BBBBBB")),
    };
}
