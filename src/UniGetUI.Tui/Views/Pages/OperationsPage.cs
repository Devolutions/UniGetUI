using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageOperations;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Theme;

namespace UniGetUI.Tui.Views.Pages;

/// <summary>
/// The Operations page: a live queue of install/update/uninstall operations on the left and a
/// scrollable output log for the selected operation on the right. It reflects the shared engine's
/// operation pipeline through <see cref="TuiOperationRegistry"/>; all engine events are marshalled to
/// the UI thread before touching controls.
/// </summary>
internal sealed class OperationsPage : UserControl, IFocusablePage
{
    private sealed record QueueRow(string Text, AbstractOperation Operation)
    {
        public override string ToString() => Text;
    }

    private sealed record LogRow(string Text, AbstractOperation.LineType Type)
    {
        public override string ToString() => Text;
    }

    private readonly ListBox _queue;
    private readonly ListBox _log;
    private readonly TextBlock _status;
    private readonly TextBlock _logHeader;

    private AbstractOperation? _selected;
    private bool _logDirty;

    public OperationsPage()
    {
        _status = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#C0C0C0")),
            Margin = new Thickness(0, 0, 0, 1),
        };

        _queue = new ListBox
        {
            Background = Brushes.Transparent,
            Width = 34,
            ItemTemplate = new FuncDataTemplate<QueueRow>(
                (row, _) => new TextBlock { Text = row?.Text ?? string.Empty },
                supportsRecycling: true),
        };
        _queue.SelectionChanged += OnQueueSelectionChanged;
        _queue.KeyDown += OnQueueKeyDown;
        // Action letters (c/x) would otherwise trigger the ListBox's built-in type-ahead search (which
        // runs off TextInput). Suppress them on the tunnel so they only drive operations.
        _queue.AddHandler(TextInputEvent, OnQueueTextInput, RoutingStrategies.Tunnel);

        _logHeader = new TextBlock
        {
            Text = "Output",
            Foreground = DevolutionsPalette.BrandBrush,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 1),
        };

        _log = new ListBox
        {
            Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<LogRow>(
                (row, _) => new TextBlock
                {
                    Text = row?.Text ?? string.Empty,
                    Foreground = row?.Type == AbstractOperation.LineType.Error
                        ? DevolutionsPalette.ErrorTextBrush
                        : new SolidColorBrush(Color.Parse("#BBBBBB")),
                },
                supportsRecycling: true),
        };

        Content = BuildLayout();
    }

    private Control BuildLayout()
    {
        var header = new StackPanel { Spacing = 0 };
        header.Children.Add(new TextBlock
        {
            Text = "Operations",
            Foreground = DevolutionsPalette.BrandBrush,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 1),
        });
        header.Children.Add(_status);

        var queuePane = new DockPanel { LastChildFill = true, Width = 34 };
        var queueHeader = new TextBlock
        {
            Text = "Queue",
            Foreground = DevolutionsPalette.BrandBrush,
            FontWeight = FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 1),
        };
        DockPanel.SetDock(queueHeader, Dock.Top);
        queuePane.Children.Add(queueHeader);
        queuePane.Children.Add(_queue);

        var logPane = new DockPanel { LastChildFill = true, Margin = new Thickness(2, 0, 0, 0) };
        DockPanel.SetDock(_logHeader, Dock.Top);
        logPane.Children.Add(_logHeader);
        logPane.Children.Add(_log);

        var body = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(queuePane, Dock.Left);
        body.Children.Add(queuePane);
        body.Children.Add(logPane);

        var hint = new TextBlock
        {
            Text = "c cancel selected   ·   x clear finished",
            Foreground = new SolidColorBrush(Color.Parse("#C0C0C0")),
            Margin = new Thickness(0, 1, 0, 0),
        };

        var root = new DockPanel { LastChildFill = true, Margin = new Thickness(2, 1, 2, 1) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(hint, Dock.Bottom);
        root.Children.Add(header);
        root.Children.Add(hint);
        root.Children.Add(body);
        return root;
    }

    public bool FocusPrimary() => TuiFocus.SeatListFocus(_queue);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        TuiOperationRegistry.Changed += OnRegistryChanged;
        RebuildQueue();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        TuiOperationRegistry.Changed -= OnRegistryChanged;
        SelectOperation(null);
    }

    private void OnRegistryChanged() => RebuildQueue();

    private void RebuildQueue()
    {
        var ops = TuiOperationRegistry.Snapshot();

        // Preserve the current selection across rebuilds by operation reference.
        var previouslySelected = (_queue.SelectedItem as QueueRow)?.Operation ?? _selected;

        var rows = ops.Select(op => new QueueRow(QueueRowText(op), op)).ToList();
        _queue.ItemsSource = rows;

        int running = ops.Count(o => o.Status is OperationStatus.Running);
        int queued = ops.Count(o => o.Status is OperationStatus.InQueue);
        _status.Text = ops.Count == 0
            ? "No operations yet. Select a package and press i / u to enqueue one."
            : $"{ops.Count} operation(s)  ·  {running} running  ·  {queued} queued";

        if (rows.Count == 0)
        {
            SelectOperation(null);
            return;
        }

        var match = rows.FirstOrDefault(r => ReferenceEquals(r.Operation, previouslySelected));
        if (match is not null)
        {
            _queue.SelectedItem = match;
            // Same operation still selected: its status/log may have advanced, so refresh the log.
            RenderLog();
        }
        else
        {
            _queue.SelectedItem = rows[^1];
        }
    }

    private void OnQueueSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        SelectOperation((_queue.SelectedItem as QueueRow)?.Operation);
    }

    private void SelectOperation(AbstractOperation? op)
    {
        if (ReferenceEquals(op, _selected))
        {
            if (op is not null) RenderLog();
            return;
        }

        if (_selected is not null)
            _selected.LogLineAdded -= OnSelectedLogLineAdded;

        _selected = op;

        if (_selected is not null)
            _selected.LogLineAdded += OnSelectedLogLineAdded;

        RenderLog();
    }

    // LogLineAdded fires on a background thread; coalesce and re-render on the UI thread.
    private void OnSelectedLogLineAdded(object? sender, (string, AbstractOperation.LineType) line)
    {
        if (_logDirty) return;
        _logDirty = true;
        Dispatcher.UIThread.Post(() =>
        {
            _logDirty = false;
            RenderLog();
        }, DispatcherPriority.Background);
    }

    private void RenderLog()
    {
        if (_selected is null)
        {
            _logHeader.Text = "Output";
            _log.ItemsSource = Array.Empty<LogRow>();
            return;
        }

        _logHeader.Text = _selected.Metadata.Title.Length > 0
            ? _selected.Metadata.Title
            : "Output";

        // GetOutput() is the source of truth (it excludes transient progress lines), so rebuilding from
        // it on every change is both correct and free of snapshot/live-subscription duplication.
        var rows = SnapshotOutput(_selected)
            .Where(l => l.Item2 is not AbstractOperation.LineType.ProgressIndicator)
            .Select(l => new LogRow(l.Item1, l.Item2))
            .ToList();

        _log.ItemsSource = rows;
        if (rows.Count > 0)
            _log.ScrollIntoView(rows[^1]);
    }

    // Menu-bar entry point: cancel the currently selected operation (same as the 'c' hotkey).
    internal void MenuCancelSelected() => _selected?.Cancel();

    // GetOutput() exposes the operation's live log list, which background producers append to while
    // the UI renders; copy it defensively and retry the rare torn enumeration instead of crashing.
    private static List<(string, AbstractOperation.LineType)> SnapshotOutput(AbstractOperation op)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { return op.GetOutput().ToList(); }
            catch (ArgumentException) { /* list resized mid-copy */ }
            catch (InvalidOperationException) { /* collection modified during enumeration */ }
        }
        return new();
    }

    private void OnQueueKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.C:
                _selected?.Cancel();
                e.Handled = true;
                break;
            case Key.X:
                TuiOperationRegistry.ClearFinished();
                e.Handled = true;
                break;
        }
    }

    private void OnQueueTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text is "c" or "C" or "x" or "X") e.Handled = true;
    }

    private static string QueueRowText(AbstractOperation op)
    {
        string glyph = op.Status switch
        {
            OperationStatus.Running => "▶",
            OperationStatus.InQueue => "…",
            OperationStatus.Succeeded => "✓",
            OperationStatus.Failed => "✗",
            OperationStatus.Canceled => "⊘",
            _ => " ",
        };

        string title = op.Metadata.Title.Length > 0 ? op.Metadata.Title : "Operation";
        if (title.Length > 30)
            title = title[..29] + "…";

        return $"{glyph} {title}";
    }
}
