using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Avalonia.Views.Pages;

/// <summary>
/// A single line in the colored log display.
/// </summary>
public sealed class LogLineModel
{
    public string Text { get; init; } = string.Empty;
    public IBrush Foreground { get; init; } = Brushes.White;
}

/// <summary>
/// Shell page hosting three log sub-views:
/// UniGetUI Application Log, Operation History, Manager Logs.
/// </summary>
public partial class LogsPageView : global::Avalonia.Controls.UserControl, IShellPage
{
    // IShellPage ──────────────────────────────────────────────────────────────
    public string Title { get; } = CoreTools.Translate("Package Manager logs");
    public string Subtitle { get; } = CoreTools.Translate("Package Manager logs");
    public bool SupportsSearch => false;
    public string SearchPlaceholder => string.Empty;
    public void UpdateSearchQuery(string query) { }

    // State ───────────────────────────────────────────────────────────────────
    private int _activeTab;     // 0 = UniGetUI, 1 = History, 2 = Managers
    private int _logLevel = 4;  // for UniGetUI log

    private readonly ObservableCollection<LogLineModel> _lines = [];

    // ── Color palettes ────────────────────────────────────────────────────────
    // Dark-theme colours
    private static IBrush DarkGrey => new SolidColorBrush(Color.FromRgb(130, 130, 130));
    private static IBrush DarkLightGrey => new SolidColorBrush(Color.FromRgb(190, 190, 190));
    private static IBrush DarkWhite => new SolidColorBrush(Color.FromRgb(250, 250, 250));
    private static IBrush DarkYellow => new SolidColorBrush(Color.FromRgb(255, 255, 90));
    private static IBrush DarkRed => new SolidColorBrush(Color.FromRgb(255, 80, 80));
    private static IBrush DarkGreen => new SolidColorBrush(Color.FromRgb(80, 255, 80));
    private static IBrush DarkBlue => new SolidColorBrush(Color.FromRgb(120, 120, 255));

    // Light-theme colours
    private static IBrush LightGrey => new SolidColorBrush(Color.FromRgb(125, 125, 225));
    private static IBrush LightLightGrey => new SolidColorBrush(Color.FromRgb(50, 50, 150));
    private static IBrush LightWhite => new SolidColorBrush(Color.FromRgb(0, 0, 0));
    private static IBrush LightYellow => new SolidColorBrush(Color.FromRgb(150, 150, 0));
    private static IBrush LightRed => new SolidColorBrush(Color.FromRgb(205, 0, 0));
    private static IBrush LightGreen => new SolidColorBrush(Color.FromRgb(0, 205, 0));
    private static IBrush LightBlue => new SolidColorBrush(Color.FromRgb(0, 0, 205));

    private bool IsDark => ActualThemeVariant == ThemeVariant.Dark;

    // ── Constructor ───────────────────────────────────────────────────────────

    public LogsPageView()
    {
        InitializeComponent();
        LogLinesItems.ItemsSource = _lines;
        PopulateLogLevelCombo();
        PopulateManagerCombo();
        // first load happens when the page is first shown (tab selection below)
        ApplyTranslations();
        SwitchTab(0);
    }

    private void ApplyTranslations()
    {
        TabUniGetUILogButton.Content = CoreTools.Translate("WingetUI log");
        TabOperationHistoryButton.Content = CoreTools.Translate("Operation history");
        TabManagerLogsButton.Content = CoreTools.Translate("Package Manager logs");
        CopyButton.Content = CoreTools.Translate("Copy");
        ExportButton.Content = CoreTools.Translate("Export");
        ReloadButton.Content = CoreTools.Translate("Reload");
    }

    // ── Combo population ──────────────────────────────────────────────────────

    private void PopulateLogLevelCombo()
    {
        LogLevelCombo.Items.Clear();
        LogLevelCombo.Items.Add(CoreTools.Translate("1 - Errors"));
        LogLevelCombo.Items.Add(CoreTools.Translate("2 - Warnings"));
        LogLevelCombo.Items.Add(CoreTools.Translate("3 - Information (less)"));
        LogLevelCombo.Items.Add(CoreTools.Translate("4 - Information (more)"));
        LogLevelCombo.Items.Add(CoreTools.Translate("5 - information (debug)"));
#if DEBUG
        LogLevelCombo.SelectedIndex = 4;
#else
        LogLevelCombo.SelectedIndex = 3;
#endif
        _logLevel = LogLevelCombo.SelectedIndex + 1;
    }

    private void PopulateManagerCombo()
    {
        ManagerCombo.Items.Clear();
        foreach (var mgr in PEInterface.Managers)
        {
            ManagerCombo.Items.Add(mgr.DisplayName);
            ManagerCombo.Items.Add($"{mgr.DisplayName} ({CoreTools.Translate("Verbose")})");
        }
        if (ManagerCombo.ItemCount > 0)
            ManagerCombo.SelectedIndex = 0;
    }

    // ── Tab switching ─────────────────────────────────────────────────────────

    private void SwitchTab(int tab)
    {
        _activeTab = tab;

        // Update tab button styles
        TabUniGetUILogButton.Classes.Set("accent", tab == 0);
        TabUniGetUILogButton.Classes.Set("toolbar-primary", tab == 0);
        TabUniGetUILogButton.Classes.Set("toolbar-secondary", tab != 0);
        TabOperationHistoryButton.Classes.Set("accent", tab == 1);
        TabOperationHistoryButton.Classes.Set("toolbar-primary", tab == 1);
        TabOperationHistoryButton.Classes.Set("toolbar-secondary", tab != 1);
        TabManagerLogsButton.Classes.Set("accent", tab == 2);
        TabManagerLogsButton.Classes.Set("toolbar-primary", tab == 2);
        TabManagerLogsButton.Classes.Set("toolbar-secondary", tab != 2);

        // Show / hide toolbar controls
        LogLevelCombo.IsVisible = tab == 0;
        ManagerCombo.IsVisible = tab == 2;

        // Show / hide content controls
        LogLinesItems.IsVisible = tab != 1;
        PlainTextBlock.IsVisible = tab == 1;

        Reload();
    }

    private void Reload()
    {
        switch (_activeTab)
        {
            case 0: LoadUniGetUILog(); break;
            case 1: LoadOperationHistory(); break;
            case 2: LoadManagerLogs(); break;
        }
    }

    internal void ShowManagerLogs(IPackageManager manager, bool verbose = false)
    {
        string targetLabel = verbose
            ? $"{manager.DisplayName} ({CoreTools.Translate("Verbose")})"
            : manager.DisplayName;

        for (int index = 0; index < ManagerCombo.ItemCount; index++)
        {
            if (string.Equals(ManagerCombo.Items[index]?.ToString(), targetLabel, StringComparison.Ordinal))
            {
                ManagerCombo.SelectedIndex = index;
                break;
            }
        }

        SwitchTab(2);
    }

    // ── Log loaders ───────────────────────────────────────────────────────────

    private void LoadUniGetUILog()
    {
        _lines.Clear();
        bool dark = IsDark;

        foreach (var entry in Logger.GetLogs())
        {
            if (string.IsNullOrEmpty(entry.Content)) continue;

            // Level filtering
            if (_logLevel <= 4 && entry.Severity == LogEntry.SeverityLevel.Debug) continue;
            if (_logLevel <= 3 && entry.Severity == LogEntry.SeverityLevel.Info) continue;
            if (_logLevel <= 2 && entry.Severity == LogEntry.SeverityLevel.Success) continue;
            if (_logLevel <= 1 && entry.Severity == LogEntry.SeverityLevel.Warning) continue;

            IBrush brush = entry.Severity switch
            {
                LogEntry.SeverityLevel.Debug => dark ? DarkGrey : LightGrey,
                LogEntry.SeverityLevel.Info => dark ? DarkLightGrey : LightLightGrey,
                LogEntry.SeverityLevel.Success => dark ? DarkWhite : LightWhite,
                LogEntry.SeverityLevel.Warning => dark ? DarkYellow : LightYellow,
                LogEntry.SeverityLevel.Error => dark ? DarkRed : LightRed,
                _ => dark ? DarkGrey : LightGrey,
            };

            string prefix = $"[{entry.Time:HH:mm:ss}] ";
            string[] parts = entry.Content.Split('\n');
            for (int i = 0; i < parts.Length; i++)
            {
                string line = i == 0 ? prefix + parts[i] : new string(' ', prefix.Length) + parts[i];
                if (string.IsNullOrWhiteSpace(line)) continue;
                _lines.Add(new LogLineModel { Text = line, Foreground = brush });
            }
        }
    }

    private void LoadOperationHistory()
    {
        var raw = Settings.GetValue(Settings.K.OperationHistory);
        var sb = new System.Text.StringBuilder();
        foreach (var line in raw.Split('\n'))
        {
            var trimmed = line.Replace("\r", "").Trim();
            if (trimmed.Length > 0)
                sb.AppendLine(trimmed);
        }
        PlainTextBlock.Text = sb.ToString().TrimEnd();
    }

    private void LoadManagerLogs()
    {
        _lines.Clear();
        bool dark = IsDark;

        if (ManagerCombo.SelectedIndex < 0) return;

        var selectedText = ManagerCombo.SelectedItem?.ToString() ?? string.Empty;
        bool verbose = selectedText.Contains(CoreTools.Translate("Verbose"));

        // Find matching manager
        IPackageManager? manager = null;
        foreach (var mgr in PEInterface.Managers)
        {
            if (selectedText.Contains(mgr.DisplayName))
            {
                manager = mgr;
                break;
            }
        }
        if (manager is null) return;

        // Version header
        _lines.Add(new LogLineModel
        {
            Text = $"Manager {manager.DisplayName}  —  {manager.Status.Version}",
            Foreground = dark ? DarkYellow : LightYellow,
        });
        _lines.Add(new LogLineModel { Text = new string('─', 60), Foreground = dark ? DarkGrey : LightGrey });

        foreach (var operation in manager.TaskLogger.Operations)
        {
            foreach (var line in operation.AsColoredString(verbose))
            {
                if (line.Length < 2) continue;

                IBrush brush = line[0] switch
                {
                    '0' => dark ? DarkWhite : LightWhite,
                    '1' => dark ? DarkLightGrey : LightLightGrey,
                    '2' => dark ? DarkRed : LightRed,
                    '3' => dark ? DarkBlue : LightBlue,
                    '4' => dark ? DarkGreen : LightGreen,
                    '5' => dark ? DarkYellow : LightYellow,
                    _ => dark ? DarkYellow : LightYellow,
                };
                _lines.Add(new LogLineModel { Text = line[1..], Foreground = brush });
            }
            // Separator between operations
            _lines.Add(new LogLineModel { Text = string.Empty, Foreground = dark ? DarkGrey : LightGrey });
        }
    }

    // ── Build clipboard / export text ─────────────────────────────────────────

    private string BuildAllText()
    {
        if (_activeTab == 1)
            return PlainTextBlock.Text ?? string.Empty;

        var sb = new System.Text.StringBuilder();
        foreach (var line in _lines)
            sb.AppendLine(line.Text);
        return sb.ToString();
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void TabButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && int.TryParse(tag, out int tab))
            SwitchTab(tab);
    }

    private void LogLevelCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        _logLevel = LogLevelCombo.SelectedIndex + 1;
        if (_activeTab == 0)
            LoadUniGetUILog();
    }

    private void ManagerCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_activeTab == 2)
            LoadManagerLogs();
    }

    private void ReloadButton_OnClick(object? sender, RoutedEventArgs e) => Reload();

    private async void CopyButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(BuildAllText());
    }

    private async void ExportButton_OnClick(object? sender, RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = CoreTools.Translate("Export log as a file"),
            SuggestedFileName = CoreTools.Translate("WingetUI log"),
            FileTypeChoices =
            [
                new FilePickerFileType(CoreTools.Translate("Text file")) { Patterns = ["*.txt"] },
            ],
        });

        if (file is not null)
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new System.IO.StreamWriter(stream, System.Text.Encoding.UTF8);
            await writer.WriteAsync(BuildAllText());
        }
    }
}
