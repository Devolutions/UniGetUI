using Avalonia.Controls;
using Avalonia.Threading;

namespace UniGetUI.Avalonia.Views.DialogPages;

public partial class DiscardBundleChangesDialog : Window
{
    public bool Confirmed { get; private set; }

    public DiscardBundleChangesDialog()
    {
        InitializeComponent();
        CancelButton.Click += (_, _) => Close();
        DiscardButton.Click += (_, _) => { Confirmed = true; Close(); };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(() => DiscardButton.Focus(), DispatcherPriority.Background);
    }
}
