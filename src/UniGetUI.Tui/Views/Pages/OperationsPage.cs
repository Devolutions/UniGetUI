using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Operations;
using UniGetUI.PackageOperations;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Theme;
using UniGetUI.Tui.Views.Dialogs;

namespace UniGetUI.Tui.Views.Pages;

/// <summary>
/// The operations queue (desktop operations panel): every tracked operation with its live status and
/// progress on the left, the selected operation's output on the right, and the desktop per-operation
/// menu (cancel, retry variants, queue position, package details/options) plus the bulk actions
/// (retry failed, clear successful/finished, cancel all).
/// </summary>
internal sealed class OperationsPage : UserControl, ITuiPage
{
    internal sealed record QueueRow(AbstractOperation Operation, string State, string Title, string Status)
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
    private List<QueueRow> _rows = [];

    public OperationsPage()
    {
        _status = new TextBlock { Foreground = TuiPalette.TextMuted };
        _queue = new DataGrid
        {
            MinColumnWidth = 3,
            AutoGenerateColumns = false,
            Background = Brushes.Transparent,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            IsReadOnly = true,
            SelectionMode = DataGridSelectionMode.Single,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _queue.Columns.Add(new DataGridTextColumn { Header = "", Binding = CompiledBinding.Create<QueueRow, string>(r => r.State), Width = new DataGridLength(3) });
        _queue.Columns.Add(new DataGridTextColumn { Header = CoreTools.Translate("Operation"), Binding = CompiledBinding.Create<QueueRow, string>(r => r.Title), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _queue.Columns.Add(new DataGridTextColumn { Header = CoreTools.Translate("Status"), Binding = CompiledBinding.Create<QueueRow, string>(r => r.Status), Width = new DataGridLength(16) });
        _queue.SelectionChanged += (_, _) => SelectOperation((_queue.SelectedItem as QueueRow)?.Operation);
        _queue.AddHandler(KeyDownEvent, OnQueueKeyDown, RoutingStrategies.Tunnel);
        _queue.AddHandler(TextInputEvent, OnQueueTextInput, RoutingStrategies.Tunnel);

        _logHeader = new TextBlock { Text = CoreTools.Translate("Output"), Foreground = TuiPalette.Brand, FontWeight = FontWeight.Bold };
        _log = new ListBox
        {
            Background = Brushes.Transparent,
            ItemTemplate = new FuncDataTemplate<LogRow>((row, _) => new TextBlock
            {
                Text = row?.Text ?? string.Empty,
                TextWrapping = TextWrapping.Wrap,
                Foreground = row?.Type == AbstractOperation.LineType.Error
                    ? TuiPalette.Error
                    : row?.Type == AbstractOperation.LineType.VerboseDetails
                        ? TuiPalette.TextDim
                        : TuiPalette.Text,
            }, supportsRecycling: false),
        };

        var header = new StackPanel();
        header.Children.Add(TuiChrome.PageTitle(CoreTools.Translate("Operations"), _status));

        var queuePane = new DockPanel { LastChildFill = true, Width = 58 };
        queuePane.Children.Add(_queue);
        var logPane = new DockPanel { LastChildFill = true, Margin = new Thickness(1, 0, 0, 0) };
        DockPanel.SetDock(_logHeader, Dock.Top);
        logPane.Children.Add(_logHeader);
        logPane.Children.Add(_log);
        var body = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(queuePane, Dock.Left);
        body.Children.Add(queuePane);
        body.Children.Add(logPane);

        var root = new DockPanel { LastChildFill = true, Margin = new Thickness(1, 0, 1, 0) };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(body);
        Content = root;
    }

    public IReadOnlyList<TuiKeyHint> KeyHints => TuiChrome.Hints(("Enter", "Output"), ("m", "Actions"), ("c", "Cancel"), ("r", "Retry"), ("Del", "Remove"), ("x", "Clear finished"), ("s", "Clear successful"), ("R", "Retry failed"), ("K", "Cancel all"));

    public IReadOnlyList<QueueRow> Rows => _rows;

    public AbstractOperation? SelectedOperation => _selected;

    public IReadOnlyList<TuiAction> Actions
    {
        get
        {
            AbstractOperation? op = _selected;
            bool active = op?.Status is OperationStatus.Running or OperationStatus.InQueue;
            bool queued = op?.Status is OperationStatus.InQueue;
            bool finished = op is not null && !active;
            bool failed = op?.Status is OperationStatus.Failed;
            var pkgOp = op as PackageOperation;
            var list = new List<TuiAction>
            {
                new(CoreTools.Translate("Show output"), "Enter", ShowOutputAsync, () => op is not null),
                new(CoreTools.Translate("Cancel"), "c", CancelSelectedAsync, () => active),
                new(CoreTools.Translate("Close"), "Del", () =>
                {
                    RemoveSelected();
                    return Task.CompletedTask;
                }, () => finished),
                TuiAction.Separator,
                new(CoreTools.Translate("Retry"), "r", () => RetryAsync(AbstractOperation.RetryMode.Retry), () => finished),
                new(CoreTools.Translate("Retry as administrator"), null, () => RetryAsync(AbstractOperation.RetryMode.Retry_AsAdmin),
                    () => finished && (pkgOp?.Package.Manager.Capabilities.CanRunAsAdmin == true || op is SourceOperation)),
                new(CoreTools.Translate("Retry interactively"), null, () => RetryAsync(AbstractOperation.RetryMode.Retry_Interactive),
                    () => finished && pkgOp?.Package.Manager.Capabilities.CanRunInteractively == true),
                new(CoreTools.Translate("Retry skipping integrity checks"), null, () => RetryAsync(AbstractOperation.RetryMode.Retry_SkipIntegrity),
                    () => finished && pkgOp is not null && pkgOp.Package.Manager.Capabilities.CanSkipIntegrityChecks),
                TuiAction.Separator,
                new(CoreTools.Translate("Run now"), null, () => Queue(o => o.SkipQueue()), () => queued),
                new(CoreTools.Translate("Run next"), null, () => Queue(o => o.RunNext()), () => queued),
                new(CoreTools.Translate("Run last"), null, () => Queue(o => o.BackOfTheQueue()), () => queued),
                TuiAction.Separator,
                new(CoreTools.Translate("Package details"), null, () => PackageDetailsDialog.ShowAsync(pkgOp!.Package, OperationType.None), () => pkgOp is not null),
                new(CoreTools.Translate("Installation options"), null, () => InstallOptionsDialog.ShowForPackageAsync(pkgOp!.Package, OperationType.Install), () => pkgOp is not null && !pkgOp.Package.Source.IsVirtualManager),
                new(CoreTools.Translate("Open install location"), null, OpenInstallLocationAsync, () => pkgOp is not null),
                new(CoreTools.Translate("Show in explorer"), null, OpenDownloadAsync, () => op is DownloadOperation { Status: OperationStatus.Succeeded }),
                new(CoreTools.Translate("Copy output"), "Ctrl+C", CopyOutputAsync, () => op is not null),
                TuiAction.Separator,
                new(CoreTools.Translate("Retry failed operations"), "R", () =>
                {
                    TuiOperationRegistry.RetryFailed();
                    return Task.CompletedTask;
                }, () => _rows.Any(r => r.Operation.Status is OperationStatus.Failed)),
                new(CoreTools.Translate("Clear successful operations"), "s", () =>
                {
                    TuiOperationRegistry.ClearSuccessful();
                    return Task.CompletedTask;
                }, () => _rows.Any(r => r.Operation.Status is OperationStatus.Succeeded)),
                new(CoreTools.Translate("Clear finished operations"), "x", () =>
                {
                    TuiOperationRegistry.ClearFinished();
                    return Task.CompletedTask;
                }, () => _rows.Any(r => r.Operation.Status is not (OperationStatus.Running or OperationStatus.InQueue))),
                new(CoreTools.Translate("Cancel all operations"), "K", CancelAllAsync, () => TuiOperationRegistry.ActiveCount > 0),
            };
            return list;
        }
    }

    public bool FocusPrimary() => _rows.Count > 0 && TuiFocus.SeatDataGridFocus(_queue);

    public void OnShown() => RebuildQueue();

    public void Reload() => RebuildQueue();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        TuiOperationRegistry.Changed += RebuildQueue;
        RebuildQueue();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        TuiOperationRegistry.Changed -= RebuildQueue;
    }

    private void RebuildQueue()
    {
        var ops = TuiOperationRegistry.Snapshot();
        AbstractOperation? previous = _selected;
        _rows = ops.Select(BuildRow).ToList();
        _queue.ItemsSource = _rows;
        int running = ops.Count(o => o.Status is OperationStatus.Running);
        int queued = ops.Count(o => o.Status is OperationStatus.InQueue);
        int failed = ops.Count(o => o.Status is OperationStatus.Failed);
        _status.Text = ops.Count == 0
            ? CoreTools.Translate("No operations are running. Select packages and install, update or uninstall them to add operations here.")
            : CoreTools.Translate("{0} operations", ops.Count) + $"  ·  {running} " + CoreTools.Translate("running")
              + $"  ·  {queued} " + CoreTools.Translate("queued") + $"  ·  {failed} " + CoreTools.Translate("failed")
              + $"  ·  {CoreTools.Translate("Parallel operations")}: {AbstractOperation.MAX_OPERATIONS}";

        if (_rows.Count == 0)
        {
            SelectOperation(null);
            return;
        }

        var match = _rows.FirstOrDefault(r => ReferenceEquals(r.Operation, previous)) ?? _rows[^1];
        _queue.SelectedItem = match;
        SelectOperation(match.Operation);
        RenderLog();
    }

    private static QueueRow BuildRow(AbstractOperation op)
    {
        string state = op.Status switch
        {
            OperationStatus.Running => ">",
            OperationStatus.InQueue => "…",
            OperationStatus.Succeeded => "✓",
            OperationStatus.Failed => "✗",
            OperationStatus.Canceled => "-",
            _ => " ",
        };
        string status = op.Status switch
        {
            OperationStatus.Running when ProgressText(op) is { Length: > 0 } p => p,
            OperationStatus.Running => CoreTools.Translate("Running"),
            OperationStatus.InQueue => CoreTools.Translate("Queued"),
            OperationStatus.Succeeded => CoreTools.Translate("Succeeded"),
            OperationStatus.Failed => CoreTools.Translate("Failed"),
            OperationStatus.Canceled => CoreTools.Translate("Canceled"),
            _ => op.Status.ToString(),
        };
        string badges = string.Concat(
            op is PackageOperation { Options.RunAsAdministrator: true } ? " [admin]" : "",
            op is PackageOperation { Options.InteractiveInstallation: true } ? " [interactive]" : "",
            op is PackageOperation { Options.SkipHashCheck: true } ? " [skip hash]" : "");
        string title = (op.Metadata.Title.Length > 0 ? op.Metadata.Title : CoreTools.Translate("Operation")) + badges;
        return new QueueRow(op, state, title, status);
    }

    private static string ProgressText(AbstractOperation op)
    {
        try
        {
            var line = SnapshotOutput(op).LastOrDefault(l => l.Item2 is AbstractOperation.LineType.ProgressIndicator);
            string text = line.Item1 ?? "";
            int pct = text.IndexOf('%');
            if (pct > 0)
            {
                int start = pct - 1;
                while (start > 0 && char.IsDigit(text[start - 1])) start--;
                return text[start..(pct + 1)];
            }
        }
        catch
        {
            // best effort
        }

        return "";
    }

    private void SelectOperation(AbstractOperation? op)
    {
        if (ReferenceEquals(op, _selected)) return;
        _selected?.LogLineAdded -= OnSelectedLogLineAdded;
        _selected = op;
        _selected?.LogLineAdded += OnSelectedLogLineAdded;
        RenderLog();
    }

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
            _logHeader.Text = CoreTools.Translate("Output");
            _log.ItemsSource = Array.Empty<LogRow>();
            return;
        }

        _logHeader.Text = _selected.Metadata.Title.Length > 0 ? _selected.Metadata.Title : CoreTools.Translate("Output");
        var rows = SnapshotOutput(_selected)
            .Where(l => l.Item2 is not AbstractOperation.LineType.ProgressIndicator)
            .Select(l => new LogRow(l.Item1, l.Item2))
            .ToList();
        _log.ItemsSource = rows;
        if (rows.Count > 0)
        {
            _log.ScrollIntoView(rows[^1]);
            // Scroll again once the new items are laid out, so the newest line is really visible.
            Dispatcher.UIThread.Post(() => _log.ScrollIntoView(rows[^1]), DispatcherPriority.Background);
        }
    }

    internal static List<(string, AbstractOperation.LineType)> SnapshotOutput(AbstractOperation op)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return op.GetOutput().ToList();
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }

        return [];
    }

    private void OnQueueKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && e.KeyModifiers == KeyModifiers.Control)
        {
            _ = CopyOutputAsync();
            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Enter or Key.Return)
        {
            _ = ShowOutputAsync();
            e.Handled = true;
        }
        else if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Delete)
        {
            RemoveSelected();
            e.Handled = true;
        }
    }

    private void OnQueueTextInput(object? sender, TextInputEventArgs e)
    {
        switch (e.Text)
        {
            case "c": _ = CancelSelectedAsync(); break;
            case "r": _ = RetryAsync(AbstractOperation.RetryMode.Retry); break;
            case "x": TuiOperationRegistry.ClearFinished(); break;
            case "s": TuiOperationRegistry.ClearSuccessful(); break;
            case "R": TuiOperationRegistry.RetryFailed(); break;
            case "K": _ = CancelAllAsync(); break;
            case "m": _ = ShowMenuAsync(); break;
        }

        e.Handled = true;
    }

    private async Task ShowMenuAsync()
    {
        var actions = Actions;
        int? picked = await TuiPrompts.ChooseAsync(_selected?.Metadata.Title ?? CoreTools.Translate("Operations"),
            actions.Select(a => a.IsSeparator ? TuiChoice.Separator : new TuiChoice(a.Label, a.IsEnabled, a.Shortcut)).ToList());
        if (picked is int i) await actions[i].Run();
    }

    private async Task ShowOutputAsync()
    {
        if (_selected is null) return;
        string text = string.Join(Environment.NewLine, SnapshotOutput(_selected)
            .Where(l => l.Item2 is not AbstractOperation.LineType.ProgressIndicator)
            .Select(l => l.Item1));
        await TuiPrompts.ShowTextAsync(_selected.Metadata.Title, text, scrollToEnd: true);
    }

    private Task CopyOutputAsync()
    {
        if (_selected is null) return Task.CompletedTask;
        string text = string.Join(Environment.NewLine, SnapshotOutput(_selected)
            .Where(l => l.Item2 is not AbstractOperation.LineType.ProgressIndicator)
            .Select(l => l.Item1));
        return TuiClipboard.CopyAsync(this, _selected.Metadata.Title, text);
    }

    private async Task CancelSelectedAsync()
    {
        if (_selected is not { Status: OperationStatus.Running or OperationStatus.InQueue } op) return;
        if (!await TuiPrompts.ConfirmAsync(CoreTools.Translate("Cancel"), CoreTools.Translate("Do you really want to cancel {0}?", op.Metadata.Title)))
            return;
        op.Cancel();
    }

    private async Task CancelAllAsync()
    {
        int active = TuiOperationRegistry.ActiveCount;
        if (active == 0) return;
        if (!await TuiPrompts.ConfirmAsync(CoreTools.Translate("Cancel all operations"),
                CoreTools.Translate("Do you really want to cancel {0} running or queued operations?", active)))
            return;
        TuiOperationRegistry.CancelAll();
    }

    private void RemoveSelected()
    {
        if (_selected is { Status: not (OperationStatus.Running or OperationStatus.InQueue) } op)
            TuiOperationRegistry.Remove(op);
    }

    private Task RetryAsync(string mode)
    {
        if (_selected is { Status: not (OperationStatus.Running or OperationStatus.InQueue) } op)
            op.Retry(mode);
        return Task.CompletedTask;
    }

    private Task Queue(Action<AbstractOperation> action)
    {
        if (_selected is not null) action(_selected);
        RebuildQueue();
        return Task.CompletedTask;
    }

    private async Task OpenInstallLocationAsync()
    {
        if (_selected is not PackageOperation pkgOp) return;
        string? path = await Task.Run(() => TuiPackageActions.InstallLocation(pkgOp.Package));
        await TuiPrompts.ShowTextAsync(CoreTools.Translate("Open install location"),
            path ?? CoreTools.Translate("The install location of this package is unknown."),
            path is null ? null : [(CoreTools.Translate("Open"), () => TuiPackageActions.OpenExternally(path))]);
    }

    private async Task OpenDownloadAsync()
    {
        if (_selected is not DownloadOperation download) return;
        await TuiPrompts.ShowTextAsync(CoreTools.Translate("Show in explorer"), download.DownloadLocation,
            [(CoreTools.Translate("Open"), () => TuiPackageActions.OpenExternally(Path.GetDirectoryName(download.DownloadLocation) ?? download.DownloadLocation))]);
    }
}
