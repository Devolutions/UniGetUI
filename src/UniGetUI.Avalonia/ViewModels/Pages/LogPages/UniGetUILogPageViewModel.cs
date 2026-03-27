using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.ViewModels.Pages.LogPages;

public class UniGetUILogPageViewModel : BaseLogPageViewModel
{
    public UniGetUILogPageViewModel()
#if DEBUG
        : base(true, initialLogLevelIndex: 4)
#else
        : base(true, initialLogLevelIndex: 3)
#endif
    {
        LoadLog();
    }

    protected override void LoadLogLevels()
    {
        LogLevelItems.Clear();
        LogLevelItems.Add(CoreTools.Translate("1 - Errors"));
        LogLevelItems.Add(CoreTools.Translate("2 - Warnings"));
        LogLevelItems.Add(CoreTools.Translate("3 - Information (less)"));
        LogLevelItems.Add(CoreTools.Translate("4 - Information (more)"));
        LogLevelItems.Add(CoreTools.Translate("5 - information (debug)"));
    }

    public override void LoadLog(bool isReload = false)
    {
        bool isDark = IsDark;
        int logLevel = SelectedLogLevelIndex + 1;

        LogLines.Clear();

        foreach (LogEntry entry in Logger.GetLogs())
        {
            if (entry.Content == "")
                continue;

            if (ShouldSkip(entry.Severity, logLevel))
                continue;

            var brush = GetSeverityBrush(entry.Severity, isDark);
            var sb = new System.Text.StringBuilder();
            bool first = true;
            int dateLength = 0;

            foreach (string line in entry.Content.Split('\n'))
            {
                if (first)
                {
                    string prefix = $"[{entry.Time}] ";
                    sb.Append(prefix).Append(line);
                    dateLength = prefix.Length;
                    first = false;
                }
                else
                {
                    sb.Append('\n').Append(' ', dateLength).Append(line);
                }
            }

            LogLines.Add(new LogLineItem(sb.ToString(), brush));
        }

        if (isReload)
            FireScrollToBottom();
    }

    private static bool ShouldSkip(LogEntry.SeverityLevel severity, int logLevel) =>
        logLevel switch
        {
            1 => severity != LogEntry.SeverityLevel.Error,
            2 => severity is LogEntry.SeverityLevel.Debug
                          or LogEntry.SeverityLevel.Info
                          or LogEntry.SeverityLevel.Success,
            3 => severity is LogEntry.SeverityLevel.Debug
                          or LogEntry.SeverityLevel.Info,
            4 => severity == LogEntry.SeverityLevel.Debug,
            _ => false,
        };
}
