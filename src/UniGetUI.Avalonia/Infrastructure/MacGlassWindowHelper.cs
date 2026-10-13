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

/// <summary>macOS look: Styles.MacGlass* plus native vibrancy (AcrylicBlur) behind the sidebar and menus.
/// Under Reduce transparency or Increase contrast the look stays, with solid surfaces instead.</summary>
internal static partial class MacGlassWindowHelper
{
    /// <summary>Class set on a window while the macOS look is active; layout and styles key off it.</summary>
    public const string GLASS_CLASS = "mac-glass";

    private const string ObjCRuntime = "/usr/lib/libobjc.A.dylib";

    // AppKit NSVisualEffectMaterial / NSVisualEffectBlendingMode / NSVisualEffectState values.
    private const long NSVisualEffectMaterialSidebar = 7;
    private const long NSVisualEffectMaterialMenu = 5;
    private const long NSVisualEffectStateActive = 1;
    private const long NSVisualEffectBlendingModeBehindWindow = 0;

    private static bool? _solidPreferred;
    private static bool _vibrancyRefused;
    private static Window? _window;

    /// <summary>True on macOS: the macOS layout and control styles apply.</summary>
    public static bool IsGlassEnabled() => OperatingSystem.IsMacOS();

    /// <summary>True when the see-through vibrancy is used; false means solid surfaces.</summary>
    public static bool IsVibrancyEnabled()
        => IsGlassEnabled() && !_vibrancyRefused && !IsSolidPreferred();

    /// <summary>Merges the macOS styles and hooks popups. Call once from App.Initialize.</summary>
    public static void Initialize(Application app)
    {
        if (!IsGlassEnabled())
            return;

        app.Resources.MergedDictionaries.Add(new MacGlassStyles());
        app.Styles.Add(new MacGlassControlStyles());
        UpdateSolidOverrides(app);
        Control.LoadedEvent.AddClassHandler<PopupRoot>((root, _) => ApplyToPopup(root));
    }

    /// <summary>Applies the macOS look to the main window. Call before the window is shown.</summary>
    public static void Apply(Window window)
    {
        if (!IsGlassEnabled())
            return;

        _window = window;
        window.Classes.Add(GLASS_CLASS);
        ApplyVibrancyState(window);
        window.Opened += (_, _) => VerifyVibrancy(window);
        // Accessibility display settings can change while the app runs; pick them up on activation.
        window.Activated += (_, _) => RefreshAccessibilityPreferences();
    }

    private static void ApplyVibrancyState(Window window)
    {
        if (IsVibrancyEnabled())
        {
            window.TransparencyLevelHint = new[] { WindowTransparencyLevel.AcrylicBlur };
            window.Background = Brushes.Transparent;
            Dispatcher.UIThread.Post(() => UseSidebarMaterial(window));
        }
        else
        {
            window.TransparencyLevelHint = new[] { WindowTransparencyLevel.None };
            window.Bind(Window.BackgroundProperty, window.GetResourceObservable("MacGlassWindowBackground"));
        }
    }

    // A transparent window without a granted backdrop shows whatever is behind it unblurred,
    // so switch to solid surfaces for the rest of the session if the request was refused.
    private static void VerifyVibrancy(Window window)
    {
        if (!IsVibrancyEnabled() || window.ActualTransparencyLevel == WindowTransparencyLevel.AcrylicBlur)
            return;

        Logger.Warn($"macOS vibrancy was not granted (got {window.ActualTransparencyLevel}); using solid surfaces");
        _vibrancyRefused = true;
        ApplyVibrancyState(window);
        if (Application.Current is { } app)
            UpdateSolidOverrides(app);
    }

    private static void RefreshAccessibilityPreferences()
    {
        bool before = IsVibrancyEnabled();
        _solidPreferred = null;
        if (IsVibrancyEnabled() == before)
            return;

        if (Application.Current is { } app)
            UpdateSolidOverrides(app);
        if (_window is { } window)
            ApplyVibrancyState(window);
    }

    private static void UpdateSolidOverrides(Application app)
    {
        var dictionaries = app.Resources.MergedDictionaries;
        int existing = -1;
        for (int i = 0; i < dictionaries.Count; i++)
        {
            if (dictionaries[i] is MacSolidStyles)
                existing = i;
        }

        if (IsVibrancyEnabled() && existing >= 0)
            dictionaries.RemoveAt(existing);
        else if (!IsVibrancyEnabled() && existing < 0)
            dictionaries.Add(new MacSolidStyles());
    }

    private static void ApplyToPopup(PopupRoot root)
    {
        // Tooltips keep their plain opaque look, as on macOS; without vibrancy menus stay solid.
        if (!IsVibrancyEnabled() || root.GetVisualDescendants().Any(c => c is ToolTip))
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

    // Avalonia creates its behind-window NSVisualEffectView with the deprecated, always-light
    // NSVisualEffectMaterialLight. The sidebar material follows the window's light/dark appearance.
    private static void UseSidebarMaterial(Window window)
    {
        if (!IsVibrancyEnabled())
            return;

        try
        {
            StyleBehindWindowEffectViews(GetNSWindow(window), NSVisualEffectMaterialSidebar, 0);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not switch the macOS vibrancy material; keeping Avalonia's default");
            Logger.Warn(ex);
        }
    }

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

    // NSWorkspace's Reduce transparency and Increase contrast settings; cleared on window activation.
    private static bool IsSolidPreferred()
    {
        if (_solidPreferred is { } cached)
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

        _solidPreferred = solid;
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
