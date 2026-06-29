using System;

namespace UniGetUI.Tui.Infrastructure;

/// <summary>
/// A tiny decoupled navigation bus. Pages don't hold a reference to the window, so when a page needs
/// to switch the active section (e.g. jumping to the Operations page after enqueueing an operation)
/// it raises <see cref="NavigationRequested"/>; the <c>MainWindow</c> subscribes and drives its
/// sidebar selection. The page id matches the nav-entry id ("operations", "installed", …).
/// </summary>
internal static class TuiShell
{
    public static event Action<string>? NavigationRequested;

    public static void Navigate(string pageId) => NavigationRequested?.Invoke(pageId);
}

/// <summary>
/// Implemented by content pages that expose a meaningful primary control to receive focus when the
/// user dives into the content pane (via Enter / Tab / "/"). The window calls <see cref="FocusPrimary"/>
/// without needing to know the concrete page type.
/// </summary>
internal interface IFocusablePage
{
    /// <summary>Moves keyboard focus to this page's primary control. Returns false if it couldn't.</summary>
    bool FocusPrimary();
}
