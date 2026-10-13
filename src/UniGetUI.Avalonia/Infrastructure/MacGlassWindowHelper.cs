using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UniGetUI.Avalonia.Assets.Styles;
using UniGetUI.Core.Logging;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>macOS counterpart of <see cref="MicaWindowHelper"/>: native vibrancy (AcrylicBlur) behind a
/// transparent window. Skipped under Reduce transparency or Increase contrast (solid Styles.Common look).</summary>
internal static partial class MacGlassWindowHelper
{
    /// <summary>Class set on a window while its glass look is active; layout and styles key off it.</summary>
    public const string GLASS_CLASS = "mac-glass";

    private const string ObjCRuntime = "/usr/lib/libobjc.A.dylib";

    // AppKit NSVisualEffectMaterial / NSVisualEffectBlendingMode values.
    private const long NSVisualEffectMaterialSidebar = 7;
    private const long NSVisualEffectMaterialMenu = 5;
    private const long NSVisualEffectStateActive = 1;
    private const long NSVisualEffectBlendingModeBehindWindow = 0;

    private static bool? _useSolidSurfaces;
    private static bool _glassConfirmedUnavailable;
    private static bool _popupGlassHooked;

    public static bool IsGlassEnabled()
        => OperatingSystem.IsMacOS()
           && !_glassConfirmedUnavailable
           && !ShouldUseSolidSurfaces();

    /// <summary>Requests the vibrancy backdrop. Call before the window is shown.</summary>
    public static void Apply(Window window)
    {
        if (!IsGlassEnabled())
            return;

        window.TransparencyLevelHint = new[] { WindowTransparencyLevel.AcrylicBlur };
        window.Background = Brushes.Transparent;
        window.Classes.Add(GLASS_CLASS);
        window.Opened += (_, _) => OnOpened(window);
    }

    /// <summary>Gives menus, flyouts and dropdowns the native menu blur with rounded corners.</summary>
    public static void EnablePopupGlass()
    {
        if (!IsGlassEnabled() || _popupGlassHooked)
            return;
        _popupGlassHooked = true;

        Control.LoadedEvent.AddClassHandler<PopupRoot>((root, _) => ApplyToPopup(root));
    }

    private static void ApplyToPopup(PopupRoot root)
    {
        // Tooltips keep their plain opaque look, as on macOS.
        if (!IsGlassEnabled() || root.GetVisualDescendants().Any(c => c is ToolTip))
            return;

        root.TransparencyLevelHint = new[] { WindowTransparencyLevel.AcrylicBlur };
        root.Background = Brushes.Transparent;

        // Avalonia (re)creates its blur view while applying the hint, so style it once that settled.
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                double radius = root.TryFindResource("MacGlassMenuCornerRadius", out object? r) && r is CornerRadius cr
                    ? cr.TopLeft
                    : 10;
                StyleBehindWindowEffectViews(GetNSWindow(root), NSVisualEffectMaterialMenu, radius);
            }
            catch (Exception ex)
            {
                Logger.Warn("Could not apply the macOS menu material to a popup");
                Logger.Warn(ex);
            }
        });
    }

    private static void OnOpened(Window window)
    {
        // A transparent window without a granted backdrop shows whatever is behind it unblurred,
        // so drop back to the solid surfaces for the whole session if the request was refused.
        if (window.ActualTransparencyLevel != WindowTransparencyLevel.AcrylicBlur)
        {
            Logger.Warn($"macOS vibrancy was not granted (got {window.ActualTransparencyLevel}); using solid surfaces");
            FallBackToSolid(window);
            return;
        }

        try
        {
            UseAdaptiveMaterial(window);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not switch the macOS vibrancy material; keeping Avalonia's default");
            Logger.Warn(ex);
        }
    }

    private static void FallBackToSolid(Window window)
    {
        _glassConfirmedUnavailable = true;

        if (Application.Current is { } app)
        {
            for (int i = app.Resources.MergedDictionaries.Count - 1; i >= 0; i--)
            {
                if (app.Resources.MergedDictionaries[i] is MacGlassStyles)
                    app.Resources.MergedDictionaries.RemoveAt(i);
            }
            for (int i = app.Styles.Count - 1; i >= 0; i--)
            {
                if (app.Styles[i] is MacGlassControlStyles)
                    app.Styles.RemoveAt(i);
            }
        }

        window.TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
        window.Classes.Remove(GLASS_CLASS);
        window.ClearValue(Window.BackgroundProperty);
        if (window.TryFindResource("AppWindowBackground", window.ActualThemeVariant, out object? bg) && bg is IBrush brush)
            window.Background = brush;
    }

    // Avalonia creates its behind-window NSVisualEffectView with the deprecated, always-light
    // NSVisualEffectMaterialLight. A semantic material follows the window's light/dark appearance.
    private static void UseAdaptiveMaterial(Window window)
        => StyleBehindWindowEffectViews(GetNSWindow(window), NSVisualEffectMaterialSidebar, 0);

    // Sets the material of the window's behind-window NSVisualEffectViews; a non-zero radius also
    // rounds them (always-active, like native menus) and re-derives the window shadow from that shape.
    private static void StyleBehindWindowEffectViews(nint nsWindow, long material, double cornerRadius)
    {
        if (nsWindow == 0)
            return;

        nint effectViewClass = objc_getClass("NSVisualEffectView");
        nint contentView = objc_msgSend_IntPtr(nsWindow, sel_registerName("contentView"));
        nint subviews = objc_msgSend_IntPtr(contentView, sel_registerName("subviews"));
        long count = objc_msgSend_IntPtr(subviews, sel_registerName("count"));

        for (long i = 0; i < count; i++)
        {
            nint view = objc_msgSend_IntPtr_Long(subviews, sel_registerName("objectAtIndex:"), i);
            if (!objc_msgSend_Bool_IntPtr(view, sel_registerName("isKindOfClass:"), effectViewClass)
                || objc_msgSend_IntPtr(view, sel_registerName("blendingMode")) != NSVisualEffectBlendingModeBehindWindow)
                continue;

            objc_msgSend_IntPtr_Long(view, sel_registerName("setMaterial:"), material);
            if (cornerRadius <= 0)
                continue;

            objc_msgSend_IntPtr_Long(view, sel_registerName("setState:"), NSVisualEffectStateActive);
            objc_msgSend_IntPtr_Long(view, sel_registerName("setWantsLayer:"), 1);
            nint layer = objc_msgSend_IntPtr(view, sel_registerName("layer"));
            if (layer != 0)
            {
                objc_msgSend_IntPtr_Double(layer, sel_registerName("setCornerRadius:"), cornerRadius);
                objc_msgSend_IntPtr_Long(layer, sel_registerName("setMasksToBounds:"), 1);
            }
        }

        if (cornerRadius > 0)
        {
            objc_msgSend_IntPtr_Long(nsWindow, sel_registerName("setHasShadow:"), 1);
            objc_msgSend_IntPtr(nsWindow, sel_registerName("invalidateShadow"));
        }
    }

    private static nint GetNSWindow(TopLevel window)
    {
        if (window.TryGetPlatformHandle() is not { Handle: not 0 } handle)
            return 0;

        return handle.HandleDescriptor == "NSWindow"
            ? handle.Handle
            : objc_msgSend_IntPtr(handle.Handle, sel_registerName("window"));
    }

    // NSWorkspace's Reduce transparency and Increase contrast accessibility settings, read once per session.
    private static bool ShouldUseSolidSurfaces()
    {
        if (_useSolidSurfaces is { } cached)
            return cached;

        bool solid = false;
        try
        {
            nint workspace = objc_msgSend_IntPtr(objc_getClass("NSWorkspace"), sel_registerName("sharedWorkspace"));
            if (workspace != 0)
                solid = objc_msgSend_Bool(workspace, sel_registerName("accessibilityDisplayShouldReduceTransparency"))
                        || objc_msgSend_Bool(workspace, sel_registerName("accessibilityDisplayShouldIncreaseContrast"));
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not read the macOS transparency and contrast settings; assuming they are off");
            Logger.Warn(ex);
        }

        _useSolidSurfaces = solid;
        return solid;
    }

    [LibraryImport(ObjCRuntime, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(ObjCRuntime, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    private static partial nint objc_msgSend_IntPtr(nint receiver, nint selector);

    [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    private static partial nint objc_msgSend_IntPtr_Long(nint receiver, nint selector, long arg);

    [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    private static partial nint objc_msgSend_IntPtr_Double(nint receiver, nint selector, double arg);

    [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool objc_msgSend_Bool(nint receiver, nint selector);

    [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool objc_msgSend_Bool_IntPtr(nint receiver, nint selector, nint arg);
}
