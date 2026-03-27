using UniGetUI.Avalonia.ViewModels.Pages.LogPages;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Avalonia.Views.Pages;

public class ManagerLogsPage : LogPages.BaseLogPage
{
    private readonly ManagerLogsPageViewModel _viewModel;

    public ManagerLogsPage() : this(new ManagerLogsPageViewModel()) { }

    private ManagerLogsPage(ManagerLogsPageViewModel vm) : base(vm)
    {
        _viewModel = vm;
    }

    public void LoadForManager(IPackageManager manager) => _viewModel.LoadForManager(manager);
}
