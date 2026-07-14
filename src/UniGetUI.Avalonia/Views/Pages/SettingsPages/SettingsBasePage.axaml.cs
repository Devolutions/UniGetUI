using Avalonia.Controls;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

/// <summary>
/// Avalonia MVVM equivalent of the old code-behind-only SettingsBasePage.
/// Hosts a manual navigation stack of ISettingsPage UserControls.
/// </summary>
public partial class SettingsBasePage : UserControl, IInnerNavigationPage, IEnterLeaveListener, ISearchBoxPage
{
    private SettingsBasePageViewModel VM => (SettingsBasePageViewModel)DataContext!;

    private readonly bool _isManagers;

    // ── Navigation stack ──────────────────────────────────────────────────
    private readonly Stack<UserControl> _history = new();
    private UserControl? _currentContent;
    private readonly DirectionalSlideTransition _slide = new();

    // ── Lazy-created homepages ────────────────────────────────────────────
    private SettingsHomepage? _settingsHomepage;
    private ManagersHomepage? _managersHomepage;

    public SettingsBasePage(bool isManagers)
    {
        _isManagers = isManagers;

        DataContext = new SettingsBasePageViewModel();
        InitializeComponent();
        Frame.PageTransition = _slide;

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

    private void NavigateToPage(UserControl page, bool forward = true)
    {
        // Detach events from the outgoing page
        if (_currentContent is ISettingsPage oldSp)
        {
            oldSp.NavigationRequested -= Page_NavigationRequested;
            oldSp.RestartRequired -= Page_RestartRequired;
        }

        // Forward (drill-in) slides in from the right; back slides in from the left.
        _slide.Reverse = !forward;
        Frame.Content = page;
        _currentContent = page;

        // Attach events to the incoming page and update VM-bound header
        if (page is ISettingsPage sp)
        {
            sp.NavigationRequested += Page_NavigationRequested;
            sp.RestartRequired += Page_RestartRequired;
            VM.Title = sp.ShortTitle;
        }

        // Refresh toggle states when returning to the managers list
        if (page is ManagersHomepage mh)
            mh.RefreshToggles();
    }

    private void NavigateBack()
    {
        if (_history.Count == 0) return;

        var discardedPage = _currentContent;
        var previousPage = _history.Pop();
        NavigateToPage(previousPage, forward: false);
        DisposePage(discardedPage);
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

    private void Page_RestartRequired(object? sender, EventArgs e)
    {
        VM.IsRestartBannerVisible = true;
        AvaloniaOperationRegistry.RestartRequired = true;
        MainWindow.Instance?.UpdateSystemTrayStatus();
    }

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

    private ManagersHomepage GetManagersHomepage()
    {
        if (_managersHomepage is null)
        {
            _managersHomepage = new ManagersHomepage();
            _managersHomepage.ManagerNavigationRequested += (_, manager) => NavigateTo(manager);
        }
        return _managersHomepage;
    }

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
        ResetToHomepage();
        VM.IsRestartBannerVisible = false;
    }

    public void OnLeave() => ResetToHomepage();

    // ── ISearchBoxPage ────────────────────────────────────────────────────
    // The title-bar box searches the settings/managers index. Suggestions and submit are driven
    // by MainWindowViewModel; this page only enables the box (via the interface) and names it.
    // The query isn't persisted across navigation, so QueryBackup is a no-op.
    public string QueryBackup { get => ""; set { } }

    public string SearchBoxPlaceholder =>
        CoreTools.Translate(_isManagers ? "Search package managers" : "Search settings");

    public void SearchBox_QuerySubmitted(object? sender, EventArgs? e) { }

    // ── IInnerNavigationPage extra overloads ──────────────────────────────

    public void NavigateTo(IPackageManager manager)
    {
        if (_currentContent is not null)
            _history.Push(_currentContent);

        NavigateToPage(new PackageManagerPage(manager));
    }

    public void NavigateTo(Type page, string? anchor = null)
    {
        // Already on the requested page (e.g. searching within it) — just scroll, don't recreate.
        if (_currentContent?.GetType() == page)
        {
            if (anchor is not null && _currentContent is ISettingsPage current)
                current.ScrollToAnchor(anchor);
            return;
        }

        if (_currentContent is not null)
            _history.Push(_currentContent);
        var target = CreatePageForType(page);
        if (target is not null)
        {
            NavigateToPage(target);
            if (anchor is not null && target is ISettingsPage sp)
                sp.ScrollToAnchor(anchor);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private void ResetToHomepage()
    {
        UserControl homepage = _isManagers ? GetManagersHomepage() : GetSettingsHomepage();

        while (_history.TryPop(out var page))
            if (!ReferenceEquals(page, homepage))
                DisposePage(page);

        if (ReferenceEquals(_currentContent, homepage)) return;

        var discardedPage = _currentContent;
        NavigateToPage(homepage);
        DisposePage(discardedPage);
    }

    private static void DisposePage(UserControl? page)
    {
        if (page is IDisposable disposable)
            disposable.Dispose();
    }

    private MainWindowViewModel? GetMainWindowViewModel() =>
        (TopLevel.GetTopLevel(this) is Window { DataContext: MainWindowViewModel vm }) ? vm : null;
}
