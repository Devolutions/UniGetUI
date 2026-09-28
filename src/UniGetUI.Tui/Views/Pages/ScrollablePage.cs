using Avalonia.Controls;
using UniGetUI.Tui.Infrastructure;

namespace UniGetUI.Tui.Views.Pages;

/// <summary>
/// A minimal <see cref="IFocusablePage"/> wrapper for otherwise static content (Managers, About).
/// It hosts the content in a focusable <see cref="ScrollViewer"/> so that, once the user dives into
/// the page (Tab / Enter), the arrow keys and PgUp/PgDn scroll it. Without this the window keeps
/// intercepting nav keys and these pages can never receive keyboard focus.
/// </summary>
internal sealed class ScrollablePage : UserControl, IFocusablePage
{
    private readonly ScrollViewer _scroll;

    public ScrollablePage(Control content)
    {
        _scroll = new ScrollViewer
        {
            Content = content,
            Focusable = true,
        };
        Content = _scroll;
    }

    public bool FocusPrimary() => _scroll.Focus();
}
