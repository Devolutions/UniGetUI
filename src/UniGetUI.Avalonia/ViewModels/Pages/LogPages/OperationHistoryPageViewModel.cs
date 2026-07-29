using Avalonia.Media;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Operations.History;

namespace UniGetUI.Avalonia.ViewModels.Pages.LogPages;

/// <summary>
/// Backs the "Log" tab of the operation history page. Renders the raw console output of every
/// recorded operation from <see cref="OperationHistoryStore"/> (the single source of truth),
/// preserving the classic separated-blocks log experience.
/// </summary>
public class OperationHistoryPageViewModel : BaseLogPageViewModel
{
    private const string SeparatorBar =
        "▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄";

    public OperationHistoryPageViewModel() : base(false)
    {
        LoadLog();
    }

    protected override void LoadLogLevels() { }

    public override void LoadLog(bool isReload = false)
    {
        bool isDark = IsDark;
        var defaultBrush = new SolidColorBrush(isDark ? Color.FromRgb(250, 250, 250) : Color.FromRgb(0, 0, 0));
        var errorBrush = new SolidColorBrush(isDark ? Color.FromRgb(255, 80, 80) : Color.FromRgb(205, 0, 0));

        LogLines.Clear();

        foreach (var record in OperationHistoryStore.GetAll())
        {
            LogLines.Add(new LogLineItem("", defaultBrush));
            LogLines.Add(new LogLineItem(SeparatorBar, defaultBrush));
            LogLines.Add(new LogLineItem(BuildHeader(record), defaultBrush));

            foreach (var line in record.Output)
            {
                string text = line.Text.Replace("\r", "").Replace("\n", "");
                LogLines.Add(new LogLineItem(text, line.Type == "Error" ? errorBrush : defaultBrush));
            }
        }

        if (isReload)
            FireScrollToBottom();
    }

    private static string BuildHeader(OperationHistoryRecord record)
    {
        if (record.Kind == OperationHistoryRecord.KindLegacyLog)
            return CoreTools.Translate("Imported history (from a previous version)");

        string target = string.IsNullOrEmpty(record.PackageName) ? record.PackageId : record.PackageName;
        string version = record.VersionAfter.Length > 0 && record.VersionBefore != record.VersionAfter
            ? $"{record.VersionBefore} -> {record.VersionAfter}"
            : record.VersionBefore;

        string timestamp = record.TimestampUtc;
        if (DateTime.TryParse(record.TimestampUtc, null,
                System.Globalization.DateTimeStyles.RoundtripKind, out var utc))
            timestamp = utc.ToLocalTime().ToString("g");

        return CoreTools.Translate("Operation") + $": {record.Kind} {record.ManagerName} {target} " +
               (version.Length > 0 ? $"({version}) " : "") +
               $"[{record.Status}] — {timestamp}";
    }
}
