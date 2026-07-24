using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Operations.History;

namespace UniGetUI.Interface;

public sealed class IpcAppLogEntry
{
    public string Time { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Content { get; set; } = "";
}

/// <summary>
/// A structured, persisted operation-history entry (the agent-readable view of the history store).
/// <see cref="Content"/> is a human-readable one-line summary kept for backward compatibility.
/// </summary>
public class IpcOperationHistoryEntry
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "";
    public int Role { get; set; }
    public string PackageId { get; set; } = "";
    public string PackageName { get; set; } = "";
    public string ManagerName { get; set; } = "";
    public string SourceName { get; set; } = "";
    public string VersionBefore { get; set; } = "";
    public string VersionAfter { get; set; } = "";
    public string Status { get; set; } = "";
    public string Timestamp { get; set; } = "";
    public int OutputLineCount { get; set; }
    public int? ExitCode { get; set; }
    public string FailureSummary { get; set; } = "";
    public string Content { get; set; } = "";
}

/// <summary>An operation-history entry including its full console output.</summary>
public sealed class IpcOperationHistoryDetails : IpcOperationHistoryEntry
{
    public IReadOnlyList<IpcOperationOutputLine> Output { get; set; } = [];
}

public sealed class IpcManagerLogTask
{
    public int Index { get; set; }
    public string[] Lines { get; set; } = [];
}

public sealed class IpcManagerLogInfo
{
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Version { get; set; } = "";
    public IpcManagerLogTask[] Tasks { get; set; } = [];
}

public static class IpcLogsApi
{
    public static IReadOnlyList<IpcAppLogEntry> ListAppLog(int level = 4)
    {
        return Logger.GetLogs()
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Content) && !ShouldSkip(entry.Severity, level))
            .Select(entry => new IpcAppLogEntry
            {
                Time = entry.Time.ToString("O"),
                Severity = entry.Severity.ToString().ToLowerInvariant(),
                Content = entry.Content,
            })
            .ToArray();
    }

    public static IReadOnlyList<IpcOperationHistoryEntry> ListOperationHistory()
    {
        return OperationHistoryStore.GetAll()
            .Select(ToEntry)
            .ToArray();
    }

    public static IpcOperationHistoryDetails? GetOperationHistoryEntry(string id)
    {
        var record = OperationHistoryStore.Get(id);
        if (record is null) return null;

        var details = new IpcOperationHistoryDetails
        {
            Output = record.Output
                .Select(line => new IpcOperationOutputLine { Text = line.Text, Type = line.Type })
                .ToArray(),
        };
        CopyEntryFields(record, details);
        return details;
    }

    private static IpcOperationHistoryEntry ToEntry(OperationHistoryRecord record)
    {
        var entry = new IpcOperationHistoryEntry();
        CopyEntryFields(record, entry);
        return entry;
    }

    private static void CopyEntryFields(OperationHistoryRecord record, IpcOperationHistoryEntry entry)
    {
        entry.Id = record.Id;
        entry.Kind = record.Kind;
        entry.Role = record.Role;
        entry.PackageId = record.PackageId;
        entry.PackageName = record.PackageName;
        entry.ManagerName = record.ManagerName;
        entry.SourceName = record.SourceName;
        entry.VersionBefore = record.VersionBefore;
        entry.VersionAfter = record.VersionAfter;
        entry.Status = record.Status;
        entry.Timestamp = record.TimestampUtc;
        entry.OutputLineCount = record.Output.Count;
        entry.ExitCode = record.ExitCode;
        entry.FailureSummary = record.FailureSummary;
        entry.Content = BuildSummary(record);
    }

    private static string BuildSummary(OperationHistoryRecord record)
    {
        string target = string.IsNullOrEmpty(record.PackageName) ? record.PackageId : record.PackageName;
        string version = record.VersionBefore != record.VersionAfter && record.VersionAfter.Length > 0
            ? $"{record.VersionBefore} -> {record.VersionAfter}"
            : record.VersionBefore;
        var parts = new List<string> { record.Kind };
        if (record.ManagerName.Length > 0) parts.Add(record.ManagerName);
        if (target.Length > 0) parts.Add(target);
        if (version.Length > 0) parts.Add($"({version})");
        parts.Add($"[{record.Status}]");
        return string.Join(' ', parts);
    }

    public static IReadOnlyList<IpcManagerLogInfo> ListManagerLogs(
        string? managerName = null,
        bool verbose = false
    )
    {
        return ResolveManagers(managerName)
            .Select(manager => new IpcManagerLogInfo
            {
                Name = IpcManagerSettingsApi.GetPublicManagerId(manager),
                DisplayName = manager.DisplayName,
                Version = manager.Status.Version,
                Tasks = manager.TaskLogger.Operations
                    .Select((operation, index) => new IpcManagerLogTask
                    {
                        Index = index,
                        Lines = operation
                            .AsColoredString(verbose)
                            .Where(line => !string.IsNullOrWhiteSpace(line))
                            .Select(StripColorCode)
                            .Where(line => !string.IsNullOrWhiteSpace(line))
                            .ToArray(),
                    })
                    .Where(task => task.Lines.Length > 0)
                    .ToArray(),
            })
            .ToArray();
    }

    private static IReadOnlyList<IPackageManager> ResolveManagers(string? managerName)
    {
        var managers = IpcManagerSettingsApi.ResolveManagers(managerName)
            .OrderBy(manager => manager.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return managers;
    }

    private static bool ShouldSkip(LogEntry.SeverityLevel severity, int level) =>
        level switch
        {
            <= 1 => severity != LogEntry.SeverityLevel.Error,
            2 => severity is LogEntry.SeverityLevel.Debug
                      or LogEntry.SeverityLevel.Info
                      or LogEntry.SeverityLevel.Success,
            3 => severity is LogEntry.SeverityLevel.Debug or LogEntry.SeverityLevel.Info,
            4 => severity == LogEntry.SeverityLevel.Debug,
            _ => false,
        };

    private static string StripColorCode(string line)
    {
        return line.Length > 1 && char.IsDigit(line[0]) ? line[1..] : line;
    }
}
