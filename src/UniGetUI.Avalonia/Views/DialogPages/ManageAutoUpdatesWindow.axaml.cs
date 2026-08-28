using Avalonia.Input;
using Avalonia.Threading;
using UniGetUI.Avalonia.ViewModels;

namespace UniGetUI.Avalonia.Views;

public partial class ManageAutoUpdatesWindow : UniGetUI.Avalonia.Views.DialogPages.ImmersiveDialog
{
    public ManageAutoUpdatesWindow()
    {
        var vm = new ManageAutoUpdatesViewModel();
        DataContext = vm;
        InitializeComponent();

        KeyDown += OnDialogKeyDown;
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => SearchBox.Focus(),
            DispatcherPriority.Background);
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

}
