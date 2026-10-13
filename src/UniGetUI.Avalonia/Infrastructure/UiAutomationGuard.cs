using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class UiAutomationGuard
{
    private const uint WM_GETOBJECT = 0x003D;

    private static bool _installed;
    private static bool _uiaCoreLoaded;

    private static readonly ConditionalWeakTable<TopLevel, object> _hookedTopLevels = new();
    private static readonly object _marker = new();

    public static void Install()
    {
        if (_installed || !OperatingSystem.IsWindows())
            return;
        _installed = true;

        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => Attach(window));
        Control.LoadedEvent.AddClassHandler<TopLevel>((topLevel, _) => Attach(topLevel), RoutingStrategies.Direct);
    }

    public static void Attach(TopLevel topLevel)
    {
        if (!OperatingSystem.IsWindows() || !_hookedTopLevels.TryAdd(topLevel, _marker))
            return;

        Win32Properties.AddWndProcHookCallback(topLevel, OnWndProc);
    }

    private static nint OnWndProc(nint hWnd, uint msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg != WM_GETOBJECT || _uiaCoreLoaded)
            return 0;

        if (NativeLibrary.TryLoad("UIAutomationCore.dll", typeof(UiAutomationGuard).Assembly,
                DllImportSearchPath.System32, out _))
        {
            _uiaCoreLoaded = true;
            return 0;
        }

        handled = true;
        return 0;
    }
}
