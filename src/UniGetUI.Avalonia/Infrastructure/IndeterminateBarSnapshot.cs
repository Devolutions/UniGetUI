using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace UniGetUI.Avalonia.Infrastructure;

internal readonly record struct IndicatorLane(double Origin, double Width, double Start, double End);

internal sealed record IndeterminateBarSnapshot(
    Rect Clip,
    Rect Track,
    double CornerRadius,
    double IndicatorTop,
    double IndicatorHeight,
    IndicatorLane First,
    IndicatorLane Second,
    Color Color,
    double Opacity)
{
    private const int CoverageSamples = 5;

    public static IndeterminateBarSnapshot? Capture(ProgressBar bar, Visual root)
    {
        if (!bar.IsIndeterminate
            || bar.Orientation != Orientation.Horizontal
            || !bar.IsEffectivelyVisible
            || !root.IsVisualAncestorOf(bar))
        {
            return null;
        }

        Border? track = null, first = null, second = null;
        foreach (Visual descendant in bar.GetVisualDescendants())
        {
            if (descendant is not Border border)
                continue;
            switch (border.Name)
            {
                case "ProgressBarRoot": track = border; break;
                case "IndeterminateProgressBarIndicator": first = border; break;
                case "IndeterminateProgressBarIndicator2": second = border; break;
            }
        }

        if (track is null || first is null || second is null
            || first.GetVisualParent() is not Visual lanes
            || first.Background is not ISolidColorBrush brush)
        {
            return null;
        }

        if (track.TransformToVisual(root) is not Matrix trackToRoot
            || lanes.TransformToVisual(root) is not Matrix lanesToRoot)
        {
            return null;
        }

        Rect trackRect = new Rect(track.Bounds.Size).TransformToAABB(trackToRoot);
        if (trackRect.Width <= 0 || trackRect.Height <= 0)
            return null;

        Rect clip = bar.GetTransformedBounds() is { } transformed
            ? transformed.Clip.Intersect(trackRect)
            : default;
        if (clip.Width <= 0 || clip.Height <= 0)
            return null;

        double opacity = EffectiveOpacity(lanes, root) * brush.Opacity * brush.Color.A / 255.0;
        if (opacity < 0.01)
            return null;

        double scale = lanesToRoot.M11;
        Rect firstRect = first.Bounds.TransformToAABB(lanesToRoot);
        Rect secondRect = second.Bounds.TransformToAABB(lanesToRoot);
        var settings = bar.TemplateSettings;

        var snapshot = new IndeterminateBarSnapshot(
            clip,
            trackRect,
            bar.CornerRadius.TopLeft * scale,
            firstRect.Y - trackRect.Y,
            firstRect.Height,
            new IndicatorLane(
                firstRect.X - trackRect.X,
                firstRect.Width,
                settings.ContainerAnimationStartPosition * scale,
                settings.ContainerAnimationEndPosition * scale),
            new IndicatorLane(
                secondRect.X - trackRect.X,
                secondRect.Width,
                settings.Container2AnimationStartPosition * scale,
                settings.Container2AnimationEndPosition * scale),
            Color.FromRgb(brush.Color.R, brush.Color.G, brush.Color.B),
            Math.Min(1, opacity));

        return IsCovered(bar, root, clip) ? null : snapshot;
    }

    public static bool IsCovered(ProgressBar bar, Visual root, Rect visible)
    {
        double y = visible.Y + visible.Height / 2;
        for (int i = 0; i < CoverageSamples; i++)
        {
            double x = visible.X + visible.Width * (i + 0.5) / CoverageSamples;
            foreach (Visual visual in root.GetVisualsAt(new Point(x, y), v => v.IsVisible))
            {
                if (visual == bar || bar.IsVisualAncestorOf(visual))
                    break;
                if (visual.IsVisualAncestorOf(bar) || DrawsNothing(visual, root))
                    continue;
                return true;
            }
        }

        return false;
    }

    private static bool DrawsNothing(Visual visual, Visual root)
    {
        if (EffectiveOpacity(visual, root) < 0.01)
            return true;

        return visual switch
        {
            Panel panel => IsClear(panel.Background),
            Border border => IsClear(border.Background) && (IsClear(border.BorderBrush) || border.BorderThickness == default),
            ContentPresenter presenter => IsClear(presenter.Background) && (IsClear(presenter.BorderBrush) || presenter.BorderThickness == default),
            _ => false,
        };
    }

    private static bool IsClear(IBrush? brush)
        => brush is null || brush.Opacity <= 0 || brush is ISolidColorBrush { Color.A: 0 };

    private static double EffectiveOpacity(Visual visual, Visual root)
    {
        double opacity = 1;
        for (Visual? current = visual; current is not null; current = current.GetVisualParent())
        {
            opacity *= current.Opacity;
            if (current == root)
                break;
        }

        return opacity;
    }
}
