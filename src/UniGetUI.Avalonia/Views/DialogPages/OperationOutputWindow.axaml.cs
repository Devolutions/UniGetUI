using System.Collections.Specialized;
using Avalonia.Controls;
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
        UniGetUI.Avalonia.Infrastructure.MicaWindowHelper.Apply(this);

        OutputText.SetLines(vm.OutputLines);
        vm.OutputLines.CollectionChanged += OnOutputLinesChanged;
    }

    private void OnOutputLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            bool wasAtBottom = OutputText.IsScrolledToBottom;
            if (e.Action == NotifyCollectionChangedAction.Reset)
            {
                OutputText.ClearLines();
            }
            else if (e.Action == NotifyCollectionChangedAction.Replace && e.NewItems is not null)
            {
                // A progress indicator repainting in place: overwrite the last rendered line.
                foreach (LogLineItem item in e.NewItems)
                    OutputText.ReplaceLastLine(item);
            }
            else if (e.NewItems is not null)
            {
                foreach (LogLineItem item in e.NewItems)
                    OutputText.AppendLine(item);
            }
            // Only follow new output if the user hadn't scrolled up to read earlier lines.
            if (wasAtBottom)
                OutputText.ScrollToBottom();
        }, DispatcherPriority.Background);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        OutputText.ScrollToBottom();
    }
}
