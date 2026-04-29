using Avalonia.Controls;
using Avalonia.Threading;
using UniGetUI.Avalonia.ViewModels.DialogPages;
using UniGetUI.PackageOperations;

namespace UniGetUI.Avalonia.Views.DialogPages;

public partial class OperationOutputWindow : Window
{
    public OperationOutputWindow(AbstractOperation operation)
    {
        DataContext = new OperationOutputViewModel(operation);
        InitializeComponent();

        ((OperationOutputViewModel)DataContext).OutputLines.CollectionChanged +=
            (_, _) => Dispatcher.UIThread.Post(OutputScroll.ScrollToEnd, DispatcherPriority.Background);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        OutputScroll.ScrollToEnd();
    }
}
