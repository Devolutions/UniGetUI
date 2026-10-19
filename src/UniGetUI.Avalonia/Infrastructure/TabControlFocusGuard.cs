using Avalonia.Controls;
using Avalonia.Input;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class TabControlFocusGuard
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed)
            return;
        _installed = true;

        InputElement.GotFocusEvent.AddClassHandler<TabControl>(OnTabControlGotFocus);
    }

    private static void OnTabControlGotFocus(TabControl tabControl, FocusChangedEventArgs e)
    {
        if (e.Source is TabItem item && tabControl.IndexFromContainer(item) >= 0)
            return;

        KeyboardNavigation.SetTabOnceActiveElement(
            tabControl,
            tabControl.ContainerFromIndex(tabControl.SelectedIndex));
    }
}
