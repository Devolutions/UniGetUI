using System.Globalization;
using System.Linq.Expressions;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Operations.History;
using UniGetUI.Tui.Infrastructure;
using UniGetUI.Tui.Theme;
using UniGetUI.Tui.Views.Dialogs;

namespace UniGetUI.Tui.Views.Pages;

/// <summary>
/// Operation history (desktop <c>OperationHistoryPage</c>): every finished operation from
/// <see cref="OperationHistoryStore"/>, filterable by status, kind, manager and text, with revert,
/// re-run, retry, full-log, copy and remove actions.
/// </summary>
internal sealed class HistoryPage : UserControl, ITuiPage
{
    internal sealed record HistoryRow(OperationHistoryRecord Record, string When, string Kind, string Package, string Manager, string Status, string Versions)
    {
        public override string ToString() => Package;
    }

    private readonly DataGrid _grid;
    private readonly TextBox _query;
    private readonly TextBlock _status;
    private readonly TextBlock _filters;
    private string _statusFilter = "";
    private string _kindFilter = "";
    private string _managerFilter = "";
    private List<HistoryRow> _rows = [];

    public HistoryPage()
    {
        _query = new TextBox { PlaceholderText = CoreTools.Translate("Search operation history") };
        _query.TextChanged += (_, _) => Refresh();
        _query.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Return or Key.Down && _rows.Count > 0)
            {
                TuiFocus.SeatDataGridFocus(_grid);
                e.Handled = true;
            }
        };
        _status = new TextBlock { Foreground = TuiPalette.TextMuted };
        _filters = new TextBlock { Foreground = TuiPalette.TextMuted };
        _grid = new DataGrid
        {
            MinColumnWidth = 3,
            AutoGenerateColumns = false,
            IsReadOnly = true,
            Background = Brushes.Transparent,
            HeadersVisibility = DataGridHeadersVisibility.Column,
            SelectionMode = DataGridSelectionMode.Single,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        // Compiled (lambda) bindings: no reflection, so they survive trimming and NativeAOT.
        void Col(string header, Expression<Func<HistoryRow, string>> prop, double width) => _grid.Columns.Add(new DataGridTextColumn
        {
            Header = header,
            Binding = CompiledBinding.Create(prop),
            Width = width > 0 ? new DataGridLength(width) : new DataGridLength(1, DataGridLengthUnitType.Star),
        });
        Col(CoreTools.Translate("Date"), r => r.When, 17);
        Col(CoreTools.Translate("Operation"), r => r.Kind, 11);
        Col(CoreTools.Translate("Package"), r => r.Package, 0);
        Col(CoreTools.Translate("Manager"), r => r.Manager, 12);
        Col(CoreTools.Translate("Status"), r => r.Status, 10);
        Col(CoreTools.Translate("Version"), r => r.Versions, 20);
        _grid.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.KeyModifiers == KeyModifiers.None && e.Key is Key.Enter or Key.Return)
            {
                _ = ViewLogAsync();
                e.Handled = true;
            }
            else if (e.KeyModifiers == KeyModifiers.None && e.Key == Key.Delete)
            {
                RemoveSelected();
                e.Handled = true;
            }
        }, RoutingStrategies.Tunnel);
        _grid.AddHandler(TextInputEvent, OnGridTextInput, RoutingStrategies.Tunnel);

        var header = new StackPanel();
        header.Children.Add(TuiChrome.PageTitle(CoreTools.Translate("Operation history"), _status));
        var queryRow = new DockPanel { LastChildFill = true };
        var label = new TextBlock { Text = "Search ", Foreground = TuiPalette.Brand, VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(label, Dock.Left);
        queryRow.Children.Add(label);
        queryRow.Children.Add(_query);
        header.Children.Add(queryRow);
        header.Children.Add(_filters);
        var root = new DockPanel { LastChildFill = true, Margin = new Thickness(1, 0, 1, 0) };
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        root.Children.Add(_grid);
        Content = root;
        OperationHistoryStore.Changed += (_, _) => Dispatcher.UIThread.Post(Refresh);
    }

    public IReadOnlyList<TuiKeyHint> KeyHints => TuiChrome.Hints(("Enter", "View log"), ("m", "Actions"), ("z", "Revert"), ("a", "Run again"), ("t", "Retry"), ("Del", "Remove"), ("f", "Filters"), ("/", "Search"));

    private OperationHistoryRecord? Selected => (_grid.SelectedItem as HistoryRow)?.Record;

    public IReadOnlyList<TuiAction> Actions
    {
        get
        {
            var r = Selected;
            var modes = r is null ? (false, false, false) : TuiHistoryActions.GetRetryModes(r);
            return
            [
                new(CoreTools.Translate("View full log"), "Enter", ViewLogAsync, () => r is not null),
                new(CoreTools.Translate("Revert"), "z", () => TuiHistoryActions.RevertAsync(r!), () => r is not null && TuiHistoryActions.CanRevert(r)),
                new(CoreTools.Translate("Run again"), "a", () => TuiHistoryActions.ReRunAsync(r!), () => r is not null && TuiHistoryActions.CanReRun(r)),
                new(CoreTools.Translate("Retry"), "t", () => TuiHistoryActions.RetryAsync(r!, ""), () => r is not null && r.Status == OperationHistoryRecord.StatusFailed && TuiHistoryActions.CanReRun(r)),
                new(CoreTools.Translate("Retry as administrator"), null, () => TuiHistoryActions.RetryAsync(r!, "admin"), () => modes.Item1),
                new(CoreTools.Translate("Retry interactively"), null, () => TuiHistoryActions.RetryAsync(r!, "interactive"), () => modes.Item2),
                new(CoreTools.Translate("Retry skipping integrity checks"), null, () => TuiHistoryActions.RetryAsync(r!, "skip-hash"), () => modes.Item3),
                TuiAction.Separator,
                new(CoreTools.Translate("Copy details"), "c", CopyDetailsAsync, () => r is not null),
                new(CoreTools.Translate("Remove from history"), "Del", () =>
                {
                    RemoveSelected();
                    return Task.CompletedTask;
                }, () => r is not null),
                new(CoreTools.Translate("Clear history"), null, ClearAsync, () => _rows.Count > 0),
                new(CoreTools.Translate("Filters") + "…", "f", ChooseFiltersAsync),
            ];
        }
    }

    public bool FocusPrimary() => _rows.Count > 0 ? TuiFocus.SeatDataGridFocus(_grid) : _query.Focus();

    public bool FocusSearch() => _query.Focus();

    public void Reload()
    {
        OperationHistoryStore.InvalidateCache();
        Refresh();
    }

    public void OnShown() => Refresh();

    public IReadOnlyList<HistoryRow> Rows => _rows;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Refresh();
    }

    private void OnGridTextInput(object? sender, TextInputEventArgs e)
    {
        var r = Selected;
        switch (e.Text)
        {
            case "z" when r is not null && TuiHistoryActions.CanRevert(r): _ = TuiHistoryActions.RevertAsync(r); break;
            case "a" when r is not null && TuiHistoryActions.CanReRun(r): _ = TuiHistoryActions.ReRunAsync(r); break;
            case "t" when r is not null && r.Status == OperationHistoryRecord.StatusFailed: _ = TuiHistoryActions.RetryAsync(r, ""); break;
            case "c": _ = CopyDetailsAsync(); break;
            case "f": _ = ChooseFiltersAsync(); break;
            case "m": _ = ShowMenuAsync(); break;
            case "/": _query.Focus(); break;
        }

        e.Handled = true;
    }

    private async Task ShowMenuAsync()
    {
        var actions = Actions;
        int? picked = await TuiPrompts.ChooseAsync(CoreTools.Translate("Operation history"),
            actions.Select(a => a.IsSeparator ? TuiChoice.Separator : new TuiChoice(a.Label, a.IsEnabled, a.Shortcut)).ToList());
        if (picked is int i) await actions[i].Run();
    }

    public void Refresh()
    {
        string q = (_query.Text ?? "").Trim();
        OperationHistoryRecord? selected = Selected;
        _rows = OperationHistoryStore.GetAll()
            .OrderByDescending(r => r.TimestampUtc, StringComparer.Ordinal)
            .Where(r => _statusFilter.Length == 0 || r.Status == _statusFilter)
            .Where(r => _kindFilter.Length == 0 || r.Kind == _kindFilter)
            .Where(r => _managerFilter.Length == 0 || r.ManagerName.Equals(_managerFilter, StringComparison.OrdinalIgnoreCase))
            .Where(r => q.Length == 0 || $"{r.PackageName} {r.PackageId} {r.ManagerName} {r.SourceName} {r.FailureSummary}".Contains(q, StringComparison.OrdinalIgnoreCase))
            .Select(ToRow)
            .ToList();
        _grid.ItemsSource = _rows;
        if (selected is not null && _rows.FirstOrDefault(r => r.Record.Id == selected.Id) is { } match) _grid.SelectedItem = match;
        _filters.Text = CoreTools.Translate("Status") + $": {(_statusFilter.Length == 0 ? CoreTools.Translate("All") : _statusFilter)}  ·  "
                        + CoreTools.Translate("Operation") + $": {(_kindFilter.Length == 0 ? CoreTools.Translate("All") : _kindFilter)}  ·  "
                        + CoreTools.Translate("Manager") + $": {(_managerFilter.Length == 0 ? CoreTools.Translate("All") : _managerFilter)}";
        _status.Text = _rows.Count == 0 ? CoreTools.Translate("No operations have been recorded yet.") : CoreTools.Translate("{0} operations", _rows.Count);
    }

    private static HistoryRow ToRow(OperationHistoryRecord r)
    {
        string when = DateTime.TryParse(r.TimestampUtc, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
            : r.TimestampUtc;
        string kind = r.Kind switch
        {
            "install-package" => CoreTools.Translate("Install"),
            "update-package" => CoreTools.Translate("Update"),
            "uninstall-package" => CoreTools.Translate("Uninstall"),
            "download-package" => CoreTools.Translate("Download"),
            "add-source" => CoreTools.Translate("Add source"),
            "remove-source" => CoreTools.Translate("Remove source"),
            _ => r.Kind,
        };
        string versions = r.VersionBefore.Length > 0 && r.VersionAfter.Length > 0 && r.VersionBefore != r.VersionAfter
            ? $"{r.VersionBefore} -> {r.VersionAfter}"
            : r.VersionAfter.Length > 0 ? r.VersionAfter : r.VersionBefore;
        string package = r.PackageName.Length > 0 ? r.PackageName : r.PackageId.Length > 0 ? r.PackageId : r.SourceName;
        return new HistoryRow(r, when, kind, package, r.ManagerName, r.Status, versions);
    }

    private async Task ChooseFiltersAsync()
    {
        var statuses = new[] { "", OperationHistoryRecord.StatusSucceeded, OperationHistoryRecord.StatusFailed, OperationHistoryRecord.StatusCanceled };
        var kinds = new[] { "", "install-package", "update-package", "uninstall-package", "download-package", "add-source", "remove-source" };
        var managers = new[] { "" }.Concat(TuiEngine.Managers.Select(m => m.Id)).ToArray();
        var choices = new List<TuiChoice>
        {
            new(CoreTools.Translate("Status") + ": " + (_statusFilter.Length == 0 ? CoreTools.Translate("All") : _statusFilter)),
            new(CoreTools.Translate("Operation") + ": " + (_kindFilter.Length == 0 ? CoreTools.Translate("All") : _kindFilter)),
            new(CoreTools.Translate("Manager") + ": " + (_managerFilter.Length == 0 ? CoreTools.Translate("All") : _managerFilter)),
            new(CoreTools.Translate("Clear filters")),
        };
        int? picked = await TuiPrompts.ChooseAsync(CoreTools.Translate("Filters"), choices);
        string[]? values = picked switch { 0 => statuses, 1 => kinds, 2 => managers, _ => null };
        if (picked == 3)
        {
            _statusFilter = _kindFilter = _managerFilter = "";
            Refresh();
            return;
        }

        if (values is null) return;
        int? value = await TuiPrompts.ChooseAsync(choices[picked!.Value].Label,
            values.Select(v => new TuiChoice(v.Length == 0 ? CoreTools.Translate("All") : v)).ToList());
        if (value is not int v) return;
        switch (picked)
        {
            case 0: _statusFilter = values[v]; break;
            case 1: _kindFilter = values[v]; break;
            case 2: _managerFilter = values[v]; break;
        }

        Refresh();
    }

    private static string Details(OperationHistoryRecord r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{CoreTools.Translate("Operation")}: {r.Kind}");
        sb.AppendLine($"{CoreTools.Translate("Package")}: {r.PackageName} [{r.PackageId}]");
        sb.AppendLine($"{CoreTools.Translate("Manager")}: {r.ManagerName}  ·  {CoreTools.Translate("Source")}: {r.SourceName}");
        sb.AppendLine($"{CoreTools.Translate("Version")}: {r.VersionBefore} -> {r.VersionAfter}");
        sb.AppendLine($"{CoreTools.Translate("Status")}: {r.Status}  ·  {CoreTools.Translate("Exit code")}: {r.ExitCode?.ToString() ?? "—"}  ·  {CoreTools.Translate("Elevated")}: {r.RanElevated?.ToString() ?? "—"}");
        sb.AppendLine($"{CoreTools.Translate("Date")}: {r.TimestampUtc}");
        if (r.FailureSummary.Length > 0) sb.AppendLine($"{CoreTools.Translate("Failure")}: {r.FailureSummary}");
        return sb.ToString();
    }

    private async Task ViewLogAsync()
    {
        if (Selected is not { } r) return;
        string text = Details(r) + Environment.NewLine + string.Join(Environment.NewLine, r.Output.Select(l => l.Text));
        await TuiPrompts.ShowTextAsync(CoreTools.Translate("Operation log") + ": " + TuiHistoryActions.DisplayName(r), text);
    }

    private Task CopyDetailsAsync() => Selected is { } r ? TuiClipboard.CopyAsync(this, TuiHistoryActions.DisplayName(r), Details(r)) : Task.CompletedTask;

    private void RemoveSelected()
    {
        if (Selected is { } r) OperationHistoryStore.Remove(r.Id);
    }

    private async Task ClearAsync()
    {
        if (await TuiPrompts.ConfirmAsync(CoreTools.Translate("Clear history"),
                CoreTools.Translate("Do you really want to clear the operation history? This action cannot be undone.")))
            OperationHistoryStore.Clear();
    }
}
