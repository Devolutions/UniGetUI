namespace UniGetUI.Tui.Infrastructure;

/// <summary>
/// A tiny decoupled navigation bus. Pages don't hold a reference to the window, so when a page needs
/// to switch the active section it raises <see cref="NavigationRequested"/>; the <c>MainWindow</c>
/// subscribes and switches the page tab. The page id is one of <see cref="TuiPageIds"/>.
/// </summary>
internal static class TuiShell
{
    public static event Action<string>? NavigationRequested;

    /// <summary>Raised when a page's title, hints or badge text changed and the chrome should re-read it.</summary>
    public static event Action? ChromeInvalidated;

    public static void Navigate(string pageId) => NavigationRequested?.Invoke(pageId);

    public static void InvalidateChrome() => ChromeInvalidated?.Invoke();

    /// <summary>Asks the Bundles page to show its "open bundle" prompt (used by Backup → Restore).</summary>
    public static event Action? BundleOpenRequested;

    public static void RequestBundleOpen() => BundleOpenRequested?.Invoke();

    /// <summary>Asks the window to rebuild the given pages next time they are shown (layout settings changed).</summary>
    public static event Action<string[]>? PagesInvalidated;

    public static void InvalidatePages(params string[] pageIds) => PagesInvalidated?.Invoke(pageIds);
}

internal static class TuiPageIds
{
    public const string Discover = "discover";
    public const string Updates = "updates";
    public const string Installed = "installed";
    public const string Bundles = "bundles";
    public const string Operations = "operations";
    public const string Managers = "managers";
    public const string Settings = "settings";
    public const string Logs = "logs";
    public const string History = "history";
    public const string Help = "help";
    public const string About = "about";

    public static readonly string[] All =
        [Discover, Updates, Installed, Bundles, Operations, Managers, Settings, Logs, History, Help, About];
}

/// <summary>A key and what it does (translated label). <paramref name="Priority"/> orders what is dropped first
/// when space runs out: lower values are kept longer.</summary>
internal sealed record TuiKeyHint(string Key, string Label, int Priority = 0);

/// <summary>A command a page offers, shown in its action menu and the menu bar and bound to a hotkey.</summary>
internal sealed record TuiAction(string Label, string? Shortcut, Func<Task> Run, Func<bool>? Enabled = null)
{
    public bool IsSeparator => Label.Length == 0;

    public bool IsEnabled => Enabled?.Invoke() ?? true;

    public static TuiAction Separator { get; } = new("", null, () => Task.CompletedTask, () => false);
}

/// <summary>
/// Implemented by content pages that expose a meaningful primary control to receive focus when the
/// user dives into the content pane (via Enter / Tab / "/").
/// </summary>
internal interface IFocusablePage
{
    /// <summary>Moves keyboard focus to this page's primary control. Returns false if it couldn't.</summary>
    bool FocusPrimary();
}

/// <summary>The richer page contract the shell uses for hints, reload, search and actions.</summary>
internal interface ITuiPage : IFocusablePage
{
    /// <summary>The page's key hints for the function-key bar, most important first (the bar drops hints from
    /// the end when the terminal is too narrow). Empty when the page shows its keys itself.</summary>
    IReadOnlyList<TuiKeyHint> KeyHints { get; }

    /// <summary>The page's commands (action menu, menu bar). Empty entries are separators.</summary>
    IReadOnlyList<TuiAction> Actions { get; }

    /// <summary>F5 / Ctrl+R.</summary>
    void Reload() { }

    /// <summary>Ctrl+F. Defaults to the primary control.</summary>
    bool FocusSearch() => FocusPrimary();

    /// <summary>Ctrl+A.</summary>
    void ToggleSelectAll() { }

    /// <summary>Called when the page becomes the visible page.</summary>
    void OnShown() { }
}
