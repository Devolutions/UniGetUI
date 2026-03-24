using Avalonia.Controls;
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
}
