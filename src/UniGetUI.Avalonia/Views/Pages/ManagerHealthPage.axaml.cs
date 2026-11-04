using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class ManagerHealthPage : UserControl, IEnterLeaveListener, ISearchBoxPage
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

    public string QueryBackup { get; set; } = "";

    public string SearchBoxPlaceholder => CoreTools.Translate("Search package managers");

    public void SearchBox_QuerySubmitted(object? sender, EventArgs? e) { }

    public void ApplyQuery(string query) => _viewModel.SetFilter(query);
}
