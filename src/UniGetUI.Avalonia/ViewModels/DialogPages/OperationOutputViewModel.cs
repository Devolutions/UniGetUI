using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using UniGetUI.Avalonia.ViewModels.Pages.LogPages;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.ViewModels.DialogPages;

public partial class OperationOutputViewModel : ObservableObject
{
    [ObservableProperty] private string _title = "";
    public ObservableCollection<LogLineItem> OutputLines { get; } = new();

    private IBrush _errorBrush = Brushes.Transparent;
    private IBrush _debugBrush = Brushes.Transparent;
    private IBrush _normalBrush = Brushes.Transparent;

    private readonly AbstractOperation _operation;
    private ProgressLineCollapser _collapser = new();

    public OperationOutputViewModel(AbstractOperation operation)
    {
        _operation = operation;
        Title = operation.Metadata.Title;

        Rebuild();

        operation.LogLineAdded += (_, ev) =>
            Dispatcher.UIThread.Post(() => AddOrCollapseLine(ev.Item1, ev.Item2));
    }

    // Rebuilds every line with brushes for the current theme; called on construction and whenever
    // the theme changes, so already-rendered output follows a live light/dark switch.
    public void Rebuild()
    {
        var theme = Infrastructure.ThemeHelper.Variant;
        _errorBrush = LookupBrush("StatusErrorForeground", theme, new SolidColorBrush(Color.Parse("#c62828")));
        _debugBrush = LookupBrush("LogOutputVerboseForeground", theme, new SolidColorBrush(Color.Parse("#767676")));
        _normalBrush = LookupBrush("SystemControlForegroundBaseHighBrush", theme, Brushes.White);

        OutputLines.Clear();
        _collapser = new();
        foreach (var (text, type) in _operation.GetOutput())
            AddOrCollapseLine(text, type);
    }

    // Progress indicators (carriage-return redraws like installer spinners) overwrite the previous
    // line instead of stacking up, mirroring how a terminal repaints in place. The first
    // non-progress line that follows settles into that same line.
    private void AddOrCollapseLine(string text, AbstractOperation.LineType type)
    {
        LogLineItem item = MakeLine(text, type);
        if (_collapser.Next(type) is ProgressLineCollapser.Fold.ReplaceLast && OutputLines.Count > 0)
            OutputLines[^1] = item;
        else
            OutputLines.Add(item);
    }

    private static IBrush LookupBrush(string key, ThemeVariant theme, IBrush fallback)
    {
        if (Application.Current?.TryGetResource(key, theme, out var resource) == true && resource is IBrush brush)
            return brush;
        return fallback;
    }

    private LogLineItem MakeLine(string text, AbstractOperation.LineType type)
    {
        IBrush brush = type switch
        {
            AbstractOperation.LineType.Error => _errorBrush,
            AbstractOperation.LineType.VerboseDetails => _debugBrush,
            _ => _normalBrush,
        };
        return new LogLineItem(text, brush);
    }
}
