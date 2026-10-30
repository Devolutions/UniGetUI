using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Avalonia.Views.Pages.SettingsPages;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class AgentSkillsPage : UserControl
{
    private readonly AgentSkillsViewModel _viewModel;
    public AgentSkillsPage(AgentSkillsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        var sources = new AgentSkillsSourcesControl(viewModel.Service);
        sources.SourcesChanged += async (_, _) => await viewModel.OnSourcesChangedAsync();
        SourcesHost.Content = sources;
        SizeChanged += (_, _) => ArrangeDetails();
    }

    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        await _viewModel.LoadCurrentModeAsync();
        ArrangeDetails();
    }

    private async void ModeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender != e.Source || _viewModel is null) return;
        await _viewModel.LoadCurrentModeAsync();
    }

    private async void SearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (!_viewModel.IsBusy && _viewModel.IsDiscover) await _viewModel.RefreshCommand.ExecuteAsync(null);
    }

    private async void SourceInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await _viewModel.AddFromSourceCommand.ExecuteAsync(null);
    }

    private void ManageSourcesClicked(object? sender, RoutedEventArgs e)
    {
        if (_viewModel.ShowSources) FocusSourceEditor();
    }

    private void FocusSourceEditor() => Dispatcher.UIThread.Post(() =>
    {
        if (_viewModel.ShowSources && SourcesHost.Content is AgentSkillsSourcesControl sources)
            sources.FocusAddress();
    });

    private void CloseSources(object? sender, RoutedEventArgs e)
    {
        _viewModel.ShowSources = false;
        ManageSourcesButton.Focus();
    }

    private void OpenSettings(object? sender, RoutedEventArgs e) => NavigateToSettings();
    private void OpenDiscover(object? sender, RoutedEventArgs e) => _viewModel.Mode = 0;
    private void OpenDiscoverySettings(object? sender, RoutedEventArgs e) => NavigateToSettings("SkillsShDiscoveryToggle");
    private static void NavigateToSettings(string? anchor = null)
    {
        if (MainWindow.Instance?.DataContext is MainWindowViewModel window)
            window.OpenSettingsPage(typeof(AgentSkillsSettingsControl), anchor);
    }

    private void ArrangeDetails()
    {
        bool narrow = Bounds.Width < 950;
        Grid.SetColumn(DetailsHost, narrow ? 0 : 1);
        Grid.SetRow(DetailsHost, narrow ? 1 : 0);
        DetailsHost.Width = narrow ? double.NaN : 340;
        DetailsHost.MaxHeight = narrow ? 250 : double.PositiveInfinity;
    }
}
