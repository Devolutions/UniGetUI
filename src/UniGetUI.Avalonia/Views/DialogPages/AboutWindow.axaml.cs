using Avalonia.Controls;
using Avalonia.Threading;

namespace UniGetUI.Avalonia.Views.DialogPages;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(() => MainTabControl.Focus(), DispatcherPriority.Background);
    }
}
