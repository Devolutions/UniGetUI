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

    // NSWindowButton values of the close, minimise and zoom buttons.
    private const long NSWindowCloseButton = 0;
    private const long NSWindowMiniaturizeButton = 1;
    private const long NSWindowZoomButton = 2;

    // AppKit NSVisualEffectMaterial / NSVisualEffectBlendingMode / NSVisualEffectState values.
    private const long NSVisualEffectMaterialSidebar = 7;
    private const long NSVisualEffectMaterialMenu = 5;
    private const long NSVisualEffectStateActive = 1;
    private const long NSVisualEffectBlendingModeBehindWindow = 0;

    private static bool? _solidPreferred;
    private static bool _increaseContrast;
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
        UpdateAccentForeground(app);
        app.ActualThemeVariantChanged += (_, _) => UpdateAccentForeground(app);
        if (app.PlatformSettings is { } platform)
            platform.ColorValuesChanged += (_, _) => UpdateAccentForeground(app);
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
        // Drop the solid-mode resource binding (or a previous value) before switching.
        window.ClearValue(Window.BackgroundProperty);
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
        bool contrastBefore = _increaseContrast;
        _solidPreferred = null;
        if (IsVibrancyEnabled() == before)
        {
            if (_increaseContrast != contrastBefore && Application.Current is { } current)
                UpdateAccentForeground(current);
            return;
        }

        if (Application.Current is { } app)
        {
            UpdateSolidOverrides(app);
            UpdateAccentForeground(app);
        }
        if (_window is { } window)
        {
            ApplyVibrancyState(window);
            // The backdrop request is re-made, so confirm it was granted once it settles.
            Dispatcher.UIThread.Post(() => VerifyVibrancy(window), DispatcherPriority.Background);
        }
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

    // Text drawn on the accent is white unless that falls below 3:1 against it (the WCAG minimum for
    // UI components and bold text; 4.5:1 under Increase Contrast), as with orange, yellow or green.
    private static void UpdateAccentForeground(Application app)
    {
        if (!app.TryFindResource("SystemAccentColor", app.ActualThemeVariant, out object? value)
            || value is not Color accent)
            return;

        double minimum = _increaseContrast ? 4.5 : 3.0;
        app.Resources["MacAccentForegroundColor"] = ContrastRatio(Colors.White, accent) >= minimum
            ? Colors.White
            : Color.FromRgb(0x1D, 0x1D, 0x1F);
    }

    private static double ContrastRatio(Color a, Color b)
    {
        double la = RelativeLuminance(a), lb = RelativeLuminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    // WCAG 2 relative luminance of an sRGB colour.
    private static double RelativeLuminance(Color color)
    {
        static double Channel(byte value)
        {
            double c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
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
        nint contentView = objc_msgSend_IntPtr(nsWindow, Sel.ContentView);
        nint subviews = objc_msgSend_IntPtr(contentView, Sel.Subviews);
        long count = objc_msgSend_IntPtr(subviews, Sel.Count);

        for (long i = 0; i < count; i++)
        {
            nint view = objc_msgSend_IntPtr_Long(subviews, Sel.ObjectAtIndex, i);
            if (!objc_msgSend_Bool_IntPtr(view, Sel.IsKindOfClass, effectViewClass)
                || objc_msgSend_IntPtr(view, Sel.BlendingMode) != NSVisualEffectBlendingModeBehindWindow)
                continue;

            objc_msgSend_IntPtr_Long(view, Sel.SetMaterial, material);
            if (cornerRadius <= 0)
                continue;

            objc_msgSend_IntPtr_Long(view, Sel.SetState, NSVisualEffectStateActive);
            objc_msgSend_IntPtr_Long(view, Sel.SetWantsLayer, 1);
            nint layer = objc_msgSend_IntPtr(view, Sel.Layer);
            if (layer != 0)
            {
                objc_msgSend_IntPtr_Double(layer, Sel.SetCornerRadius, cornerRadius);
                objc_msgSend_IntPtr_Long(layer, Sel.SetMasksToBounds, 1);
            }
        }

        if (cornerRadius > 0)
        {
            objc_msgSend_IntPtr_Long(nsWindow, Sel.SetHasShadow, 1);
            objc_msgSend_IntPtr(nsWindow, Sel.InvalidateShadow);
        }
    }

    /// <summary>The zoom (green) button's frame in the window, in DIPs from the top-left corner, as macOS
    /// actually placed it; null when it can't be read. The title-bar row aligns to it.</summary>
    public static Rect? GetZoomButtonFrame(Window window)
    {
        if (!OperatingSystem.IsMacOS())
            return null;

        try
        {
            nint nsWindow = GetNSWindow(window);
            if (nsWindow == 0)
                return null;

            nint zoom = objc_msgSend_IntPtr_Long(nsWindow, Sel.StandardWindowButton, NSWindowZoomButton);
            nint contentView = objc_msgSend_IntPtr(nsWindow, Sel.ContentView);
            if (zoom == 0 || contentView == 0)
                return null;

            NSRect frame = ConvertRect(zoom, GetRect(zoom, Sel.Bounds), contentView);
            NSRect content = GetRect(contentView, Sel.Bounds);
            if (frame.Width <= 0 || content.Height <= 0)
                return null;

            // AppKit views measure from the bottom-left unless flipped; Avalonia measures from the top-left.
            double top = objc_msgSend_Bool(contentView, Sel.IsFlipped)
                ? frame.Y
                : content.Height - frame.Y - frame.Height;
            return new Rect(frame.X, top, frame.Width, frame.Height);
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not read the macOS window buttons' position; using the default title-bar inset");
            Logger.Warn(ex);
            return null;
        }
    }

    /// <summary>Moves the close/minimise/zoom buttons so the first one starts <paramref name="left"/> DIPs from
    /// the window's left edge and their centre line sits <paramref name="centerY"/> DIPs from its top. macOS
    /// lays them out again on resize, zoom and full screen, so call it again after each. Inside the floating
    /// glass sidebar they need the inset System Settings gives them, not the bare title-bar position.</summary>
    public static void SetWindowButtonsPosition(Window window, double left, double centerY)
    {
        if (!OperatingSystem.IsMacOS())
            return;

        try
        {
            nint nsWindow = GetNSWindow(window);
            if (nsWindow == 0)
                return;

            nint close = objc_msgSend_IntPtr_Long(nsWindow, Sel.StandardWindowButton, NSWindowCloseButton);
            nint miniaturize = objc_msgSend_IntPtr_Long(nsWindow, Sel.StandardWindowButton, NSWindowMiniaturizeButton);
            nint zoom = objc_msgSend_IntPtr_Long(nsWindow, Sel.StandardWindowButton, NSWindowZoomButton);
            if (close == 0 || miniaturize == 0 || zoom == 0)
                return;

            // The buttons live in the title-bar view, inside NSTitlebarContainerView; the container is
            // only as tall as the native title bar, so it grows to hold the buttons at the new centre line.
            nint titlebarView = objc_msgSend_IntPtr(close, Sel.Superview);
            nint container = titlebarView == 0 ? 0 : objc_msgSend_IntPtr(titlebarView, Sel.Superview);
            nint contentView = objc_msgSend_IntPtr(nsWindow, Sel.ContentView);
            if (container == 0 || contentView == 0)
                return;

            NSRect closeFrame = GetRect(close, Sel.Frame);
            NSRect miniaturizeFrame = GetRect(miniaturize, Sel.Frame);
            double spacing = miniaturizeFrame.X - closeFrame.X;
            double height = Math.Max(closeFrame.Height, 2 * centerY);
            NSRect content = GetRect(contentView, Sel.Bounds);
            NSRect current = GetRect(container, Sel.Frame);

            SetRect(container, Sel.SetFrame, new NSRect(current.X, content.Height - height, current.Width, height));
            SetRect(titlebarView, Sel.SetFrame, new NSRect(0, 0, current.Width, height));

            // Title-bar view coordinates start at the bottom-left; centre the buttons on the requested line.
            double y = height - centerY - (closeFrame.Height / 2);
            nint[] buttons = [close, miniaturize, zoom];
            for (int i = 0; i < buttons.Length; i++)
            {
                objc_msgSend_Void_Point(buttons[i], Sel.SetFrameOrigin, left + (i * spacing), y);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not move the macOS window buttons; keeping their default position");
            Logger.Warn(ex);
        }
    }

    private static void SetRect(nint receiver, nint selector, NSRect rect)
        => objc_msgSend_Void_Rect(receiver, selector, rect);

    // An NSRect comes back in registers on arm64 and through a hidden pointer (objc_msgSend_stret) on x64.
    private static NSRect GetRect(nint receiver, nint selector)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            objc_msgSend_stret_Rect(out NSRect result, receiver, selector);
            return result;
        }

        return objc_msgSend_Rect(receiver, selector);
    }

    private static NSRect ConvertRect(nint view, NSRect rect, nint toView)
    {
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
        {
            objc_msgSend_stret_Rect_Rect_IntPtr(out NSRect result, view, Sel.ConvertRectToView, rect, toView);
            return result;
        }

        return objc_msgSend_Rect_Rect_IntPtr(view, Sel.ConvertRectToView, rect, toView);
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NSRect(double x, double y, double width, double height)
    {
        public readonly double X = x;
        public readonly double Y = y;
        public readonly double Width = width;
        public readonly double Height = height;
    }

    private static nint GetNSWindow(TopLevel window)
    {
        if (window.TryGetPlatformHandle() is not { Handle: not 0 } handle)
            return 0;

        return handle.HandleDescriptor == "NSWindow"
            ? handle.Handle
            : objc_msgSend_IntPtr(handle.Handle, Sel.Window);
    }

    // NSWorkspace's Reduce transparency and Increase contrast settings; cleared on window activation.
    private static bool IsSolidPreferred()
    {
        if (_solidPreferred is { } cached)
            return cached;

        bool solid = false;
        try
        {
            nint workspace = objc_msgSend_IntPtr(objc_getClass("NSWorkspace"), Sel.SharedWorkspace);
            if (workspace != 0)
            {
                _increaseContrast = objc_msgSend_Bool(workspace, Sel.AccessibilityDisplayShouldIncreaseContrast);
                solid = _increaseContrast
                        || objc_msgSend_Bool(workspace, Sel.AccessibilityDisplayShouldReduceTransparency);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Could not read the macOS transparency and contrast settings; assuming they are off");
            Logger.Warn(ex);
        }

        _solidPreferred = solid;
        return solid;
    }

    // Selectors used on every popup and window, registered once on first use (macOS only).
    private static class Sel
    {
        public static readonly nint AccessibilityDisplayShouldIncreaseContrast = sel_registerName("accessibilityDisplayShouldIncreaseContrast");
        public static readonly nint AccessibilityDisplayShouldReduceTransparency = sel_registerName("accessibilityDisplayShouldReduceTransparency");
        public static readonly nint BlendingMode = sel_registerName("blendingMode");
        public static readonly nint Bounds = sel_registerName("bounds");
        public static readonly nint ContentView = sel_registerName("contentView");
        public static readonly nint ConvertRectToView = sel_registerName("convertRect:toView:");
        public static readonly nint Frame = sel_registerName("frame");
        public static readonly nint Count = sel_registerName("count");
        public static readonly nint InvalidateShadow = sel_registerName("invalidateShadow");
        public static readonly nint IsFlipped = sel_registerName("isFlipped");
        public static readonly nint IsKindOfClass = sel_registerName("isKindOfClass:");
        public static readonly nint Layer = sel_registerName("layer");
        public static readonly nint ObjectAtIndex = sel_registerName("objectAtIndex:");
        public static readonly nint SetCornerRadius = sel_registerName("setCornerRadius:");
        public static readonly nint SetFrame = sel_registerName("setFrame:");
        public static readonly nint SetFrameOrigin = sel_registerName("setFrameOrigin:");
        public static readonly nint SetHasShadow = sel_registerName("setHasShadow:");
        public static readonly nint SetMasksToBounds = sel_registerName("setMasksToBounds:");
        public static readonly nint SetMaterial = sel_registerName("setMaterial:");
        public static readonly nint SetState = sel_registerName("setState:");
        public static readonly nint SetWantsLayer = sel_registerName("setWantsLayer:");
        public static readonly nint SharedWorkspace = sel_registerName("sharedWorkspace");
        public static readonly nint StandardWindowButton = sel_registerName("standardWindowButton:");
        public static readonly nint Subviews = sel_registerName("subviews");
        public static readonly nint Superview = sel_registerName("superview");
        public static readonly nint Window = sel_registerName("window");
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
    private static partial NSRect objc_msgSend_Rect(nint receiver, nint selector);

    [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    private static partial NSRect objc_msgSend_Rect_Rect_IntPtr(nint receiver, nint selector, NSRect rect, nint view);

    [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    private static partial void objc_msgSend_Void_Rect(nint receiver, nint selector, NSRect rect);

    // NSPoint is two doubles, passed in registers on both arm64 and x64.
    [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    private static partial void objc_msgSend_Void_Point(nint receiver, nint selector, double x, double y);

    [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend_stret")]
    private static partial void objc_msgSend_stret_Rect(out NSRect result, nint receiver, nint selector);

    [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend_stret")]
    private static partial void objc_msgSend_stret_Rect_Rect_IntPtr(out NSRect result, nint receiver, nint selector, NSRect rect, nint view);

    [LibraryImport(ObjCRuntime, EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool objc_msgSend_Bool_IntPtr(nint receiver, nint selector, nint arg);
}
