using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using UniGetUI.Avalonia.Extensions;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.PackageLoader;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class SoftwareCatalogPage : UserControl, IEnterLeaveListener, ISearchBoxPage
{
    private const string FilterPaneWidthKey = "SoftwareCatalog";
    private const double DefaultFilterPaneWidth = 220;
    private readonly SoftwareCatalogViewModel _viewModel = new();
    private readonly DispatcherTimer _stateTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private InstalledPackagesLoader? _loader;
    private double _savedFilterPaneWidth = DefaultFilterPaneWidth;

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

        // Restore the per-page filter pane width from settings.
        var savedWidth = Settings.GetDictionaryItem<string, int>(Settings.K.SidepanelWidths, FilterPaneWidthKey);
        if (savedWidth >= 100) _savedFilterPaneWidth = savedWidth;
        _viewModel.TrackedFilterPaneWidth = _savedFilterPaneWidth;

        // Persist the dragged width and snap the pane closed when it is dragged below the minimum.
        FilteringPanel.ColumnDefinitions[0]
            .GetObservable(ColumnDefinition.WidthProperty)
            .SubscribeValue(width =>
            {
                if (!_viewModel.IsFilterPaneOpen) return;
                if (width.IsAbsolute && width.Value >= 100)
                {
                    _savedFilterPaneWidth = width.Value;
                    _viewModel.TrackedFilterPaneWidth = width.Value;
                    Settings.SetDictionaryItem(Settings.K.SidepanelWidths, FilterPaneWidthKey, (int)width.Value);
                }
                else if (width.IsAbsolute && width.Value < 100)
                {
                    _savedFilterPaneWidth = DefaultFilterPaneWidth;
                    _viewModel.TrackedFilterPaneWidth = DefaultFilterPaneWidth;
                    _viewModel.IsFilterPaneOpen = false;
                }
            });

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdateFilterPaneColumn(_viewModel.IsFilterPaneOpen);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SoftwareCatalogViewModel.IsFilterPaneOpen))
            UpdateFilterPaneColumn(_viewModel.IsFilterPaneOpen);
    }

    private void UpdateFilterPaneColumn(bool open)
    {
        if (FilteringPanel.ColumnDefinitions.Count < 2) return;
        FilteringPanel.ColumnDefinitions[0].Width = open ? new GridLength(_savedFilterPaneWidth) : new GridLength(0);
        FilteringPanel.ColumnDefinitions[1].Width = open ? new GridLength(12) : new GridLength(0);
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
