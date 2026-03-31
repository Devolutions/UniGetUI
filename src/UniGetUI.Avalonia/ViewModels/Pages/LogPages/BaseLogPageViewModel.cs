using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.ViewModels.Pages.LogPages;

public abstract partial class BaseLogPageViewModel : ViewModels.ViewModelBase
{
    public ObservableCollection<LogLineItem> LogLines { get; } = new();
    public ObservableCollection<string> LogLevelItems { get; } = new();

    [ObservableProperty]
    private bool _logLevelVisible;

    [ObservableProperty]
    private int _selectedLogLevelIndex;

    partial void OnSelectedLogLevelIndexChanged(int value) => LoadLog();

    // Events for view-layer operations (clipboard, file save, scroll)
    public event EventHandler<string>? CopyTextRequested;
    public event EventHandler<string>? ExportTextRequested;
    public event EventHandler? ScrollToBottomRequested;

    protected BaseLogPageViewModel(bool logLevelEnabled, int initialLogLevelIndex = 0)
    {
        LogLevelVisible = logLevelEnabled;
        _selectedLogLevelIndex = initialLogLevelIndex;
        if (logLevelEnabled)
            LoadLogLevels();
    }

    protected abstract void LoadLogLevels();
    public abstract void LoadLog(bool isReload = false);

    public void ClearLog() => LogLines.Clear();

    protected static bool IsDark =>
        Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    protected static IBrush GetSeverityBrush(LogEntry.SeverityLevel severity, bool isDark)
    {
        var color = severity switch
        {
            LogEntry.SeverityLevel.Debug => isDark ? Color.FromRgb(130, 130, 130) : Color.FromRgb(125, 125, 225),
            LogEntry.SeverityLevel.Info => isDark ? Color.FromRgb(190, 190, 190) : Color.FromRgb(50, 50, 150),
            LogEntry.SeverityLevel.Success => isDark ? Color.FromRgb(250, 250, 250) : Color.FromRgb(0, 0, 0),
            LogEntry.SeverityLevel.Warning => isDark ? Color.FromRgb(255, 255, 90) : Color.FromRgb(150, 150, 0),
            LogEntry.SeverityLevel.Error => isDark ? Color.FromRgb(255, 80, 80) : Color.FromRgb(205, 0, 0),
            _ => isDark ? Color.FromRgb(130, 130, 130) : Color.FromRgb(125, 125, 225),
        };
        return new SolidColorBrush(color);
    }

    protected static IBrush GetManagerColorBrush(char colorCode, bool isDark)
    {
        var color = colorCode switch
        {
            '0' => isDark ? Color.FromRgb(250, 250, 250) : Color.FromRgb(0, 0, 0),
            '1' => isDark ? Color.FromRgb(190, 190, 190) : Color.FromRgb(50, 50, 150),
            '2' => isDark ? Color.FromRgb(255, 80, 80) : Color.FromRgb(205, 0, 0),
            '3' => isDark ? Color.FromRgb(120, 120, 255) : Color.FromRgb(0, 0, 205),
            '4' => isDark ? Color.FromRgb(80, 255, 80) : Color.FromRgb(0, 205, 0),
            '5' => isDark ? Color.FromRgb(255, 255, 90) : Color.FromRgb(150, 150, 0),
            _ => isDark ? Color.FromRgb(255, 255, 90) : Color.FromRgb(150, 150, 0),
        };
        return new SolidColorBrush(color);
    }

    [RelayCommand]
    private void Copy()
    {
        var text = string.Join("\n", LogLines.Select(l => l.Text));
        CopyTextRequested?.Invoke(this, text);
    }

    [RelayCommand]
    private void Export()
    {
        var text = string.Join("\n", LogLines.Select(l => l.Text));
        ExportTextRequested?.Invoke(this, text);
    }

    [RelayCommand]
    private void Reload() => LoadLog(isReload: true);

    protected void FireScrollToBottom() => ScrollToBottomRequested?.Invoke(this, EventArgs.Empty);
}
