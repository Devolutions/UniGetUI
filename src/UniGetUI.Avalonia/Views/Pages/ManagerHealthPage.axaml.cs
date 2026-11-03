using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class ManagerHealthPage : UserControl, IEnterLeaveListener
{
    private readonly ManagerHealthViewModel _viewModel = new();

    public ManagerHealthPage()
    {
        InitializeComponent();
        DataContext = _viewModel;
    }

    public void OnEnter()
    {
        _viewModel.IsActive = true;
        _viewModel.Refresh();
    }

    public void OnLeave() => _viewModel.IsActive = false;
}
