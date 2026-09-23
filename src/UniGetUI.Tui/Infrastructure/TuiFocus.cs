using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace UniGetUI.Tui.Infrastructure;

/// <summary>
/// Helpers for seating real keyboard focus inside the TUI over a PTY.
/// </summary>
internal static class TuiFocus
{
    /// <summary>
    /// Seat real keyboard focus on the selected row's realized <c>ListBoxItem</c> container so the
    /// ListBox's built-in arrow navigation actually receives keystrokes. A bare <c>ListBox.Focus()</c>
    /// does NOT do this over ConPTY: it focuses the ListBox control but leaves no realized item holding
    /// keyboard focus, so Up/Down never reach the navigation logic and selection sticks on row 0.
    /// <para/>
    /// This ensures a selection exists, focuses the selected container, falls back to the ListBox itself
    /// when the container is not yet realized, and posts a single Background retry to re-seat focus once
    /// the container has been generated (the "cold from a TextBox" hand-off case).
    /// <para/>
    /// Returns true on the synchronous pass even when it falls back to <c>list.Focus()</c>, so callers
    /// that gate pane activation on the return value still flip the pane.
    /// </summary>
    public static bool SeatListFocus(ListBox list)
    {
        if (list.ItemCount == 0) return false;
        if (list.SelectedIndex < 0) list.SelectedIndex = 0;

        bool ok = list.ContainerFromIndex(list.SelectedIndex) is Control c && c.Focus();
        if (!ok) ok = list.Focus();

        Dispatcher.UIThread.Post(() =>
        {
            // Pages are cached and the post is delayed, so by the time this runs the user may have
            // opened a prompt/menu, tabbed to the sidebar, pressed Esc, or navigated to another page.
            // The retry only exists to upgrade the cold list.Focus() fallback (focus parked on the
            // ListBox itself) to a realized ListBoxItem; if focus has already left the list, the newer
            // focus decision must win, so no-op rather than stealing it back.
            var top = TopLevel.GetTopLevel(list);
            if (top is null) return;
            if (top.FocusManager?.GetFocusedElement() is not Visual focused) return;
            if (!ReferenceEquals(focused, list) && !list.IsVisualAncestorOf(focused)) return;
            if (list.ContainerFromIndex(list.SelectedIndex) is Control c2) c2.Focus();
        }, DispatcherPriority.Background);

        return ok;
    }

    /// <summary>
    /// Seat keyboard focus on a terminal DataGrid. Consolonia's DataGrid handles row navigation when the
    /// grid owns focus; this ensures a selected item exists first so Down/Up start from a real row.
    /// </summary>
    public static bool SeatDataGridFocus(DataGrid grid)
    {
        if (grid.SelectedItem is null)
        {
            object? first = FirstItem(grid.ItemsSource);
            if (first is null) return false;
            grid.SelectedItem = first;
        }

        bool ok = grid.Focus();
        Dispatcher.UIThread.Post(() =>
        {
            var top = TopLevel.GetTopLevel(grid);
            if (top is null) return;
            if (top.FocusManager?.GetFocusedElement() is not Visual focused) return;
            if (!ReferenceEquals(focused, grid) && !grid.IsVisualAncestorOf(focused)) return;
            grid.Focus();
        }, DispatcherPriority.Background);

        return ok;
    }

    private static object? FirstItem(IEnumerable? items)
    {
        if (items is null) return null;
        foreach (object? item in items)
        {
            if (item is not null) return item;
        }

        return null;
    }
}
