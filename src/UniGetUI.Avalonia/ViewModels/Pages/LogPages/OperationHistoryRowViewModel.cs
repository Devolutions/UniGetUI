using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Views.Controls;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Operations.History;

namespace UniGetUI.Avalonia.ViewModels.Pages.LogPages;

/// <summary>One row of the operation-history list: a read-only projection of a persisted record plus its actions.</summary>
public partial class OperationHistoryRowViewModel : ViewModelBase
{
    private const string SymbolBase = "avares://UniGetUI/Assets/Symbols/";

    public OperationHistoryRecord Record { get; }

    public OperationHistoryRowViewModel(OperationHistoryRecord record)
    {
        Record = record;
    }

    public string TargetName => string.IsNullOrEmpty(Record.PackageName) ? Record.PackageId : Record.PackageName;
    public string PackageId => Record.PackageId;

    public string KindLabel => Record.Kind switch
    {
        "install-package" => CoreTools.Translate("Install"),
        "update-package" => CoreTools.Translate("Update"),
        "uninstall-package" => CoreTools.Translate("Uninstall"),
        "download-package" => CoreTools.Translate("Download"),
        "add-source" => CoreTools.Translate("Add source"),
        "remove-source" => CoreTools.Translate("Remove source"),
        _ => Record.Kind,
    };

    public string KindIconPath => SymbolBase + (Record.Kind switch
    {
        "install-package" => "installed.svg",
        "update-package" => "update.svg",
        "uninstall-package" => "delete.svg",
        "download-package" => "download.svg",
        "add-source" or "remove-source" => "Sources.svg",
        _ => "history.svg",
    });

    public string VersionChange
    {
        get
        {
            if (Record.VersionAfter.Length > 0 && Record.VersionBefore != Record.VersionAfter)
                return $"{Record.VersionBefore} → {Record.VersionAfter}";
            return Record.VersionBefore.Length > 0 ? Record.VersionBefore : Record.VersionAfter;
        }
    }

    public string SourceLabel
    {
        get
        {
            if (Record.ManagerName.Length == 0) return Record.SourceName;
            if (Record.SourceName.Length == 0 || Record.SourceName == Record.ManagerName) return Record.ManagerName;
            return $"{Record.ManagerName}: {Record.SourceName}";
        }
    }

    public string StatusLabel => Record.Status switch
    {
        OperationHistoryRecord.StatusSucceeded => CoreTools.Translate("Succeeded"),
        OperationHistoryRecord.StatusFailed => CoreTools.Translate("Failed"),
        OperationHistoryRecord.StatusCanceled => CoreTools.Translate("Canceled"),
        _ => Record.Status,
    };

    public StatusBadgeSeverity StatusSeverity => Record.Status switch
    {
        OperationHistoryRecord.StatusSucceeded => StatusBadgeSeverity.Success,
        OperationHistoryRecord.StatusFailed => StatusBadgeSeverity.Error,
        _ => StatusBadgeSeverity.Info,
    };

    public string StatusTooltip => Record.ExitCode is { } code
        ? CoreTools.Translate("Exit code: {0}", code)
        : StatusLabel;

    public DateTime Timestamp
        => DateTime.TryParse(Record.TimestampUtc, null,
               System.Globalization.DateTimeStyles.RoundtripKind, out var utc)
            ? utc
            : DateTime.MinValue;

    public string TimestampLabel => FormatRelative(Timestamp);

    public string TimestampTooltip
        => Timestamp == DateTime.MinValue ? Record.TimestampUtc : Timestamp.ToLocalTime().ToString("f");

    private static string FormatRelative(DateTime utc)
    {
        if (utc == DateTime.MinValue) return "";
        var delta = DateTime.UtcNow - utc;
        if (delta < TimeSpan.Zero) delta = TimeSpan.Zero;
        if (delta.TotalMinutes < 1) return CoreTools.Translate("just now");
        if (delta.TotalMinutes < 60) return CoreTools.Translate("{0} min ago", (int)delta.TotalMinutes);
        if (delta.TotalHours < 24) return CoreTools.Translate("{0} h ago", (int)delta.TotalHours);
        if (delta.TotalDays < 7) return CoreTools.Translate("{0} d ago", (int)delta.TotalDays);
        return utc.ToLocalTime().ToString("d");
    }

    public bool HasOutput => Record.Output.Count > 0;

    /// <summary>Compact, paste-ready one-line summary (for bug reports); language-neutral status/values.</summary>
    public string DetailsSummary
    {
        get
        {
            var parts = new List<string>();
            if (Record.ManagerName.Length > 0) parts.Add(Record.ManagerName);
            parts.Add(PackageId.Length > 0 && PackageId != TargetName ? $"{TargetName} ({PackageId})" : TargetName);
            if (VersionChange.Length > 0) parts.Add(VersionChange);
            parts.Add(Record.Status);
            if (Record.ExitCode is { } code) parts.Add(CoreTools.Translate("exit {0}", code));
            if (Record.FailureSummary.Length > 0) parts.Add(Record.FailureSummary);
            return string.Join(" · ", parts);
        }
    }

    public bool CanRevert => OperationHistoryActionService.CanRevert(Record);
    public bool CanReRun => OperationHistoryActionService.CanReRun(Record);

    private (bool AsAdmin, bool Interactive, bool SkipHash)? _retryModes;
    private (bool AsAdmin, bool Interactive, bool SkipHash) RetryModes
        => _retryModes ??= OperationHistoryActionService.GetRetryModes(Record);

    public bool CanRetryAsAdmin => RetryModes.AsAdmin;
    public bool CanRetryInteractive => RetryModes.Interactive;
    public bool CanRetrySkipHash => RetryModes.SkipHash;

    [RelayCommand]
    private async Task Revert() => await OperationHistoryActionService.RevertAsync(Record);

    [RelayCommand]
    private async Task ReRun() => await OperationHistoryActionService.ReRunAsync(Record);

    [RelayCommand]
    private async Task ViewLog() => await OperationHistoryLogDialog.ShowAsync(Record);

    [RelayCommand]
    private void Remove() => OperationHistoryStore.Remove(Record.Id);

    [RelayCommand]
    private async Task RetryAsAdmin() => await OperationHistoryActionService.RetryAsync(Record, "admin");

    [RelayCommand]
    private async Task RetryInteractive() => await OperationHistoryActionService.RetryAsync(Record, "interactive");

    [RelayCommand]
    private async Task RetrySkipHash() => await OperationHistoryActionService.RetryAsync(Record, "skip-hash");
}

/// <summary>
/// Strongly-typed, reflection-free comparer for a history column. Assigned to
/// <c>DataGridColumn.CustomSortComparer</c> so header-click sorting stays NativeAOT-safe
/// (the default <c>SortMemberPath</c> path is reflection-based and gets trimmed away).
/// </summary>
internal sealed class OperationHistoryRowComparer : System.Collections.IComparer
{
    private readonly string _key;

    public OperationHistoryRowComparer(string key) => _key = key;

    public int Compare(object? x, object? y)
    {
        if (x is not OperationHistoryRowViewModel a || y is not OperationHistoryRowViewModel b)
            return 0;

        return _key switch
        {
            "kind" => string.Compare(a.KindLabel, b.KindLabel, StringComparison.OrdinalIgnoreCase),
            "package" => string.Compare(a.TargetName, b.TargetName, StringComparison.OrdinalIgnoreCase),
            "version" => string.Compare(a.VersionChange, b.VersionChange, StringComparison.OrdinalIgnoreCase),
            "source" => string.Compare(a.SourceLabel, b.SourceLabel, StringComparison.OrdinalIgnoreCase),
            "status" => string.Compare(a.StatusLabel, b.StatusLabel, StringComparison.OrdinalIgnoreCase),
            "date" => a.Timestamp.CompareTo(b.Timestamp),
            _ => 0,
        };
    }
}
