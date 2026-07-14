using Avalonia.Controls;
using UniGetUI.Avalonia.Infrastructure;

namespace UniGetUI.Avalonia.Views.Pages.SettingsPages;

public interface ISettingsPage
{
    bool CanGoBack { get; }
    string ShortTitle { get; }

    event EventHandler? RestartRequired;
    event EventHandler<Type>? NavigationRequested;

    // Scroll to (and highlight) a named control on this page — used by settings search.
    // Every implementer is a Control, so the shared default handles it; no per-page code needed.
    void ScrollToAnchor(string anchor)
    {
        if (this is Control control)
            SettingsAnchor.ScrollToAndHighlight(control, anchor);
    }
}
