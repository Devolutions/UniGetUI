using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace UniGetUI.Avalonia.Infrastructure;

internal enum BarCoverage { None, Partial, Full }

internal readonly record struct IndicatorLane(double Origin, double Width, double Start, double End);

internal readonly record struct RoundedShape(double OffsetX, double OffsetY, double Width, double Height, double Radius)
{
    public static bool Supports(CornerRadius corners)
        => Pattern(corners.TopLeft, corners.TopRight, corners.BottomRight, corners.BottomLeft) is not null;

    public static RoundedShape? For(double width, double height, CornerRadius corners)
    {
        CornerRadius fitted = Fit(width, height, corners);
        return Pattern(fitted.TopLeft, fitted.TopRight, fitted.BottomRight, fitted.BottomLeft) switch
        {
            Side.All => new RoundedShape(0, 0, width, height, fitted.TopLeft),
            Side.Bottom => new RoundedShape(0, -fitted.BottomLeft, width, height + fitted.BottomLeft, fitted.BottomLeft),
            Side.Top => new RoundedShape(0, 0, width, height + fitted.TopLeft, fitted.TopLeft),
            Side.Right => new RoundedShape(-fitted.TopRight, 0, width + fitted.TopRight, height, fitted.TopRight),
            Side.Left => new RoundedShape(0, 0, width + fitted.TopLeft, height, fitted.TopLeft),
            _ => null,
        };
    }

    public static CornerRadius Fit(double width, double height, CornerRadius corners)
    {
        double scale = 1;
        scale = Limit(scale, width, corners.TopLeft + corners.TopRight);
        scale = Limit(scale, width, corners.BottomLeft + corners.BottomRight);
        scale = Limit(scale, height, corners.TopLeft + corners.BottomLeft);
        scale = Limit(scale, height, corners.TopRight + corners.BottomRight);
        return scale >= 1
            ? corners
            : new CornerRadius(
                corners.TopLeft * scale,
                corners.TopRight * scale,
                corners.BottomRight * scale,
                corners.BottomLeft * scale);
    }

    private static double Limit(double scale, double side, double sum)
        => sum > side && sum > 0 ? Math.Min(scale, Math.Max(0, side) / sum) : scale;

    private enum Side { All, Top, Bottom, Left, Right }

    private static Side? Pattern(double topLeft, double topRight, double bottomRight, double bottomLeft)
    {
        if (topLeft == topRight && topRight == bottomRight && bottomRight == bottomLeft)
            return Side.All;
        if (topLeft == 0 && topRight == 0 && bottomLeft == bottomRight)
            return Side.Bottom;
        if (bottomLeft == 0 && bottomRight == 0 && topLeft == topRight)
            return Side.Top;
        if (topLeft == 0 && bottomLeft == 0 && topRight == bottomRight)
            return Side.Right;
        if (topRight == 0 && bottomRight == 0 && topLeft == bottomLeft)
            return Side.Left;
        return null;
    }
}

internal sealed record IndeterminateBarSnapshot(
    Rect Clip,
    Rect Track,
    RoundedShape TrackShape,
    double IndicatorTop,
    double IndicatorHeight,
    IndicatorLane First,
    RoundedShape FirstShape,
    IndicatorLane Second,
    RoundedShape SecondShape,
    Color Color,
    double Opacity)
{
    public static bool IsSupported(ProgressBar bar)
        => bar.Orientation == Orientation.Horizontal
           && RoundedShape.Supports(bar.CornerRadius)
           && FindParts(bar) is { First.Background: ISolidColorBrush };

    public static IndeterminateBarSnapshot? Capture(ProgressBar bar, Visual root)
        => Capture(bar, root, out _);

    public static IndeterminateBarSnapshot? Capture(ProgressBar bar, Visual root, out BarCoverage coverage)
    {
        coverage = BarCoverage.None;
        if (!bar.IsIndeterminate
            || !bar.IsEffectivelyVisible
            || !root.IsVisualAncestorOf(bar)
            || !IsSupported(bar)
            || FindParts(bar) is not { } parts
            || parts.First.Background is not ISolidColorBrush brush
            || parts.First.GetVisualParent() is not Visual lanes)
        {
            return null;
        }

        if (parts.Track.TransformToVisual(root) is not Matrix trackToRoot
            || lanes.TransformToVisual(root) is not Matrix lanesToRoot)
        {
            return null;
        }

        Rect trackRect = new Rect(parts.Track.Bounds.Size).TransformToAABB(trackToRoot);
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
        CornerRadius corners = new(
            bar.CornerRadius.TopLeft * scale,
            bar.CornerRadius.TopRight * scale,
            bar.CornerRadius.BottomRight * scale,
            bar.CornerRadius.BottomLeft * scale);
        Rect firstRect = parts.First.Bounds.TransformToAABB(lanesToRoot);
        Rect secondRect = parts.Second.Bounds.TransformToAABB(lanesToRoot);

        if (RoundedShape.For(trackRect.Width, trackRect.Height, corners) is not { } trackShape
            || RoundedShape.For(firstRect.Width, firstRect.Height, corners) is not { } firstShape
            || RoundedShape.For(secondRect.Width, secondRect.Height, corners) is not { } secondShape)
        {
            return null;
        }

        var settings = bar.TemplateSettings;
        var snapshot = new IndeterminateBarSnapshot(
            clip,
            trackRect,
            trackShape,
            firstRect.Y - trackRect.Y,
            firstRect.Height,
            new IndicatorLane(
                firstRect.X - trackRect.X,
                firstRect.Width,
                settings.ContainerAnimationStartPosition * scale,
                settings.ContainerAnimationEndPosition * scale),
            firstShape,
            new IndicatorLane(
                secondRect.X - trackRect.X,
                secondRect.Width,
                settings.Container2AnimationStartPosition * scale,
                settings.Container2AnimationEndPosition * scale),
            secondShape,
            Color.FromRgb(brush.Color.R, brush.Color.G, brush.Color.B),
            Math.Min(1, opacity));

        coverage = Coverage(bar, root, clip);
        return coverage == BarCoverage.None ? snapshot : null;
    }

    public static BarCoverage Coverage(ProgressBar bar, Visual root, Rect visible)
    {
        BarCoverage result = BarCoverage.None;
        Visual branch = bar;
        for (Visual? parent = bar.GetVisualParent(); parent is not null; parent = parent.GetVisualParent())
        {
            var siblings = parent.GetVisualChildren().ToList();
            var branchOrder = Order(branch, siblings);
            foreach (Visual sibling in siblings)
            {
                if (sibling == branch || Order(sibling, siblings).CompareTo(branchOrder) <= 0)
                    continue;
                result = Max(result, CoverageBy(sibling, root, visible, EffectiveOpacity(parent, root), new Rect(root.Bounds.Size)));
                if (result == BarCoverage.Full)
                    return result;
            }

            if (parent == root)
                break;
            branch = parent;
        }

        return result;
    }

    private static BarCoverage CoverageBy(Visual visual, Visual root, Rect visible, double opacity, Rect clip)
    {
        if (!visual.IsVisible || !clip.Intersects(visible))
            return BarCoverage.None;
        opacity *= visual.Opacity;
        if (opacity < 0.01 || visual.TransformToVisual(root) is not Matrix toRoot)
            return BarCoverage.None;

        Rect bounds = new Rect(visual.Bounds.Size).TransformToAABB(toRoot);
        Rect painted = bounds.Intersect(clip);
        bool intersects = painted.Intersects(visible);
        if (visual.ClipToBounds)
        {
            if (!intersects)
                return BarCoverage.None;
            clip = painted;
        }

        BarCoverage result = BarCoverage.None;
        if (intersects && Draws(visual, out bool opaque))
        {
            result = opaque && opacity >= 0.999 && painted.Contains(visible)
                ? BarCoverage.Full
                : BarCoverage.Partial;
            if (result == BarCoverage.Full)
                return result;
        }

        foreach (Visual child in visual.GetVisualChildren())
        {
            result = Max(result, CoverageBy(child, root, visible, opacity, clip));
            if (result == BarCoverage.Full)
                return result;
        }

        return result;
    }

    private static BarCoverage Max(BarCoverage a, BarCoverage b) => a > b ? a : b;

    public static bool IsDrawnAbove(Visual visual, Visual target)
    {
        var targetChain = new List<Visual>();
        for (Visual? current = target; current is not null; current = current.GetVisualParent())
            targetChain.Add(current);

        Visual branch = visual;
        Visual? parent = visual.GetVisualParent();
        while (parent is not null && !targetChain.Contains(parent))
        {
            branch = parent;
            parent = parent.GetVisualParent();
        }

        int common = parent is null ? -1 : targetChain.IndexOf(parent);
        if (common < 1)
            return true;

        Visual targetBranch = targetChain[common - 1];
        var siblings = parent!.GetVisualChildren().ToList();
        return Order(branch, siblings).CompareTo(Order(targetBranch, siblings)) > 0;
    }

    private static (int ZIndex, int Index) Order(Visual child, List<Visual> siblings)
        => (child.ZIndex, siblings.IndexOf(child));

    private static (Border Track, Border First, Border Second)? FindParts(ProgressBar bar)
    {
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

        return track is not null && first is not null && second is not null ? (track, first, second) : null;
    }

    private static bool Draws(Visual visual, out bool opaque)
    {
        opaque = false;
        switch (visual)
        {
            case Panel panel:
                opaque = IsOpaque(panel.Background);
                return !IsClear(panel.Background);
            case Border border:
                opaque = IsOpaque(border.Background);
                return !IsClear(border.Background) || (!IsClear(border.BorderBrush) && border.BorderThickness != default);
            case ContentPresenter presenter:
                opaque = IsOpaque(presenter.Background);
                return !IsClear(presenter.Background) || (!IsClear(presenter.BorderBrush) && presenter.BorderThickness != default);
            case TemplatedControl:
            case Decorator:
                return false;
            default:
                return true;
        }
    }

    private static bool IsClear(IBrush? brush)
        => brush is null || brush.Opacity <= 0 || brush is ISolidColorBrush { Color.A: 0 };

    private static bool IsOpaque(IBrush? brush)
        => brush is ISolidColorBrush { Color.A: 255, Opacity: >= 1 };

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
