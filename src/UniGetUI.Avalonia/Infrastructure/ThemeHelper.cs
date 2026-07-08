using Avalonia;
using Avalonia.Platform;
using Avalonia.Styling;

namespace UniGetUI.Avalonia.Infrastructure;

// Reliable theme resolution for code that bakes colors in C# (log/output views). ActualThemeVariant
// can read back as Default before it settles; resolve that against the OS theme so a resource lookup
// never falls back to the light-theme (near-black) value on a dark background (#5032).
public static class ThemeHelper
{
    public static ThemeVariant Variant
    {
        get
        {
            var app = Application.Current;
            var variant = app?.ActualThemeVariant;
            if (variant == ThemeVariant.Dark) return ThemeVariant.Dark;
            if (variant == ThemeVariant.Light) return ThemeVariant.Light;
            return app?.PlatformSettings?.GetColorValues().ThemeVariant == PlatformThemeVariant.Dark
                ? ThemeVariant.Dark
                : ThemeVariant.Light;
        }
    }

    public static bool IsDark => Variant == ThemeVariant.Dark;
}
