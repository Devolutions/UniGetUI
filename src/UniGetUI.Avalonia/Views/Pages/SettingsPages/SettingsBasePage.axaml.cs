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
public partial class SettingsBasePage : UserControl, IInnerNavigationPage, IEnterLeaveListener,
    ISearchBoxPage, IAsyncLeaveGuard
{
    private SettingsBasePageViewModel VM => (SettingsBasePageViewModel)DataContext!;

    private readonly bool _isManagers;

    // ── Navigation stack ──────────────────────────────────────────────────
    private readonly Stack<UserControl> _history = new();
    private UserControl? _currentContent;
    private readonly DirectionalSlideTransition _slide = new();
    private readonly SemaphoreSlim _transitionSemaphore = new(1, 1);

    // ── Lazy-created homepages ────────────────────────────────────────────
    private SettingsHomepage? _settingsHomepage;
    private ManagersHomepage? _managersHomepage;

    public SettingsBasePage(bool isManagers)
    {
        _isManagers = isManagers;

        DataContext = new SettingsBasePageViewModel();
        InitializeComponent();
        Frame.PageTransition = _slide;

        VM.BackRequested += (_, _) => _ = OnBackClickedAsync();

        // Navigate to the appropriate homepage on first load
        NavigateToPage(isManagers ? GetManagersHomepage() : GetSettingsHomepage());
    }

    // ── Button handlers ───────────────────────────────────────────────────

    private async Task OnBackClickedAsync()
    {
        if (_currentContent is SettingsHomepage or ManagersHomepage)
            GetMainWindowViewModel()?.NavigateBack();
        else if (_history.Count > 0)
            await NavigateBackAsync();
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

    private async Task<bool> NavigateBackAsync(CancellationToken cancellationToken = default)
    {
        await _transitionSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (_history.Count == 0) return false;
            if (!await CanCurrentPageLeaveCoreAsync(
                    PageLeaveReason.NestedNavigation,
                    cancellationToken))
                return false;

            var discardedPage = _currentContent;
            var previousPage = _history.Pop();
            NavigateToPage(previousPage, forward: false);
            DisposePage(discardedPage);
            return true;
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    private async void Page_NavigationRequested(object? sender, Type e)
    {
        if (e == typeof(ManagersHomepage))
        {
            GetMainWindowViewModel()?.NavigateTo(PageType.Managers);
            return;
        }

        await NavigateToAsync(e);
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
        if (t == typeof(AgentPolicyInspector)) return new AgentPolicyInspector();
        return null;
    }

    private SettingsHomepage GetSettingsHomepage() =>
        _settingsHomepage ??= new SettingsHomepage();

    private ManagersHomepage GetManagersHomepage()
    {
        if (_managersHomepage is null)
        {
            _managersHomepage = new ManagersHomepage();
            _managersHomepage.ManagerNavigationRequested += (_, manager) => _ = NavigateToAsync(manager);
        }
        return _managersHomepage;
    }

    // ── IInnerNavigationPage ──────────────────────────────────────────────

    public bool CanGoBack() =>
        _history.Count > 0
        && _currentContent is not SettingsHomepage
        && _currentContent is not ManagersHomepage;

    public async Task<bool> GoBackAsync(CancellationToken cancellationToken = default)
    {
        if (CanGoBack())
            return await NavigateBackAsync(cancellationToken);
        else
        {
            GetMainWindowViewModel()?.NavigateBack();
            return true;
        }
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

    public async Task<bool> NavigateToAsync(
        IPackageManager manager,
        CancellationToken cancellationToken = default)
    {
        await _transitionSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (!await CanCurrentPageLeaveCoreAsync(
                    PageLeaveReason.NestedNavigation,
                    cancellationToken))
                return false;

            if (_currentContent is not null)
                _history.Push(_currentContent);

            NavigateToPage(new PackageManagerPage(manager));
            return true;
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    public async Task<bool> NavigateToAsync(
        Type page,
        string? anchor = null,
        CancellationToken cancellationToken = default)
    {
        await _transitionSemaphore.WaitAsync(cancellationToken);
        try
        {
            // Already on the requested page (e.g. searching within it) — just scroll, don't recreate.
            if (_currentContent?.GetType() == page)
            {
                if (anchor is not null && _currentContent is ISettingsPage current)
                    current.ScrollToAnchor(anchor);
                return true;
            }

            if (!await CanCurrentPageLeaveCoreAsync(
                    PageLeaveReason.NestedNavigation,
                    cancellationToken))
                return false;

            if (_currentContent is not null)
                _history.Push(_currentContent);
            var target = CreatePageForType(page);
            if (target is not null)
            {
                NavigateToPage(target);
                if (anchor is not null && target is ISettingsPage sp)
                    sp.ScrollToAnchor(anchor);
            }
            return target is not null;
        }
        finally
        {
            _transitionSemaphore.Release();
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

    public async Task<bool> CanLeaveAsync(
        PageLeaveReason reason,
        CancellationToken cancellationToken = default)
    {
        await _transitionSemaphore.WaitAsync(cancellationToken);
        try
        {
            return await CanCurrentPageLeaveCoreAsync(reason, cancellationToken);
        }
        finally
        {
            _transitionSemaphore.Release();
        }
    }

    private Task<bool> CanCurrentPageLeaveCoreAsync(
        PageLeaveReason reason,
        CancellationToken cancellationToken = default) =>
        _currentContent is IAsyncLeaveGuard guard
            ? guard.CanLeaveAsync(reason, cancellationToken)
            : Task.FromResult(true);
}
