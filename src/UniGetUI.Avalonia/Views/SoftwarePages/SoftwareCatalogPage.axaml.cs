using Avalonia.Controls;
using Avalonia.Threading;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class SoftwareCatalogPage : UserControl, IEnterLeaveListener, ISearchBoxPage
{
    private readonly SoftwareCatalogViewModel _viewModel = new();
    private readonly DispatcherTimer _stateTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private InstalledPackagesLoader? _loader;

    public string QueryBackup
    {
        get => _viewModel.Query;
        set => _viewModel.Query = value;
    }

    public string SearchBoxPlaceholder => CoreTools.Translate("Search for packages");
    public void ApplyQuery(string query) => _viewModel.Query = query;
    public void SearchBox_QuerySubmitted(object? sender, EventArgs? e) { }

    public SoftwareCatalogPage()
    {
        InitializeComponent();
        DataContext = _viewModel;
        _stateTimer.Tick += (_, _) => _viewModel.UpdateStates();
    }

    public void OnEnter()
    {
        _loader = InstalledPackagesLoader.Instance;
        _loader?.FinishedLoading += OnInventoryLoaded;
        _stateTimer.Start();
        _ = _viewModel.RefreshAsync();
    }

    public void OnLeave()
    {
        _stateTimer.Stop();
        _loader?.FinishedLoading -= OnInventoryLoaded;
        _loader = null;
    }

    private void OnInventoryLoaded(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(() => _ = _viewModel.RefreshAsync());
}
