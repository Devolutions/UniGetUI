using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class ButtonActivationGuard
{
    private static bool _installed;

    private static readonly ConditionalWeakTable<Button, object> _suppressedSpacePresses = new();
    private static readonly object _marker = new();

    public static void Install()
    {
        if (_installed)
            return;
        _installed = true;

        Button.KeyDownEvent.AddClassHandler<Button>(OnButtonKeyDown, RoutingStrategies.Tunnel);
        Button.KeyUpEvent.AddClassHandler<Button>(OnButtonKeyUp, RoutingStrategies.Tunnel);
    }

    private static void OnButtonKeyDown(Button button, KeyEventArgs e)
    {
        if (e.Key is Key.Space)
        {
            if (e.KeyModifiers is not KeyModifiers.None)
            {
                _suppressedSpacePresses.AddOrUpdate(button, _marker);
                e.Handled = true;
            }
            else
            {
                _suppressedSpacePresses.Remove(button);
            }
        }
        else if (e.Key is Key.Enter && e.KeyModifiers is not KeyModifiers.None)
        {
            e.Handled = true;
        }
    }

    private static void OnButtonKeyUp(Button button, KeyEventArgs e)
    {
        if (e.Key is Key.Space && _suppressedSpacePresses.Remove(button))
            e.Handled = true;
    }
}
