using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Operations.History;

namespace UniGetUI.Avalonia.ViewModels.Pages.LogPages;

/// <summary>A selectable filter value (a facet like status/kind/manager). Key "" means "all".</summary>
public sealed class HistoryFilterOption
{
    public string Key { get; init; } = "";
    public string Label { get; init; } = "";
    public override string ToString() => Label;
}

/// <summary>Backs the "History" tab: a live, structured list of finished operations with per-row actions.</summary>
public partial class OperationHistoryListViewModel : ViewModelBase
{
    private readonly List<OperationHistoryRowViewModel> _all = new();
    private string _filter = "";
    private bool _suppressFilter;

    public ObservableCollection<OperationHistoryRowViewModel> Entries { get; } = new();

    public ObservableCollection<HistoryFilterOption> StatusOptions { get; } = new();
    public ObservableCollection<HistoryFilterOption> KindOptions { get; } = new();
    public ObservableCollection<HistoryFilterOption> ManagerOptions { get; } = new();

    [ObservableProperty]
    private HistoryFilterOption? _selectedStatus;

    [ObservableProperty]
    private HistoryFilterOption? _selectedKind;

    [ObservableProperty]
    private HistoryFilterOption? _selectedManager;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private string _emptyMessage = "";

    /// <summary>Raised when the user asks to clear all history; the view confirms and calls <see cref="ClearConfirmed"/>.</summary>
    public event EventHandler? ClearRequested;

    public OperationHistoryListViewModel()
    {
        StatusOptions.Add(new() { Key = "", Label = CoreTools.Translate("All statuses") });
        StatusOptions.Add(new() { Key = OperationHistoryRecord.StatusSucceeded, Label = CoreTools.Translate("Succeeded") });
        StatusOptions.Add(new() { Key = OperationHistoryRecord.StatusFailed, Label = CoreTools.Translate("Failed") });
        StatusOptions.Add(new() { Key = OperationHistoryRecord.StatusCanceled, Label = CoreTools.Translate("Canceled") });

        KindOptions.Add(new() { Key = "", Label = CoreTools.Translate("All types") });
        KindOptions.Add(new() { Key = "install-package", Label = CoreTools.Translate("Install") });
        KindOptions.Add(new() { Key = "update-package", Label = CoreTools.Translate("Update") });
        KindOptions.Add(new() { Key = "uninstall-package", Label = CoreTools.Translate("Uninstall") });
        KindOptions.Add(new() { Key = "download-package", Label = CoreTools.Translate("Download") });
        KindOptions.Add(new() { Key = "add-source", Label = CoreTools.Translate("Add source") });
        KindOptions.Add(new() { Key = "remove-source", Label = CoreTools.Translate("Remove source") });

        _selectedStatus = StatusOptions[0];
        _selectedKind = KindOptions[0];

        OperationHistoryStore.Changed += OnStoreChanged;
        Load();
    }

    private void OnStoreChanged(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(Load);

    partial void OnSelectedStatusChanged(HistoryFilterOption? value) => ApplyFilter();
    partial void OnSelectedKindChanged(HistoryFilterOption? value) => ApplyFilter();
    partial void OnSelectedManagerChanged(HistoryFilterOption? value) => ApplyFilter();

    public void Load()
    {
        _suppressFilter = true;

        _all.Clear();
        foreach (var record in OperationHistoryStore.GetAll())
        {
            // The imported legacy blob has no structured metadata, so it lives only in the Log tab.
            if (record.Kind == OperationHistoryRecord.KindLegacyLog) continue;
            _all.Add(new OperationHistoryRowViewModel(record));
        }

        RebuildManagerOptions();

        _suppressFilter = false;
        ApplyFilter();
    }

    private void RebuildManagerOptions()
    {
        string? previous = SelectedManager?.Key;

        ManagerOptions.Clear();
        ManagerOptions.Add(new() { Key = "", Label = CoreTools.Translate("All managers") });
        foreach (var manager in _all.Select(r => r.Record.ManagerName)
                     .Where(m => m.Length > 0)
                     .Distinct()
                     .OrderBy(m => m, StringComparer.OrdinalIgnoreCase))
        {
            ManagerOptions.Add(new() { Key = manager, Label = manager });
        }

        SelectedManager = ManagerOptions.FirstOrDefault(o => o.Key == previous) ?? ManagerOptions[0];
    }

    public void SetFilter(string query)
    {
        _filter = (query ?? "").Trim();
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        if (_suppressFilter) return;

        string status = SelectedStatus?.Key ?? "";
        string kind = SelectedKind?.Key ?? "";
        string manager = SelectedManager?.Key ?? "";

        Entries.Clear();
        foreach (var row in _all)
        {
            if (status.Length > 0 && row.Record.Status != status) continue;
            if (kind.Length > 0 && row.Record.Kind != kind) continue;
            if (manager.Length > 0 && row.Record.ManagerName != manager) continue;
            if (!MatchesText(row, _filter)) continue;
            Entries.Add(row);
        }

        IsEmpty = Entries.Count == 0;
        bool anyFilterActive = _filter.Length > 0 || status.Length > 0 || kind.Length > 0 || manager.Length > 0;
        EmptyMessage = anyFilterActive
            ? CoreTools.Translate("No operations match the current filters")
            : CoreTools.Translate("No operations have been performed yet");
    }

    private static bool MatchesText(OperationHistoryRowViewModel row, string query)
    {
        if (query.Length == 0) return true;

        var r = row.Record;
        string haystack = string.Join(
            ' ',
            r.PackageName, r.PackageId, r.ManagerName, r.SourceName,
            row.KindLabel, row.VersionChange, row.StatusLabel);

        return haystack.Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void Clear() => ClearRequested?.Invoke(this, EventArgs.Empty);

    public void ClearConfirmed() => OperationHistoryStore.Clear();
}
