using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class DriversPage : UserControl
{
    public DriversPage(DriversViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
