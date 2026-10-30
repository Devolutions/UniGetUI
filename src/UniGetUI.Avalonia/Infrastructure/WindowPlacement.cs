using Avalonia;

namespace UniGetUI.Avalonia.Infrastructure;

internal readonly record struct PlacementScreen(PixelRect Bounds, PixelRect WorkingArea, double Scaling, bool IsPrimary);

internal readonly record struct WindowPlacementResult(PixelPoint Position, double Width, double Height);

internal static class WindowPlacement
{
    public const double DefaultScreenFraction = 0.9;
    private const int VisibilityTolerance = 10;

    public static WindowPlacementResult ForDefault(
        PlacementScreen screen, double desiredWidth, double desiredHeight, double minWidth, double minHeight)
    {
        PixelRect area = screen.WorkingArea;
        double width = FitLength(desiredWidth, area.Width * DefaultScreenFraction, screen.Scaling, minWidth);
        double height = FitLength(desiredHeight, area.Height * DefaultScreenFraction, screen.Scaling, minHeight);
        int widthPx = ToPixels(width, screen.Scaling);
        int heightPx = ToPixels(height, screen.Scaling);
        var position = new PixelPoint(
            area.X + Math.Max(0, (area.Width - widthPx) / 2),
            area.Y + Math.Max(0, (area.Height - heightPx) / 2));
        return new WindowPlacementResult(position, width, height);
    }

    public static WindowPlacementResult FitSaved(
        IReadOnlyList<PlacementScreen> screens, PixelPoint position, double width, double height,
        double minWidth, double minHeight)
    {
        var saved = new WindowPlacementResult(position, width, height);
        if (screens.Count == 0 || IsFullyVisible(screens, position, width, height))
            return saved;

        double hostScaling = HostScaling(screens, position);
        var rect = new PixelRect(position.X, position.Y, ToPixels(width, hostScaling), ToPixels(height, hostScaling));
        PlacementScreen target = ClosestScreen(screens, rect);
        PixelRect area = target.WorkingArea;

        double fittedWidth = FitLength(width, area.Width, target.Scaling, minWidth);
        double fittedHeight = FitLength(height, area.Height, target.Scaling, minHeight);
        int widthPx = ToPixels(fittedWidth, target.Scaling);
        int heightPx = ToPixels(fittedHeight, target.Scaling);
        var fittedPosition = new PixelPoint(
            ClampStart(position.X, widthPx, area.X, area.Width),
            ClampStart(position.Y, heightPx, area.Y, area.Height));
        return new WindowPlacementResult(fittedPosition, fittedWidth, fittedHeight);
    }

    public static bool IsFullyVisible(IReadOnlyList<PlacementScreen> screens, PixelPoint position, double width, double height)
    {
        if (screens.Count == 0)
            return true;

        double hostScaling = HostScaling(screens, position);
        var inner = new PixelRect(
            position.X + VisibilityTolerance,
            position.Y + VisibilityTolerance,
            Math.Max(1, (int)(width * hostScaling) - 2 * VisibilityTolerance),
            Math.Max(1, (int)(height * hostScaling) - 2 * VisibilityTolerance));
        return IsCovered(screens, inner);
    }

    public static bool IsSamePlacement(WindowPlacementResult placement, PixelPoint position, double width, double height)
        => placement.Position == position
            && Math.Abs(placement.Width - width) < 1
            && Math.Abs(placement.Height - height) < 1;

    private static bool IsCovered(IReadOnlyList<PlacementScreen> screens, PixelRect rect)
    {
        var xs = new SortedSet<int> { rect.X, rect.Right };
        var ys = new SortedSet<int> { rect.Y, rect.Bottom };
        foreach (var screen in screens)
        {
            PixelRect area = screen.WorkingArea;
            if (area.X > rect.X && area.X < rect.Right) xs.Add(area.X);
            if (area.Right > rect.X && area.Right < rect.Right) xs.Add(area.Right);
            if (area.Y > rect.Y && area.Y < rect.Bottom) ys.Add(area.Y);
            if (area.Bottom > rect.Y && area.Bottom < rect.Bottom) ys.Add(area.Bottom);
        }

        int[] xEdges = xs.ToArray();
        int[] yEdges = ys.ToArray();
        for (int i = 0; i < xEdges.Length - 1; i++)
        {
            for (int j = 0; j < yEdges.Length - 1; j++)
            {
                var cell = new PixelRect(xEdges[i], yEdges[j], xEdges[i + 1] - xEdges[i], yEdges[j + 1] - yEdges[j]);
                bool inside = false;
                foreach (var screen in screens)
                {
                    if (screen.WorkingArea.Contains(cell))
                    {
                        inside = true;
                        break;
                    }
                }

                if (!inside)
                    return false;
            }
        }

        return true;
    }

    private static double HostScaling(IReadOnlyList<PlacementScreen> screens, PixelPoint position)
    {
        foreach (var screen in screens)
        {
            if (screen.Bounds.Contains(position))
                return screen.Scaling;
        }

        foreach (var screen in screens)
        {
            if (screen.IsPrimary)
                return screen.Scaling;
        }

        return 1.0;
    }

    private static PlacementScreen ClosestScreen(IReadOnlyList<PlacementScreen> screens, PixelRect rect)
    {
        PlacementScreen best = screens[0];
        long bestOverlap = -1;
        long bestDistance = long.MaxValue;
        long centerX = rect.X + rect.Width / 2;
        long centerY = rect.Y + rect.Height / 2;

        foreach (var screen in screens)
        {
            PixelRect area = screen.WorkingArea;
            PixelRect overlap = area.Intersect(rect);
            long overlapArea = (long)overlap.Width * overlap.Height;
            long dx = Math.Max(0, Math.Max(area.X - centerX, centerX - area.Right));
            long dy = Math.Max(0, Math.Max(area.Y - centerY, centerY - area.Bottom));
            long distance = dx * dx + dy * dy;

            if (overlapArea > bestOverlap || (overlapArea == bestOverlap && distance < bestDistance))
            {
                best = screen;
                bestOverlap = overlapArea;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static double FitLength(double desired, double availablePx, double scaling, double minimum)
    {
        double available = Math.Floor(availablePx / scaling);
        return Math.Max(minimum, Math.Min(desired, available));
    }

    private static int ToPixels(double dips, double scaling) => (int)Math.Round(dips * scaling);

    private static int ClampStart(int start, int length, int areaStart, int areaLength)
    {
        if (length >= areaLength)
            return areaStart;
        return Math.Clamp(start, areaStart, areaStart + areaLength - length);
    }
}
