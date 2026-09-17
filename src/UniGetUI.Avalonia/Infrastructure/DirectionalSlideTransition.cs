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
/// WinUI-style horizontal page slide whose direction is set explicitly via <see cref="Reverse"/>.
/// (TransitioningContentControl always reports forward navigation, so the caller toggles this
/// before changing content.) Reverse=false slides the incoming page in from the right;
/// Reverse=true slides it in from the left. This mirrors the former WinUI
/// SlideNavigationTransitionInfo used by SettingsBasePage.
/// Scrollbars are hidden for the duration so they don't drag across the view.
/// </summary>
public sealed class DirectionalSlideTransition : IPageTransition
{
    public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(300);

    private static readonly SplineEasing SlideEasing = new(0.1d, 0.9d, 0.2d, 1d);

    public bool Reverse { get; set; }

    /// <summary>
    /// Uses the direction supplied by controls such as Carousel. TransitioningContentControl does
    /// not provide a useful direction, so settings and About continue to set <see cref="Reverse"/>.
    /// </summary>
    public bool FollowForwardDirection { get; set; }

    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        // Honor the OS "reduce motion" preference: swap pages instantly, no slide.
        if (MotionPreference.ReducedMotion)
        {
            if (from is not null) { from.IsVisible = false; from.RenderTransform = null; }
            to?.RenderTransform = null;
            return;
        }

        double sign = (Reverse || (FollowForwardDirection && !forward)) ? -1d : 1d;
        double width = (to ?? from)?.GetVisualParent()?.Bounds.Width
                       ?? (to ?? from)?.Bounds.Width ?? 0d;
        var hidden = new List<ScrollViewer>();
        HideScrollBars(from, hidden);
        HideScrollBars(to, hidden);

        try
        {
            var tasks = new List<Task>();
            if (from is not null)
                tasks.Add(Slide(from, 0d, -sign * width, cancellationToken));
            if (to is not null)
                tasks.Add(Slide(to, sign * width, 0d, cancellationToken));
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
        }
        if (to is not null)
            to.RenderTransform = null;
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

    private Task Slide(Visual target, double fromX, double toX, CancellationToken cancellationToken)
    {
        var anim = new Animation
        {
            Duration = Duration,
            Easing = SlideEasing,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(TranslateTransform.XProperty, fromX) } },
                new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(TranslateTransform.XProperty, toX) } },
            },
        };
        return anim.RunAsync(target, cancellationToken);
    }
}
