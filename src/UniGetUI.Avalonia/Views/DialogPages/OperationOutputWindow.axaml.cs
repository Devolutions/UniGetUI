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
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        OutputScroll.ScrollToEnd();
        Dispatcher.UIThread.Post(() => OutputTextBox.Focus(), DispatcherPriority.Background);
    }
}
