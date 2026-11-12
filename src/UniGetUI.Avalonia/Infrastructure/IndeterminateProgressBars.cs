using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class IndeterminateProgressBars
{
    public const string CompositorClass = "compositor-indeterminate";

    private static bool _installed;
    private static IndeterminateBarRegistry? _registry;

    public static void Install(Application app)
    {
        if (_installed)
            return;
        _installed = true;

        RemoveThemeAnimations(app);

#if WINDOWS
        if (CompositorProgressBars.IsSupported())
            Attach(new IndeterminateBarRegistry(CompositorProgressBars.CreateSurface));
#endif
    }

    internal static void Attach(IndeterminateBarRegistry registry)
    {
        _registry = registry;
        Control.LoadedEvent.AddClassHandler<ProgressBar>(OnLoaded);
        Control.UnloadedEvent.AddClassHandler<ProgressBar>(OnUnloaded);
        ProgressBar.IsIndeterminateProperty.Changed.AddClassHandler<ProgressBar>(OnChanged);
        ProgressBar.OrientationProperty.Changed.AddClassHandler<ProgressBar>(OnChanged);
        TemplatedControl.ForegroundProperty.Changed.AddClassHandler<ProgressBar>(OnChanged);
        TemplatedControl.CornerRadiusProperty.Changed.AddClassHandler<ProgressBar>(OnChanged);
        Visual.OpacityProperty.Changed.AddClassHandler<Visual>(OnVisualMoved);
        Visual.RenderTransformProperty.Changed.AddClassHandler<Visual>(OnVisualMoved);
        TranslateTransform.XProperty.Changed.AddClassHandler<TranslateTransform>(OnTransformMoved);
        TranslateTransform.YProperty.Changed.AddClassHandler<TranslateTransform>(OnTransformMoved);
        ScaleTransform.ScaleXProperty.Changed.AddClassHandler<ScaleTransform>(OnTransformMoved);
        ScaleTransform.ScaleYProperty.Changed.AddClassHandler<ScaleTransform>(OnTransformMoved);
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

    private static void OnLoaded(ProgressBar bar, RoutedEventArgs e) => _registry?.Register(bar);

    private static void OnUnloaded(ProgressBar bar, RoutedEventArgs e) => _registry?.Unregister(bar);

    private static void OnChanged(ProgressBar bar, AvaloniaPropertyChangedEventArgs e) => _registry?.Refresh();

    private static void OnVisualMoved(Visual visual, AvaloniaPropertyChangedEventArgs e)
        => _registry?.NotifyAppearanceChanged(visual);

    private static void OnTransformMoved(Transform transform, AvaloniaPropertyChangedEventArgs e)
        => _registry?.NotifyAppearanceChanged(null);
}
