using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class ButtonActivationGuard
{
    private static bool _installed;

    public static void Install()
    {
        if (_installed)
            return;
        _installed = true;

        Button.KeyDownEvent.AddClassHandler<Button>(OnButtonKey, RoutingStrategies.Tunnel);
        Button.KeyUpEvent.AddClassHandler<Button>(OnButtonKey, RoutingStrategies.Tunnel);
    }

    private static void OnButtonKey(Button button, KeyEventArgs e)
    {
        if ((e.Key is Key.Space or Key.Enter) && e.KeyModifiers is not KeyModifiers.None)
            e.Handled = true;
    }
}
