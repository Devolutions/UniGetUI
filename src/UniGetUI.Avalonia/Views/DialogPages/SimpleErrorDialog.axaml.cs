using Avalonia.Controls;
using Avalonia.Threading;

namespace UniGetUI.Avalonia.Views.DialogPages;

public partial class SimpleErrorDialog : Window
{
    public SimpleErrorDialog(string title, string message)
    {
        InitializeComponent();
        UniGetUI.Avalonia.Infrastructure.MicaWindowHelper.Apply(this);
        Title = title;
        TitleBlock.Text = title;
        MessageBlock.Text = message;
        OkButton.Click += (_, _) => Close();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(() => OkButton.Focus(), DispatcherPriority.Background);
    }
}
