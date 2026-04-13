using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UniGetUI.Avalonia.ViewModels;

namespace UniGetUI.Avalonia.Views;

public partial class ManageIgnoredUpdatesWindow : Window
{
    public ManageIgnoredUpdatesWindow()
    {
        var vm = new ManageIgnoredUpdatesViewModel();
        DataContext = vm;
        InitializeComponent();
        vm.CloseRequested += (_, _) => Close();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        Dispatcher.UIThread.Post(() =>
        {
            if (IgnoredUpdatesGrid.IsVisible)
                IgnoredUpdatesGrid.Focus();
            else
                ResetButton.Focus();
        }, DispatcherPriority.Background);
    }

    private void ResetYes_Click(object? sender, RoutedEventArgs e)
    {
        ((ManageIgnoredUpdatesViewModel)DataContext!).ResetAllCommand.Execute(null);
        ResetButton.Flyout?.Hide();
    }

    private void ResetNo_Click(object? sender, RoutedEventArgs e) =>
        ResetButton.Flyout?.Hide();
}
