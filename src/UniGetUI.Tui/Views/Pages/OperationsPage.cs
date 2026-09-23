using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
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
    private sealed record QueueRow(AbstractOperation Operation, string State, string Title, string Status)
    {
        public override string ToString() => Title;
    }

    private sealed record LogRow(string Text, AbstractOperation.LineType Type)
    {
        public override string ToString() => Text;
    }

    private readonly DataGrid _queue;
    private readonly ListBox _log;
    private readonly TextBlock _status;
    private readonly TextBlock _logHeader;

    private AbstractOperation? _selected;
    private bool _logDirty;

    public OperationsPage()
    {
        _status = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.Parse("#C0C0C0")), Margin = new Thickness(0, 0, 0, 1),
        };

        _queue = BuildQueueGrid();
        _queue.SelectionChanged += OnQueueSelectionChanged;
        _queue.KeyDown += OnQueueKeyDown;
        // Action letters (c/x) would otherwise feed DataGrid text search/editing paths. Suppress them
        // on the tunnel so they only drive operations.
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
        header.Children.Add(TuiChrome.PageTitle(">", "Operations"));
        header.Children.Add(TuiChrome.Separator());
        header.Children.Add(_status);

        var queuePane = new DockPanel { LastChildFill = true, Width = 44 };
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

    public bool FocusPrimary() => TuiFocus.SeatDataGridFocus(_queue);

    private static DataGrid BuildQueueGrid()
    {
        var grid = new DataGrid
        {
            AutoGenerateColumns = false,
            Background = Brushes.Transparent,
            CanUserSortColumns = true,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            IsReadOnly = true,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };

        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "State",
            Binding = new Binding(nameof(QueueRow.State)),
            Width = new DataGridLength(7, DataGridLengthUnitType.Pixel),
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Operation",
            Binding = new Binding(nameof(QueueRow.Title)),
            Width = new DataGridLength(23, DataGridLengthUnitType.Pixel),
        });
        grid.Columns.Add(new DataGridTextColumn
        {
            Header = "Status",
            Binding = new Binding(nameof(QueueRow.Status)),
            Width = new DataGridLength(10, DataGridLengthUnitType.Pixel),
        });

        return grid;
    }

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

        var rows = ops.Select(BuildQueueRow).ToList();
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

    // Menu-bar entry points: same actions as the queue hotkeys, with managed confirmation dialogs.
    internal void MenuCancelSelected() => _ = CancelSelectedAsync();
    internal void MenuClearFinished() => _ = ClearFinishedAsync();
    internal void MenuCancelAll() => _ = CancelAllAsync();
    internal void MenuCopyOutput() => _ = CopySelectedOutputAsync();

    // GetOutput() exposes the operation's live log list, which background producers append to while
    // the UI renders; copy it defensively and retry the rare torn enumeration instead of crashing.
    private static List<(string, AbstractOperation.LineType)> SnapshotOutput(AbstractOperation op)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return op.GetOutput().ToList();
            }
            catch (ArgumentException)
            {
                /* list resized mid-copy */
            }
            catch (InvalidOperationException)
            {
                /* collection modified during enumeration */
            }
        }

        return new();
    }

    private void OnQueueKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            _ = CopySelectedOutputAsync();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.C:
                _ = CancelSelectedAsync();
                e.Handled = true;
                break;
            case Key.X:
                _ = ClearFinishedAsync();
                e.Handled = true;
                break;
        }
    }

    private void OnQueueTextInput(object? sender, TextInputEventArgs e)
    {
        if (e.Text is "c" or "C" or "x" or "X") e.Handled = true;
    }

    private async Task CancelSelectedAsync()
    {
        if (_selected is null)
        {
            _status.Text = "No operation selected.";
            return;
        }

        string title = _selected.Metadata.Title.Length > 0 ? _selected.Metadata.Title : "selected operation";
        if (!await TuiDialogs.ConfirmAsync(this, "Cancel operation", $"Cancel {title}?"))
        {
            _status.Text = "Cancel operation aborted.";
            return;
        }

        _selected.Cancel();
        TuiNotifications.Warning("Cancel requested", title);
    }

    private async Task CopySelectedOutputAsync()
    {
        if (_selected is null)
        {
            _status.Text = "No operation selected to copy.";
            TuiNotifications.Warning("Nothing to copy", "Select an operation first.");
            return;
        }

        var lines = SnapshotOutput(_selected)
            .Where(l => l.Item2 is not AbstractOperation.LineType.ProgressIndicator)
            .Select(l => l.Item1)
            .ToArray();
        string title = _selected.Metadata.Title.Length > 0 ? _selected.Metadata.Title : "operation output";
        if (await TuiClipboard.CopyAsync(this, title, string.Join(Environment.NewLine, lines)))
            _status.Text = $"Copied output for {title}.";
    }

    private async Task ClearFinishedAsync()
    {
        if (!TuiOperationRegistry.Snapshot()
                .Any(op => op.Status is not OperationStatus.Running and not OperationStatus.InQueue))
        {
            _status.Text = "No finished operations to clear.";
            return;
        }

        if (!await TuiDialogs.ConfirmAsync(this, "Clear finished operations",
                "Remove all finished operations from the queue?"))
        {
            _status.Text = "Clear finished operations canceled.";
            return;
        }

        TuiOperationRegistry.ClearFinished();
        TuiNotifications.Success("Finished operations cleared", "Queue now only contains active operations.");
    }

    private async Task CancelAllAsync()
    {
        int active = TuiOperationRegistry.Snapshot()
            .Count(op => op.Status is OperationStatus.Running or OperationStatus.InQueue);
        if (active == 0)
        {
            _status.Text = "No running or queued operations to cancel.";
            return;
        }

        if (!await TuiDialogs.ConfirmAsync(this, "Cancel all operations",
                $"Cancel {active} running/queued operation(s)?"))
        {
            _status.Text = "Cancel all operations aborted.";
            return;
        }

        TuiOperationRegistry.CancelAll();
        TuiNotifications.Warning("Cancel requested", $"{active} operation(s)");
    }

    private static QueueRow BuildQueueRow(AbstractOperation op)
    {
        string state = op.Status switch
        {
            OperationStatus.Running => "▶",
            OperationStatus.InQueue => "…",
            OperationStatus.Succeeded => "✓",
            OperationStatus.Failed => "✗",
            OperationStatus.Canceled => "⊘",
            _ => " ",
        };

        string title = op.Metadata.Title.Length > 0 ? op.Metadata.Title : "Operation";
        if (title.Length > 28)
            title = title[..27] + "…";

        return new QueueRow(op, state, title, op.Status.ToString());
    }
}
