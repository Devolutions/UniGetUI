using Avalonia.Controls;
using Avalonia.Interactivity;
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

    private void ResetYes_Click(object? sender, RoutedEventArgs e)
    {
        ((ManageIgnoredUpdatesViewModel)DataContext!).ResetAllCommand.Execute(null);
        ResetButton.Flyout?.Hide();
    }

    private void ResetNo_Click(object? sender, RoutedEventArgs e) =>
        ResetButton.Flyout?.Hide();
}
