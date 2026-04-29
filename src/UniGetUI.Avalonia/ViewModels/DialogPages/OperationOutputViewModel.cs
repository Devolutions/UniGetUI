using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using UniGetUI.Avalonia.ViewModels.Pages.LogPages;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.ViewModels.DialogPages;

public partial class OperationOutputViewModel : ObservableObject
{
    [ObservableProperty] private string _title = "";
    public ObservableCollection<LogLineItem> OutputLines { get; } = new();

    private static readonly IBrush _errorBrush = new SolidColorBrush(Color.Parse("#FF6B6B"));
    private static readonly IBrush _debugBrush = new SolidColorBrush(Color.Parse("#888888"));
    private static readonly IBrush _normalBrush = Brushes.White;

    public OperationOutputViewModel(AbstractOperation operation)
    {
        Title = operation.Metadata.Title;

        foreach (var (text, type) in operation.GetOutput())
            OutputLines.Add(MakeLine(text, type));

        operation.LogLineAdded += (_, ev) =>
            Dispatcher.UIThread.Post(() => OutputLines.Add(MakeLine(ev.Item1, ev.Item2)));
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
