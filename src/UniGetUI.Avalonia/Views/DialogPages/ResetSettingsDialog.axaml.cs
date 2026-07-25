using Avalonia.Controls;
using Avalonia.Threading;

namespace UniGetUI.Avalonia.Views.DialogPages;

public partial class ResetSettingsDialog : UniGetUI.Avalonia.Views.DialogPages.ImmersiveDialog
{
    public bool Confirmed { get; private set; }

    public ResetSettingsDialog()
    {
        InitializeComponent();
        CancelButton.Click += (_, _) => Close();
        ResetButton.Click += (_, _) => { Confirmed = true; Close(); };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(() => CancelButton.Focus(), DispatcherPriority.Background);
    }
}
