using Avalonia.Controls;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

/// <summary>
/// Avalonia MVVM equivalent of the old code-behind-only SettingsBasePage.
/// Hosts a manual navigation stack of ISettingsPage UserControls.
/// </summary>
public partial class SettingsBasePage : UserControl, IInnerNavigationPage, IEnterLeaveListener
{
    private SettingsBasePageViewModel VM => (SettingsBasePageViewModel)DataContext!;

    private readonly bool _isManagers;

    // ── Navigation stack ──────────────────────────────────────────────────
    private readonly Stack<UserControl> _history = new();
    private UserControl? _currentContent;

    // ── Lazy-created homepages ────────────────────────────────────────────
    private SettingsHomepage? _settingsHomepage;
    private ManagersHomepage? _managersHomepage;

    public SettingsBasePage(bool isManagers)
    {
        _isManagers = isManagers;

        DataContext = new SettingsBasePageViewModel();
        InitializeComponent();

        VM.BackRequested += (_, _) => OnBackClicked();

        // Navigate to the appropriate homepage on first load
        NavigateToPage(isManagers ? GetManagersHomepage() : GetSettingsHomepage());
    }

    // ── Button handlers ───────────────────────────────────────────────────

    private void OnBackClicked()
    {
        if (_currentContent is SettingsHomepage or ManagersHomepage)
            GetMainWindowViewModel()?.NavigateBack();
        else if (_history.Count > 0)
            NavigateBack();
        else
            NavigateToPage(_isManagers ? GetManagersHomepage() : GetSettingsHomepage());
    }

    // ── Navigation ────────────────────────────────────────────────────────

    private void NavigateToPage(UserControl page)
    {
        // Detach events from the outgoing page
        if (_currentContent is ISettingsPage oldSp)
        {
            oldSp.NavigationRequested -= Page_NavigationRequested;
            oldSp.RestartRequired -= Page_RestartRequired;
        }

        Frame.Content = page;
        _currentContent = page;

        // Attach events to the incoming page and update VM-bound header
        if (page is ISettingsPage sp)
        {
            sp.NavigationRequested += Page_NavigationRequested;
            sp.RestartRequired += Page_RestartRequired;
            VM.Title = sp.ShortTitle;
        }
    }

    private void NavigateBack()
    {
        if (_history.Count == 0) return;
        var prev = _history.Pop();
        NavigateToPage(prev);
    }

    private void Page_NavigationRequested(object? sender, Type e)
    {
        if (e == typeof(ManagersHomepage))
        {
            GetMainWindowViewModel()?.NavigateTo(PageType.Managers);
            return;
        }

        // Push the current page onto history before navigating forward
        if (_currentContent is not null)
            _history.Push(_currentContent);

        var target = CreatePageForType(e);
        if (target is not null)
            NavigateToPage(target);
    }

    private void Page_RestartRequired(object? sender, EventArgs e) =>
        VM.IsRestartBannerVisible = true;

    private static UserControl? CreatePageForType(Type t)
    {
        if (t == typeof(SettingsHomepage)) return new SettingsHomepage();
        if (t == typeof(ManagersHomepage)) return new ManagersHomepage();
        if (t == typeof(General)) return new General();
        if (t == typeof(Interface_P)) return new Interface_P();
        if (t == typeof(Internet)) return new Internet();
        if (t == typeof(Backup)) return new Backup();
        if (t == typeof(Experimental)) return new Experimental();
        if (t == typeof(Notifications)) return new Notifications();
        if (t == typeof(Updates)) return new Updates();
        if (t == typeof(Operations)) return new Operations();
        if (t == typeof(Administrator)) return new Administrator();
        return null;
    }

    private SettingsHomepage GetSettingsHomepage() =>
        _settingsHomepage ??= new SettingsHomepage();

    private ManagersHomepage GetManagersHomepage() =>
        _managersHomepage ??= new ManagersHomepage();

    // ── IInnerNavigationPage ──────────────────────────────────────────────

    public bool CanGoBack() =>
        _history.Count > 0
        && _currentContent is not SettingsHomepage
        && _currentContent is not ManagersHomepage;

    public void GoBack()
    {
        if (CanGoBack())
            NavigateBack();
        else
            GetMainWindowViewModel()?.NavigateBack();
    }

    // ── IEnterLeaveListener ───────────────────────────────────────────────

    public void OnEnter()
    {
        _history.Clear();
        NavigateToPage(_isManagers ? GetManagersHomepage() : GetSettingsHomepage());
        VM.IsRestartBannerVisible = false;
    }

    public void OnLeave() { }

    // ── IInnerNavigationPage extra overloads ──────────────────────────────

    public void NavigateTo(IPackageManager manager)
    {
        // TODO: navigate to PackageManagerPage for this manager (Phase 5)
    }

    public void NavigateTo(Type page)
    {
        if (_currentContent is not null)
            _history.Push(_currentContent);
        var target = CreatePageForType(page);
        if (target is not null) NavigateToPage(target);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private MainWindowViewModel? GetMainWindowViewModel() =>
        (TopLevel.GetTopLevel(this) is Window { DataContext: MainWindowViewModel vm }) ? vm : null;
}
