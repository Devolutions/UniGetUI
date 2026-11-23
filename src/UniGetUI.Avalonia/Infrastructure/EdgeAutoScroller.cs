using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace UniGetUI.Avalonia.Infrastructure;

internal sealed class EdgeAutoScroller
{
    private const double EdgeSize = 40d;
    private const double MaxStep = 18d;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(30) };
    private readonly Action _scrolled;
    private ScrollViewer? _viewer;
    private double _step;

    public EdgeAutoScroller(Action scrolled)
    {
        _scrolled = scrolled;
        _timer.Tick += (_, _) => Step();
    }

    internal double PendingStep => _step;

    public void Update(ScrollViewer? viewer, TopLevel topLevel, Point pointInTopLevel)
    {
        _viewer = viewer;
        _step = 0d;
        if (viewer is not null
            && viewer.IsEffectivelyVisible
            && topLevel.TranslatePoint(pointInTopLevel, viewer) is { } point
            && point.X >= 0d && point.X <= viewer.Bounds.Width)
        {
            double height = viewer.Bounds.Height;
            if (point.Y > -EdgeSize && point.Y < EdgeSize)
                _step = -MaxStep * (EdgeSize - Math.Max(point.Y, 0d)) / EdgeSize;
            else if (point.Y > height - EdgeSize && point.Y < height + EdgeSize)
                _step = MaxStep * (Math.Min(point.Y, height) - (height - EdgeSize)) / EdgeSize;
        }

        if (_step == 0d)
            _timer.Stop();
        else if (!_timer.IsEnabled)
            _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _viewer = null;
        _step = 0d;
    }

    internal void Step()
    {
        if (_viewer is not { } viewer || _step == 0d)
        {
            _timer.Stop();
            return;
        }

        double max = Math.Max(0d, viewer.Extent.Height - viewer.Viewport.Height);
        double offset = Math.Clamp(viewer.Offset.Y + _step, 0d, max);
        if (offset == viewer.Offset.Y)
            return;

        viewer.Offset = new Vector(viewer.Offset.X, offset);
        _scrolled();
    }
}
