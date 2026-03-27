using Avalonia.Media;
using UniGetUI.Core.SettingsEngine;

namespace UniGetUI.Avalonia.ViewModels.Pages.LogPages;

public class OperationHistoryPageViewModel : BaseLogPageViewModel
{
    public OperationHistoryPageViewModel() : base(false)
    {
        LoadLog();
    }

    protected override void LoadLogLevels() { }

    public override void LoadLog(bool isReload = false)
    {
        bool isDark = IsDark;
        var defaultBrush = new SolidColorBrush(isDark ? Color.FromRgb(250, 250, 250) : Color.FromRgb(0, 0, 0));

        LogLines.Clear();

        foreach (string line in Settings.GetValue(Settings.K.OperationHistory).Split("\n"))
        {
            string trimmed = line.Replace("\r", "").Replace("\n", "").Trim();
            if (trimmed == "") continue;
            LogLines.Add(new LogLineItem(trimmed, defaultBrush));
        }

        if (isReload)
            FireScrollToBottom();
    }
}
