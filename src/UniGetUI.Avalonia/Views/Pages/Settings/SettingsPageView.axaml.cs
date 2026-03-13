using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public partial class SettingsPageView : UserControl, IShellPage
{
    private readonly Dictionary<SettingsSectionRoute, Control> _sectionCache = [];
    private readonly List<SettingsSectionRoute> _history = [];

    private ISettingsSectionStateNotifier? _sectionStateNotifier;
    private SettingsSectionRoute _currentSection;

    private Button SectionBackNavigationButton => GetControl<Button>("SectionBackButton");

    private TextBlock SectionKickerText => GetControl<TextBlock>("SectionKickerBlock");

    private TextBlock SectionTitleText => GetControl<TextBlock>("SectionTitleBlock");

    private TextBlock SectionSubtitleText => GetControl<TextBlock>("SectionSubtitleBlock");

    private TextBlock SectionStatusText => GetControl<TextBlock>("SectionStatusBlock");

    private ContentControl SectionContentPresenter => GetControl<ContentControl>("SectionContentHost");

    public SettingsPageView()
    {
        Title = CoreTools.Translate("Settings");
        Subtitle = CoreTools.Translate("Default preferences - suitable for regular users");
        SupportsSearch = false;
        SearchPlaceholder = string.Empty;

        InitializeComponent();
        SectionKickerText.Text = CoreTools.Translate("Settings");
        NavigateTo(SettingsSectionRoute.Home, false);
    }

    public string Title { get; }

    public string Subtitle { get; }

    public bool SupportsSearch { get; }

    public string SearchPlaceholder { get; }

    public void UpdateSearchQuery(string query)
    {
    }

    internal void OpenSection(SettingsSectionRoute route)
    {
        NavigateTo(route);
    }

    private void NavigateTo(SettingsSectionRoute route, bool addToHistory = true)
    {
        if (_currentSection == route && SectionContentPresenter.Content is not null)
        {
            return;
        }

        if (addToHistory && SectionContentPresenter.Content is not null)
        {
            _history.Add(_currentSection);
        }

        _currentSection = route;
        SetSectionContent(GetSection(route));
        ApplySectionChrome();
    }

    private void SetSectionContent(Control section)
    {
        if (_sectionStateNotifier is not null)
        {
            _sectionStateNotifier.SectionStateChanged -= SectionStateNotifier_SectionStateChanged;
            _sectionStateNotifier = null;
        }

        SectionContentPresenter.Content = section;

        if (section is ISettingsSectionStateNotifier notifier)
        {
            _sectionStateNotifier = notifier;
            _sectionStateNotifier.SectionStateChanged += SectionStateNotifier_SectionStateChanged;
        }
    }

    private Control GetSection(SettingsSectionRoute route)
    {
        if (_sectionCache.TryGetValue(route, out var existingSection))
        {
            return existingSection;
        }

        Control section = route switch
        {
            SettingsSectionRoute.Home => CreateHomeSection(),
            SettingsSectionRoute.Interface => new InterfaceSettingsView(),
            SettingsSectionRoute.General => new GeneralSettingsView(),
            SettingsSectionRoute.Managers => new ManagersSettingsView(),
            SettingsSectionRoute.Updates => new UpdatesSettingsView(),
            SettingsSectionRoute.Notifications => new NotificationsSettingsView(),
            SettingsSectionRoute.Operations => new OperationsSettingsView(),
            SettingsSectionRoute.Internet => new InternetSettingsView(),
            SettingsSectionRoute.Administrator => new AdministratorSettingsView(),
            SettingsSectionRoute.Backup => new BackupSettingsView(),
            SettingsSectionRoute.Experimental => new ExperimentalSettingsView(),
            _ => throw new InvalidOperationException($"Unsupported settings section {route}"),
        };

        _sectionCache[route] = section;
        return section;
    }

    private SettingsHomeView CreateHomeSection()
    {
        var homeSection = new SettingsHomeView();
        homeSection.NavigationRequested += HomeSection_NavigationRequested;
        return homeSection;
    }

    private void ApplySectionChrome()
    {
        if (SectionContentPresenter.Content is not ISettingsSectionView sectionView)
        {
            throw new InvalidCastException("The current settings section does not implement ISettingsSectionView.");
        }

        SectionTitleText.Text = sectionView.SectionTitle;
        SectionSubtitleText.Text = sectionView.SectionSubtitle;
        SectionStatusText.Text = sectionView.SectionStatus;
        SectionBackNavigationButton.IsVisible = _history.Count > 0;

        if (_currentSection == SettingsSectionRoute.Home)
        {
            SectionKickerText.IsVisible = false;
        }
        else
        {
            SectionKickerText.IsVisible = true;
            SectionKickerText.Text = CoreTools.Translate("Settings");
        }
    }

    private void HomeSection_NavigationRequested(object? sender, SettingsSectionRoute route)
    {
        NavigateTo(route);
    }

    private void SectionStateNotifier_SectionStateChanged(object? sender, EventArgs e)
    {
        ApplySectionChrome();
    }

    private void SectionBackButton_OnClick(object? sender, RoutedEventArgs e)
    {
        if (_history.Count == 0)
        {
            return;
        }

        var targetSection = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        NavigateTo(targetSection, false);
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
