using Avalonia.Controls;
using Avalonia.Interactivity;
using UniGetUI.Avalonia.ViewModels;

namespace UniGetUI.Avalonia.Views;

public partial class ManageDesktopShortcutsWindow : Window
{
    public ManageDesktopShortcutsWindow(System.Collections.Generic.IReadOnlyList<string>? shortcuts = null)
    {
        var vm = new ManageDesktopShortcutsViewModel(shortcuts);
        DataContext = vm;
        InitializeComponent();
        vm.CloseRequested += (_, _) => Close();
    }

    private void ResetYes_Click(object? sender, RoutedEventArgs e)
    {
        ((ManageDesktopShortcutsViewModel)DataContext!).ResetAllCommand.Execute(null);
        ResetButton.Flyout?.Hide();
    }

    private void ResetNo_Click(object? sender, RoutedEventArgs e) =>
        ResetButton.Flyout?.Hide();
}
