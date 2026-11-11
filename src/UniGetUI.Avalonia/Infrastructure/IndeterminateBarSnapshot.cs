using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace UniGetUI.Avalonia.Infrastructure;

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
    private const int CoverageSamples = 5;

    public static bool IsSupported(ProgressBar bar)
        => bar.Orientation == Orientation.Horizontal
           && RoundedShape.Supports(bar.CornerRadius)
           && FindParts(bar) is { First.Background: ISolidColorBrush };

    public static IndeterminateBarSnapshot? Capture(ProgressBar bar, Visual root)
    {
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
                if (visual.IsVisualAncestorOf(bar) || !IsDrawnAbove(visual, bar) || DrawsNothing(visual, root))
                    continue;
                return true;
            }
        }

        return false;
    }

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
