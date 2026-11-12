using Avalonia;
using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages;

namespace UniGetUI.Avalonia.Views.Controls;

public partial class XcodeAccountCard : UserControl
{
    public XcodeAccountCard() => InitializeComponent();

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (DataContext is XcodeAccountViewModel account)
            account.RefreshCommand.Execute(null);
    }
}
