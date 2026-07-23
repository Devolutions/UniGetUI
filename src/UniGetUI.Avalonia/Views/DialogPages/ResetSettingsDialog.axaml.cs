using Avalonia.Controls;
using Avalonia.Threading;

namespace UniGetUI.Avalonia.Views.DialogPages;

public partial class ResetSettingsDialog : Window
{
    public bool Confirmed { get; private set; }

    public ResetSettingsDialog()
    {
        InitializeComponent();
        UniGetUI.Avalonia.Infrastructure.MicaWindowHelper.Apply(this);
        CancelButton.Click += (_, _) => Close();
        ResetButton.Click += (_, _) => { Confirmed = true; Close(); };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(() => CancelButton.Focus(), DispatcherPriority.Background);
    }
}
