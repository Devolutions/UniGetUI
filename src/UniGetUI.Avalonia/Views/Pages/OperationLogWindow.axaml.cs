using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Views.Pages;

/// <summary>
/// Scrollable live-log window for a running or finished <see cref="AbstractOperation"/>.
/// Replays already-captured output on open and subscribes to <see cref="AbstractOperation.LogLineAdded"/>
/// for live updates while the window is visible.
/// </summary>
public partial class OperationLogWindow : Window
{
    private readonly AbstractOperation _operation;
    private bool _lastLineWasProgress;

    private ScrollViewer LogScroll => GetControl<ScrollViewer>("LogScrollViewer");
    private StackPanel LogPanel => GetControl<StackPanel>("LogLinesPanel");
    private TextBlock StatusFooter => GetControl<TextBlock>("StatusFooterBlock");

    // Mono-space font stack used for log output
    private static readonly FontFamily MonoFont =
        new("Cascadia Mono,Cascadia Code,Consolas,Courier New,monospace");

    public OperationLogWindow()
    {
        _operation = null!;
        InitializeComponent();
        IsEnabled = false;
    }

    public OperationLogWindow(AbstractOperation operation)
    {
        _operation = operation;
        Title = CoreTools.Translate("View {0} logs", operation.Metadata.Title);

        InitializeComponent();

        // Replay lines captured before the window was opened
        foreach (var (text, type) in operation.GetOutput())
        {
            AddLine(text, type);
        }

        UpdateFooter(operation.Status);

        // Subscribe for live output and status changes
        operation.LogLineAdded += OnLogLineAdded;
        operation.StatusChanged += OnStatusChanged;

        GetControl<Button>("CloseButton").Content = CoreTools.Translate("Close");

        Closed += (_, _) =>
        {
            operation.LogLineAdded -= OnLogLineAdded;
            operation.StatusChanged -= OnStatusChanged;
        };
    }

    // ── Event handlers ────────────────────────────────────────────────────────

    private void OnLogLineAdded(object? sender, (string, AbstractOperation.LineType) line) =>
        Dispatcher.UIThread.Post(() => AddLine(line.Item1, line.Item2));

    private void OnStatusChanged(object? sender, OperationStatus status) =>
        Dispatcher.UIThread.Post(() => UpdateFooter(status));

    private void CloseButton_OnClick(object? sender, RoutedEventArgs e) => Close();

    // ── Log rendering ─────────────────────────────────────────────────────────

    private void AddLine(string text, AbstractOperation.LineType type)
    {
        // ProgressIndicator lines replace the previous progress line (spinner-style)
        if (type == AbstractOperation.LineType.ProgressIndicator)
        {
            if (_lastLineWasProgress && LogPanel.Children.Count > 0)
                LogPanel.Children.RemoveAt(LogPanel.Children.Count - 1);
            _lastLineWasProgress = true;
        }
        else
        {
            _lastLineWasProgress = false;
        }

        var tb = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = MonoFont,
            FontSize = 12,
        };

        switch (type)
        {
            case AbstractOperation.LineType.Error:
                tb.Foreground = new SolidColorBrush(Color.FromRgb(220, 60, 60));
                break;
            case AbstractOperation.LineType.VerboseDetails:
                tb.Opacity = 0.52;
                break;
        }

        LogPanel.Children.Add(tb);
        LogScroll.ScrollToEnd();
    }

    private void UpdateFooter(OperationStatus status)
    {
        StatusFooter.Text = status switch
        {
            OperationStatus.InQueue => CoreTools.Translate("This package is on the queue"),
            OperationStatus.Running => CoreTools.Translate("Operation in progress"),
            OperationStatus.Succeeded => CoreTools.Translate("Success!"),
            OperationStatus.Failed => CoreTools.Translate("Failed"),
            OperationStatus.Canceled => CoreTools.Translate("Operation cancelled"),
            _ => string.Empty,
        };
    }

    // ── Boilerplate ───────────────────────────────────────────────────────────

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private T GetControl<T>(string name)
        where T : Control
        => this.FindControl<T>(name) ?? throw new InvalidOperationException($"Control '{name}' not found");
}
