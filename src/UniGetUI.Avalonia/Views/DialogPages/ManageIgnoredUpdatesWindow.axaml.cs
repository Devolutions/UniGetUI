using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UniGetUI.Avalonia.ViewModels;

namespace UniGetUI.Avalonia.Views;

public partial class ManageIgnoredUpdatesWindow : UniGetUI.Avalonia.Views.DialogPages.ImmersiveDialog
{
    public ManageIgnoredUpdatesWindow()
    {
        var vm = new ManageIgnoredUpdatesViewModel();
        DataContext = vm;
        InitializeComponent();

        KeyDown += OnDialogKeyDown;
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(FocusInitialControl,
            DispatcherPriority.Background);
    }

    private void FocusInitialControl()
    {
        if (((ManageIgnoredUpdatesViewModel)DataContext!).HasEntries)
            IgnoredUpdatesGrid.Focus();
        else
            ResetButton.Focus();
    }

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void ResetYes_Click(object? sender, RoutedEventArgs e)
    {
        ((ManageIgnoredUpdatesViewModel)DataContext!).ResetAllCommand.Execute(null);
        ResetButton.Flyout?.Hide();
    }

    private void ResetNo_Click(object? sender, RoutedEventArgs e) =>
        ResetButton.Flyout?.Hide();
}
