using Avalonia.Controls;
using Avalonia.Threading;

namespace UniGetUI.Avalonia.Views.DialogPages;

public partial class IntegrityViolationDialog : UniGetUI.Avalonia.Views.DialogPages.ImmersiveDialog
{
    public IntegrityViolationDialog()
    {
        InitializeComponent();
        CloseButton.Click += (_, _) => Close();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(() => CloseButton.Focus(), DispatcherPriority.Background);
    }
}
