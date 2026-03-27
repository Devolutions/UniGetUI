using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Avalonia.ViewModels.Pages.LogPages;

public class ManagerLogsPageViewModel : BaseLogPageViewModel
{
    public ManagerLogsPageViewModel() : base(true)
    {
        LoadLog();
    }

    protected override void LoadLogLevels()
    {
        LogLevelItems.Clear();
        foreach (IPackageManager manager in PEInterface.Managers)
        {
            LogLevelItems.Add(manager.DisplayName);
            LogLevelItems.Add($"{manager.DisplayName} ({CoreTools.Translate("Verbose")})");
        }
        // SelectedLogLevelIndex defaults to 0
    }

    public void LoadForManager(IPackageManager manager)
    {
        bool isDark = IsDark;
        bool verbose = LogLevelItems.Count > SelectedLogLevelIndex
            && (LogLevelItems[SelectedLogLevelIndex]?.Contains(CoreTools.Translate("Verbose")) ?? false);

        if (!verbose)
        {
            int idx = LogLevelItems.IndexOf(manager.DisplayName);
            if (idx >= 0)
                SelectedLogLevelIndex = idx;
        }

        LogLines.Clear();

        LogLines.Add(new LogLineItem(
            $"Manager {manager.DisplayName} with version:\n{manager.Status.Version}\n\n——————————————————————————————————————————\n",
            GetManagerColorBrush('0', isDark)));

        foreach (var operation in manager.TaskLogger.Operations)
        {
            var lines = operation.AsColoredString(verbose).ToList();
            if (lines.Count == 0) continue;

            var sb = new System.Text.StringBuilder();
            bool first = true;
            foreach (string line in lines)
            {
                if (line.Length == 0) continue;
                char colorCode = line[0];
                string text = line[1..];
                if (!first) sb.Append('\n');
                sb.Append(text);
                first = false;

                // Each colored segment is its own LogLineItem
                if (sb.Length > 0)
                {
                    LogLines.Add(new LogLineItem(sb.ToString(), GetManagerColorBrush(colorCode, isDark)));
                    sb.Clear();
                    first = true;
                }
            }
        }
    }

    public override void LoadLog(bool isReload = false)
    {
        int idx = SelectedLogLevelIndex;
        if (idx < 0 || idx >= LogLevelItems.Count) return;

        string selectedItem = LogLevelItems[idx];
        foreach (IPackageManager manager in PEInterface.Managers)
        {
            if (selectedItem.Contains(manager.DisplayName))
            {
                LoadForManager(manager);
                break;
            }
        }

        if (isReload)
            FireScrollToBottom();
    }
}
