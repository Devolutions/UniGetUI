using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// Fluent horizontal navigation whose direction is set explicitly via <see cref="Reverse"/>.
/// (TransitioningContentControl always reports forward navigation, so the caller toggles this
/// before changing content.) Reverse=false brings the incoming page from the right
/// (drill-in); Reverse=true brings it from the left (back navigation). WinUI navigation
/// moves content only a short distance and uses different enter/exit curves; applying one
/// symmetric easing to two full-width pages makes the transition read as linear.
/// Scrollbars are hidden for the duration so they don't drag across the view.
/// </summary>
public sealed class DirectionalSlideTransition : IPageTransition
{
    public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(250);

    private static readonly TimeSpan ExitDuration = TimeSpan.FromMilliseconds(167);
    private const double EnterOffset = 48d;
    private const double ExitOffset = 16d;

    public bool Reverse { get; set; }

    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        // Honor the OS "reduce motion" preference: swap pages instantly, no slide.
        if (MotionPreference.ReducedMotion)
        {
            if (from is not null) { from.IsVisible = false; from.RenderTransform = null; }
            to?.RenderTransform = null;
            return;
        }

        double sign = Reverse ? -1d : 1d;
        var hidden = new List<ScrollViewer>();
        HideScrollBars(from, hidden);
        HideScrollBars(to, hidden);

        try
        {
            var tasks = new List<Task>();
            if (from is not null)
                tasks.Add(Animate(from, 0d, -sign * ExitOffset, 1d, 0d,
                    ExitDuration, new SplineEasing(1d, 0d, 1d, 1d), cancellationToken));
            if (to is not null)
                tasks.Add(Animate(to, sign * EnterOffset, 0d, 0d, 1d,
                    Duration, new SplineEasing(0d, 0d, 0d, 1d), cancellationToken));
            await Task.WhenAll(tasks);
        }
        finally
        {
            foreach (var sv in hidden)
                sv.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }

        // Hide before clearing the transform so the outgoing page never snaps back on-screen.
        if (from is not null)
        {
            from.IsVisible = false;
            from.RenderTransform = null;
            from.Opacity = 1d;
        }
        if (to is not null)
        {
            to.RenderTransform = null;
            to.Opacity = 1d;
        }
    }

    private static void HideScrollBars(Visual? root, List<ScrollViewer> hidden)
    {
        if (root is null)
            return;

        foreach (var sv in root.GetVisualDescendants().OfType<ScrollViewer>())
        {
            if (sv.VerticalScrollBarVisibility == ScrollBarVisibility.Auto)
            {
                sv.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
                hidden.Add(sv);
            }
        }
    }

    private static Task Animate(
        Visual target,
        double fromX,
        double toX,
        double fromOpacity,
        double toOpacity,
        TimeSpan duration,
        Easing easing,
        CancellationToken cancellationToken)
    {
        var anim = new Animation
        {
            Duration = duration,
            Easing = easing,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters =
                    {
                        new Setter(TranslateTransform.XProperty, fromX),
                        new Setter(Visual.OpacityProperty, fromOpacity),
                    },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters =
                    {
                        new Setter(TranslateTransform.XProperty, toX),
                        new Setter(Visual.OpacityProperty, toOpacity),
                    },
                },
            },
        };
        return anim.RunAsync(target, cancellationToken);
    }
}
