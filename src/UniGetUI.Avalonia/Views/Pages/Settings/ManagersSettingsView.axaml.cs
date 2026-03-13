using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UniGetUI.Avalonia.Views.Pages.ManagersPages;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public partial class ManagersSettingsView : UserControl, ISettingsSectionView, ISettingsSectionStateNotifier
{
    private readonly Dictionary<string, ManagerDetailView> _detailCache = [];

    private ManagersHomeView? _homeView;
    private IPackageManager? _selectedManager;

    private Button BackToOverviewButtonControl => GetControl<Button>("BackToOverviewButton");

    private ContentControl SectionContentHostControl => GetControl<ContentControl>("SectionContentHost");

    public ManagersSettingsView()
    {
        InitializeComponent();
        BackToOverviewButtonControl.Content = CoreTools.Translate("Package managers");
        NavigateHome();
    }

    public event EventHandler? SectionStateChanged;

    public string SectionTitle => _selectedManager is null
        ? CoreTools.Translate("Package manager preferences")
        : CoreTools.Translate("{0} settings", _selectedManager.DisplayName);

    public string SectionSubtitle => _selectedManager is null
        ? CoreTools.Translate("Enable and disable package managers, change default install options, etc.")
        : CoreTools.Translate("Package manager preferences");

    public string SectionStatus => _selectedManager is null
        ? CoreTools.Translate("{0} settings", PEInterface.Managers.Length)
        : BuildStatus(_selectedManager);

    private void NavigateHome()
    {
        _selectedManager = null;

        if (_homeView is null)
        {
            _homeView = new ManagersHomeView();
            _homeView.NavigationRequested += HomeView_NavigationRequested;
        }

        SectionContentHostControl.Content = _homeView;
        BackToOverviewButtonControl.IsVisible = false;
        SectionStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void NavigateToManager(IPackageManager manager)
    {
        _selectedManager = manager;

        if (!_detailCache.TryGetValue(manager.Name, out var detailView))
        {
            detailView = new ManagerDetailView();
            _detailCache[manager.Name] = detailView;
        }

        detailView.LoadManager(manager);
        SectionContentHostControl.Content = detailView;
        BackToOverviewButtonControl.IsVisible = true;
        SectionStateChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string BuildStatus(IPackageManager manager)
    {
        if (!manager.IsEnabled())
            return CoreTools.Translate("Disabled");

        return manager.Status.Found
            ? CoreTools.Translate("Ready")
            : CoreTools.Translate("Not found");
    }

    private void HomeView_NavigationRequested(object? sender, IPackageManager manager)
    {
        NavigateToManager(manager);
    }

    private void BackToOverviewButton_OnClick(object? sender, RoutedEventArgs e)
    {
        NavigateHome();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private T GetControl<T>(string name)
        where T : Control
    {
        return this.FindControl<T>(name)
            ?? throw new InvalidOperationException($"Control '{name}' was not found.");
    }
}
