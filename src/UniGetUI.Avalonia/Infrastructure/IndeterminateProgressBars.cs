using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class IndeterminateProgressBars
{
    public const string CompositorClass = "compositor-indeterminate";

    private static bool _installed;

    public static void Install(Application app)
    {
        if (_installed)
            return;
        _installed = true;

        RemoveThemeAnimations(app);

#if WINDOWS
        if (CompositorProgressBars.IsSupported())
        {
            Control.LoadedEvent.AddClassHandler<ProgressBar>(OnLoaded);
            Control.UnloadedEvent.AddClassHandler<ProgressBar>(OnUnloaded);
            ProgressBar.IsIndeterminateProperty.Changed.AddClassHandler<ProgressBar>(
                (_, _) => CompositorProgressBars.Refresh());
        }
#endif
    }

    public static int RemoveThemeAnimations(IResourceHost host)
    {
        if (!host.TryGetResource(typeof(ProgressBar), null, out object? resource)
            || resource is not ControlTheme theme)
        {
            return 0;
        }

        return RemoveInfiniteAnimations(theme.Children);
    }

    private static int RemoveInfiniteAnimations(IEnumerable<IStyle> styles)
    {
        int removed = 0;
        foreach (IStyle style in styles)
        {
            if (style is StyleBase styleBase)
            {
                for (int i = styleBase.Animations.Count - 1; i >= 0; i--)
                {
                    if (styleBase.Animations[i] is Animation animation
                        && animation.IterationCount == IterationCount.Infinite)
                    {
                        styleBase.Animations.RemoveAt(i);
                        removed++;
                    }
                }
            }

            removed += RemoveInfiniteAnimations(style.Children);
        }

        return removed;
    }

#if WINDOWS
    private static void OnLoaded(ProgressBar bar, RoutedEventArgs e) => CompositorProgressBars.Register(bar);

    private static void OnUnloaded(ProgressBar bar, RoutedEventArgs e) => CompositorProgressBars.Unregister(bar);
#endif
}
