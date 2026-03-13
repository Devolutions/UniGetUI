using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Avalonia.Views.Pages.ManagersPages;

public partial class ManagersPageView : UserControl, IShellPage
{
    private readonly Dictionary<string, ManagerDetailView> _detailCache = [];

    private ManagersHomeView? _homeView;
    private IPackageManager? _selectedManager;

    private Button BackNavigationButton => GetControl<Button>("SectionBackButton");

    private TextBlock SectionKickerText => GetControl<TextBlock>("SectionKickerBlock");

    private TextBlock SectionTitleText => GetControl<TextBlock>("SectionTitleBlock");

    private TextBlock SectionSubtitleText => GetControl<TextBlock>("SectionSubtitleBlock");

    private TextBlock SectionStatusText => GetControl<TextBlock>("SectionStatusBlock");

    private ContentControl SectionContentPresenter => GetControl<ContentControl>("SectionContentHost");

    public ManagersPageView()
    {
        Title = CoreTools.Translate("Package Managers");
        Subtitle = CoreTools.Translate("Enable and disable package managers, change default install options, etc.");
        SupportsSearch = false;
        SearchPlaceholder = string.Empty;

        InitializeComponent();
        SectionKickerText.Text = CoreTools.Translate("Package Managers");
        NavigateHome();
    }

    public string Title { get; }

    public string Subtitle { get; }

    public bool SupportsSearch { get; }

    public string SearchPlaceholder { get; }

    public void UpdateSearchQuery(string query)
    {
    }

    private void NavigateHome()
    {
        _selectedManager = null;

        if (_homeView is null)
        {
            _homeView = new ManagersHomeView();
            _homeView.NavigationRequested += HomeView_NavigationRequested;
        }

        SectionContentPresenter.Content = _homeView;
        ApplySectionChrome(_homeView);
        BackNavigationButton.IsVisible = false;
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
        SectionContentPresenter.Content = detailView;
        ApplySectionChrome(detailView);
        BackNavigationButton.IsVisible = true;
    }

    private void ApplySectionChrome(IManagerSectionView sectionView)
    {
        SectionTitleText.Text = sectionView.SectionTitle;
        SectionSubtitleText.Text = sectionView.SectionSubtitle;
        SectionStatusText.Text = sectionView.SectionStatus;
    }

    private void HomeView_NavigationRequested(object? sender, IPackageManager manager)
    {
        NavigateToManager(manager);
    }

    private void SectionBackButton_OnClick(object? sender, RoutedEventArgs e)
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
