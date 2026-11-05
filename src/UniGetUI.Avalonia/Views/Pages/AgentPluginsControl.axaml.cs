using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Core.Tools;
using UniGetUI.Extensions.AgentSkills;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class AgentPluginsControl : UserControl
{
    private Vector _listOffset;
    private AgentPluginsViewModel? _viewModel;
    public AgentPluginsControl()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateWorkspace();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Left && e.KeyModifiers.HasFlag(KeyModifiers.Alt)
                && _viewModel is { ShowLibrary: false, CanWork: true })
            {
                e.Handled = true;
                _viewModel.BackCommand.Execute(null);
            }
        };
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _viewModel = DataContext as AgentPluginsViewModel;
        _viewModel?.PropertyChanged += ViewModelChanged;
        UpdateWorkspace();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _viewModel?.PropertyChanged -= ViewModelChanged;
        _viewModel = null;
        base.OnDetachedFromVisualTree(e);
    }
    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AgentPluginsViewModel.AddMode))
            Dispatcher.UIThread.Post(() =>
            {
                if (_viewModel?.AddingMarketplace == true) MarketplaceInput.Focus();
                else if (_viewModel?.AddingPlugin == true) PluginInput.Focus();
            });
        if (e.PropertyName != nameof(AgentPluginsViewModel.Page)) return;
        if (PluginLibrary.IsEffectivelyVisible)
            _listOffset = PluginList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.Offset ?? default;
        UpdateWorkspace();
        if (_viewModel is { ShowLibrary: false })
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (_viewModel?.ShowMarketplaces == true) MarketplacesBackButton.Focus();
                else PluginBackButton.Focus();
            });
        }
        else Dispatcher.UIThread.Post(() =>
        {
            var scroll = PluginList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            scroll?.Offset = _listOffset;
            if (_viewModel?.SelectedPlugin is { } selected)
                PluginList.ContainerFromItem(selected)?.GetVisualDescendants().OfType<Button>().FirstOrDefault()?.Focus();
            else PluginSearch.Focus();
        }, DispatcherPriority.Loaded);
    }
    private void UpdateWorkspace()
    {
        bool sideBySide = Bounds.Width >= 1000 && _viewModel?.ShowDetails == true;
        bool showLibrary = _viewModel?.ShowLibrary == true || sideBySide;
        bool listHadFocus = PluginLibrary.IsKeyboardFocusWithin;
        if (PluginLibrary.IsEffectivelyVisible && !showLibrary)
            _listOffset = PluginList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.Offset ?? default;
        PluginWorkspace.ColumnDefinitions[1].Width = new GridLength(sideBySide ? 360 : 0);
        PluginWorkspace.ColumnSpacing = sideBySide ? 16 : 0;
        Grid.SetColumn(PluginDetailsPane, sideBySide ? 1 : 0);
        PluginLibrary.IsVisible = showLibrary;
        PluginBackButton.Content = sideBySide ? CoreTools.Translate("Close") : CoreTools.Translate("Back to plugins");
        if (listHadFocus && !showLibrary && _viewModel?.ShowDetails == true)
            Dispatcher.UIThread.Post(() => PluginBackButton.Focus());
    }
    private async void PluginEnabledClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { DataContext: AgentPluginItem item } toggle) return;
        // Keep the switch authoritative while confirmation or the agent command is pending.
        toggle.SetCurrentValue(ToggleSwitch.IsCheckedProperty, item.Enabled);
        if (_viewModel is { } viewModel)
            await viewModel.ToggleItemCommand.ExecuteAsync(item);
    }
    private void PluginListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.Source is Visual visual
            && (visual is Button || visual.GetVisualAncestors().OfType<Button>().Any())) return;
        e.Handled = true;
        _viewModel?.OpenDetailsCommand.Execute(_viewModel.SelectedPlugin);
    }
    private async void MarketplaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _viewModel is not { CanWork: true }) return;
        e.Handled = true;
        await _viewModel.AddMarketplaceCommand.ExecuteAsync(null);
    }
    private async void IdentifierKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || _viewModel is not { CanWork: true }) return;
        e.Handled = true;
        await _viewModel.InstallByIdCommand.ExecuteAsync(null);
    }
    private async void ProviderChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender == e.Source && IsEffectivelyVisible && TopLevel.GetTopLevel(this) is not null
            && DataContext is AgentPluginsViewModel viewModel)
            await viewModel.LoadAsync();
    }
}
