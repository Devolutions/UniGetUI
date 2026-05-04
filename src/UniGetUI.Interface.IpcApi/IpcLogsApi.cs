using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Interface;

public sealed class IpcAppLogEntry
{
    public string Time { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Content { get; set; } = "";
}

public sealed class IpcOperationHistoryEntry
{
    public string Content { get; set; } = "";
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
        return Settings.GetValue(Settings.K.OperationHistory)
            .Split('\n')
            .Select(line => line.Replace("\r", "").Replace("\n", "").Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => new IpcOperationHistoryEntry { Content = line })
            .ToArray();
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
