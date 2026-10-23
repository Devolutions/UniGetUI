using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Avalonia.Views.DialogPages;
using UniGetUI.PackageEngine.Managers.SkillsManager;
using SkillsManager = UniGetUI.PackageEngine.Managers.SkillsManager.AgentSkills;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public sealed partial class AgentSkillsSettingsControl : UserControl
{
    private readonly AgentSkillsSettingsViewModel _viewModel;
    private readonly SourceManagerCardViewModel? _sources;
    private bool _attached;

    public AgentSkillsSettingsControl(SkillsManager manager)
        : this(new AgentSkillsSettingsViewModel(manager), new SourceManagerCard(manager),
            () => ShowDialogAsync(new MissingDependencyDialog(NotionCliTool.CreateDependency(), 1, 1)),
            () => ShowDialogAsync(new NotionSignInDialog(manager)))
    { }

    internal AgentSkillsSettingsControl(AgentSkillsSettingsViewModel viewModel, Control? sourceCard = null,
        Func<Task>? install = null, Func<Task>? signIn = null)
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        InitializeComponent();
        SourcesHolder.Content = sourceCard;
        _sources = sourceCard?.DataContext as SourceManagerCardViewModel;
        InstallNotionButton.Click += async (_, _) =>
        {
            if (install is not null)
                await _viewModel.RunNotionActionAsync(install, requiresValidVersion: true);
            if (_viewModel.HasVersionError) FocusVersion();
        };
        SignInButton.Click += async (_, _) =>
        {
            if (signIn is not null) await _viewModel.RunNotionActionAsync(signIn);
        };
        SignOutButton.Click += async (_, _) => await _viewModel.SignOutAsync();
        RefreshNotionButton.Click += async (_, _) => await _viewModel.RefreshAsync();
        EditVersionButton.Click += (_, _) => FocusVersion();
    }

    private static Task ShowDialogAsync(ImmersiveDialog dialog)
    {
        if (MainWindow.Instance is not { } owner)
            throw new InvalidOperationException("The main window is not available for Notion setup.");
        return owner.ShowDialogAndRestoreVisibilityAsync(dialog);
    }

    private void FocusVersion()
    {
        _viewModel.IsAdvancedExpanded = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_attached) return;
            var target = _viewModel.SpecificVersion ? (Control)VersionInput : VersionPolicySelector;
            target.BringIntoView();
            target.Focus();
        });
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _viewModel.Activate();
        _viewModel.PropertyChanged += OnViewModelChanged;
        if (_sources is not null)
        {
            _sources.PropertyChanged += OnSourcesChanged;
            _sources.Sources.CollectionChanged += OnSourcesCollectionChanged;
        }
        _ = _viewModel.RefreshAsync();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        _viewModel.PropertyChanged -= OnViewModelChanged;
        if (_sources is not null)
        {
            _sources.PropertyChanged -= OnSourcesChanged;
            _sources.Sources.CollectionChanged -= OnSourcesCollectionChanged;
        }
        _viewModel.Deactivate();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnSourcesChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SourceManagerCardViewModel.IsLoading) && _sources is { IsLoading: false })
            QueueRefresh();
    }

    private void OnSourcesCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_sources is { IsLoading: false }) QueueRefresh();
    }

    private void QueueRefresh() => Dispatcher.UIThread.Post(() =>
    {
        if (_attached) _ = _viewModel.RefreshAsync();
    });

    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AgentSkillsSettingsViewModel.SkillsShDiscoveryEnabled)
            && _viewModel.HasSkillsShDiscoveryError)
        {
            // A failed two-way write can leave the attempted value in the binding's target cache.
            Dispatcher.UIThread.Post(() =>
            {
                if (_attached)
                    SkillsShDiscoveryToggle.SetCurrentValue(ToggleSwitch.IsCheckedProperty, _viewModel.SkillsShDiscoveryEnabled);
            });
        }

        string? error = e.PropertyName switch
        {
            nameof(AgentSkillsSettingsViewModel.TargetError) => _viewModel.TargetError,
            nameof(AgentSkillsSettingsViewModel.VersionError) => _viewModel.VersionError,
            nameof(AgentSkillsSettingsViewModel.NotionError) => _viewModel.NotionError,
            nameof(AgentSkillsSettingsViewModel.SkillsShDiscoveryError) => _viewModel.SkillsShDiscoveryError,
            _ => null,
        };
        if (!string.IsNullOrEmpty(error))
            AccessibilityAnnouncementService.Announce(error, AutomationLiveSetting.Assertive);
    }
}
