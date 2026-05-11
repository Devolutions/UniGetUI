using System.Collections.Specialized;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Threading;
using UniGetUI.Avalonia.ViewModels.DialogPages;
using UniGetUI.Avalonia.ViewModels.Pages.LogPages;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Views.DialogPages;

public partial class OperationOutputWindow : Window
{
    public OperationOutputWindow(AbstractOperation operation)
    {
        var vm = new OperationOutputViewModel(operation);
        DataContext = vm;
        InitializeComponent();

        foreach (var line in vm.OutputLines)
            AppendLine(line);

        vm.OutputLines.CollectionChanged += OnOutputLinesChanged;
    }

    private void OnOutputLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                OutputText.Inlines?.Clear();
            }
            else if (e.NewItems is not null)
            {
                foreach (LogLineItem item in e.NewItems)
                    AppendLine(item);
            }
            OutputScroll.ScrollToEnd();
        }, DispatcherPriority.Background);
    }

    private void AppendLine(LogLineItem line)
    {
        var inlines = OutputText.Inlines ??= new InlineCollection();
        if (inlines.Count > 0)
            inlines.Add(new LineBreak());
        inlines.Add(new Run(line.Text) { Foreground = line.Foreground });
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        OutputScroll.ScrollToEnd();
    }
}
