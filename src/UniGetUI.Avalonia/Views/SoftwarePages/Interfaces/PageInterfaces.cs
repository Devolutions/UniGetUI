namespace UniGetUI.Avalonia.Views.Pages;

/// <summary>
/// Implemented by pages that listen to keyboard shortcut triggers from MainWindow.
/// Mirrors UniGetUI.Pages.PageInterfaces.IKeyboardShortcutListener
/// </summary>
public interface IKeyboardShortcutListener
{
    void SearchTriggered();
    void ReloadTriggered();
    void SelectAllTriggered();
}

/// <summary>
/// Implemented by pages that have enter/leave lifecycle events.
/// Mirrors UniGetUI.Pages.PageInterfaces.IEnterLeaveListener
/// </summary>
public interface IEnterLeaveListener
{
    void OnEnter();
    void OnLeave();
}

/// <summary>
/// Implemented by pages that bind to the global search box.
/// Mirrors UniGetUI.Pages.PageInterfaces.ISearchBoxPage
/// </summary>
public interface ISearchBoxPage
{
    string QueryBackup { get; set; }
    string SearchBoxPlaceholder { get; }
    void SearchBox_QuerySubmitted(object? sender, EventArgs? e);
}

/// <summary>
/// Implemented by pages that have their own internal navigation stack (e.g. Settings).
/// Mirrors UniGetUI.Pages.PageInterfaces.IInnerNavigationPage
/// </summary>
public interface IInnerNavigationPage
{
    bool CanGoBack();
    void GoBack();
}
