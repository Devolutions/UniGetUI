using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Avalonia.Views.DialogPages;
using UniGetUI.Avalonia.Views.Pages.SettingsPages;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages;

public partial class AgentSkillsPage : UserControl
{
    private readonly AgentSkillsViewModel _viewModel;
    private Vector _listOffset;
    private bool _isAttached;

    public AgentSkillsPage(AgentSkillsViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        var sources = new AgentSkillsSourcesControl(viewModel.Service);
        sources.SourcesChanged += async (_, _) => await viewModel.OnSourcesChangedAsync();
        SourcesHost.Content = sources;
        SizeChanged += (_, _) => UpdateWorkspace();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Left && e.KeyModifiers.HasFlag(KeyModifiers.Alt)
                && _viewModel.ShowDetails && _viewModel.CanNavigate)
            {
                e.Handled = true;
                _viewModel.BackCommand.Execute(null);
            }
        };
    }

    protected override async void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        _viewModel.PropertyChanged += ViewModelChanged;
        UpdateWorkspace();
        await _viewModel.LoadLibraryViewAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        _viewModel.PropertyChanged -= ViewModelChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void ViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AgentSkillsViewModel.AddSourceExpanded) && _viewModel.AddSourceExpanded)
            Dispatcher.UIThread.Post(() => SourceInput.Focus());
        if (e.PropertyName == nameof(AgentPluginsViewModel.AddMode))
            Dispatcher.UIThread.Post(() =>
            {
                if (_viewModel.AgentPlugins.AddingMarketplace)
                {
                    if (SourceMarketplaceInput.IsEffectivelyVisible) SourceMarketplaceInput.Focus();
                    else MarketplaceInput.Focus();
                }
                else if (_viewModel.AgentPlugins.AddingPlugin) PluginInput.Focus();
            });
        if (e.PropertyName is nameof(AgentSkillsViewModel.ShowDetails) or nameof(AgentSkillsViewModel.LibraryView))
            UpdateWorkspace();
        if (e.PropertyName != nameof(AgentSkillsViewModel.ShowDetails)) return;
        if (_viewModel.ShowDetails)
        {
            _listOffset = LibraryList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()?.Offset ?? default;
            Dispatcher.UIThread.Post(() => BackButton.Focus());
        }
        else Dispatcher.UIThread.Post(() =>
        {
            var scroll = LibraryList.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            scroll?.Offset = _listOffset;
            if (_viewModel.SelectedItem is { } selected)
            {
                LibraryList.ScrollIntoView(selected);
                LibraryList.ContainerFromItem(selected)?.GetVisualDescendants().OfType<Button>().FirstOrDefault()?.Focus();
            }
            else LibrarySearch.Focus();
        }, DispatcherPriority.Loaded);
    }

    private void UpdateWorkspace()
    {
        bool sideBySide = Bounds.Width >= 1000 && _viewModel.ShowDetails && _viewModel.IsLibraryView;
        bool showLibrary = _viewModel.IsLibraryView && (!_viewModel.ShowDetails || sideBySide);
        LibraryWorkspace.ColumnDefinitions[1].Width = new GridLength(sideBySide ? 420 : 0);
        LibraryWorkspace.ColumnSpacing = sideBySide ? 16 : 0;
        Grid.SetColumn(DetailsPane, sideBySide ? 1 : 0);
        LibraryListPane.IsVisible = showLibrary;
        DetailsPane.IsVisible = _viewModel.ShowDetails && _viewModel.IsLibraryView;
        SourcesWorkspace.ColumnDefinitions[0].Width = _viewModel.ShowSkillSources
            ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SourcesWorkspace.ColumnDefinitions[1].Width = _viewModel.ShowPluginMarketplaces
            ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        SourcesWorkspace.ColumnSpacing = _viewModel.ShowSkillSources && _viewModel.ShowPluginMarketplaces ? 16 : 0;
    }

    private async void LibraryViewChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender != e.Source || !_isAttached || !_viewModel.CanNavigate) return;
        UpdateWorkspace();
        await _viewModel.LoadLibraryViewAsync();
    }

    private async void ContentTypeChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender != e.Source || !_isAttached || !_viewModel.CanNavigate) return;
        UpdateWorkspace();
        await _viewModel.LoadLibraryViewAsync();
    }

    private async void ToolFilterChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender != e.Source || !_isAttached || !_viewModel.CanNavigate) return;
        await _viewModel.LoadLibraryViewAsync();
    }

    private async void CatalogKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        if (_viewModel.CanNavigate) await _viewModel.SearchCatalogCommand.ExecuteAsync(null);
    }

    private void LibraryListKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.Source is Visual visual
            && (visual is Button || visual.GetVisualAncestors().OfType<Button>().Any())) return;
        e.Handled = true;
        _viewModel.OpenLibraryItemCommand.Execute(_viewModel.SelectedItem);
    }

    private void BackClicked(object? sender, RoutedEventArgs e) => _viewModel.BackCommand.Execute(null);

    private async void UninstallClicked(object? sender, RoutedEventArgs e)
    {
        var skill = _viewModel.SelectedItem?.Skill;
        if (!_viewModel.CanRemove || skill is null) return;
        if (await ConfirmationDialog.ShowAsync(CoreTools.Translate("Uninstall {0} from its installed agents?", skill.Name)
            + Environment.NewLine + skill.AgentNames)
            && ReferenceEquals(skill, _viewModel.SelectedSkill) && _viewModel.CanRemove)
            await _viewModel.RemoveCommand.ExecuteAsync(null);
    }

    private async void SourceInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await _viewModel.AddFromSourceCommand.ExecuteAsync(null);
    }

    private async void MarketplaceKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await _viewModel.AgentPlugins.AddMarketplaceCommand.ExecuteAsync(null);
    }

    private async void IdentifierKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await _viewModel.AgentPlugins.InstallByIdCommand.ExecuteAsync(null);
    }

    private void OpenSettings(object? sender, RoutedEventArgs e)
    {
        if (MainWindow.Instance?.DataContext is MainWindowViewModel window)
            window.OpenSettingsPage(typeof(AgentSkillsSettingsControl));
    }
}
