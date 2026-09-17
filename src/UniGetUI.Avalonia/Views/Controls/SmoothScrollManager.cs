using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using UniGetUI.Avalonia.Infrastructure;

namespace UniGetUI.Avalonia.Views.Controls;

/// <summary>
/// Applies velocity-based wheel inertia to every scroll host in the application. Registration is
/// performed once at the TopLevel class-handler level, so dynamically created windows and controls
/// participate without per-view wiring.
/// </summary>
public sealed class SmoothScrollManager
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<SmoothScrollManager, Control, bool>(
            "IsEnabled",
            defaultValue: true,
            inherits: true);

    private const double MaximumFrameTime = 1.0 / 30.0;
    private const double StopVelocity = 4.0;
    private const double PrecisionInertiaDelay = 0.05;
    private const double PrecisionGestureRetention = 0.12;
    private const double MaximumPrecisionSampleInterval = 0.08;
    private const double PrecisionVelocityBlend = 0.5;
    private const double MaximumPrecisionVelocity = 3600.0;

    private static readonly ConditionalWeakTable<Control, SmoothScrollManager> _animators = new();
    private static readonly ConditionalWeakTable<TopLevel, PrecisionInputState> _precisionInputStates = new();
    private static IDisposable? _classHandler;

    private readonly Control _target;
    private Vector _velocity;
    private TimeSpan? _lastFrame;
    private bool _frameRequested;
    private long _lastPrecisionInputTimestamp;
    private bool _precisionInertiaPending;

    private SmoothScrollManager(Control target)
    {
        _target = target;
    }

    public static void Install()
    {
        _classHandler ??= InputElement.PointerWheelChangedEvent.AddClassHandler<TopLevel>(
            OnTopLevelWheel,
            RoutingStrategies.Tunnel);
    }

    public static bool GetIsEnabled(Control control) => control.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Control control, bool value) => control.SetValue(IsEnabledProperty, value);

    private static void OnTopLevelWheel(TopLevel topLevel, PointerWheelEventArgs e)
    {
        // Modified wheel gestures may have control-specific meanings such as zooming.
        if (e.Delta == default || e.KeyModifiers != KeyModifiers.None || MotionPreference.ReducedMotion) return;
        if (e.Source is not Visual source || HasNativeWheelInteraction(source)) return;
        Control? sourceControl = source.FindAncestorOfType<Control>(includeSelf: true);
        if (sourceControl is null || !GetIsEnabled(sourceControl)) return;
        bool isPrecisionTouchpadScroll = IsPrecisionTouchpadScroll(topLevel, e.Delta);

        // Carousel owns horizontal page gestures. Let it receive the complete event instead of
        // consuming a small incidental Y component in an ancestor vertical ScrollViewer.
        if (isPrecisionTouchpadScroll && Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) &&
            source.FindAncestorOfType<Carousel>(includeSelf: true) is not null)
            return;

        // DataGrid implements scrolling itself rather than through an ancestor ScrollViewer.
        // Resolve it first to preserve the package list's virtualization-aware inertia path.
        if (source.FindAncestorOfType<DataGrid>(includeSelf: true) is { } grid)
        {
            _animators.GetValue(grid, static control => new(control))
                .ApplyInput(e.Delta, isPrecisionTouchpadScroll);
            e.Handled = true;
            return;
        }

        ScrollViewer? horizontalTarget = FindScrollTarget(source, e.Delta.X, horizontal: true);
        ScrollViewer? verticalTarget = FindScrollTarget(source, e.Delta.Y, horizontal: false);
        if (horizontalTarget is null && verticalTarget is null)
        {
            // Do not fall back to Avalonia's much larger conventional wheel step at a hard edge.
            // WinUI keeps the manipulation owned by the current scroll chain while its boundary
            // response settles; consuming it here gives the same stable stop and cancels our tail.
            if (isPrecisionTouchpadScroll && StopPrecisionInputAtBoundary(source, e.Delta))
                e.Handled = true;
            return;
        }

        if (horizontalTarget is not null && ReferenceEquals(horizontalTarget, verticalTarget))
        {
            _animators.GetValue(horizontalTarget, static control => new(control))
                .ApplyInput(e.Delta, isPrecisionTouchpadScroll);
        }
        else
        {
            if (horizontalTarget is not null)
                _animators.GetValue(horizontalTarget, static control => new(control))
                    .ApplyInput(new Vector(e.Delta.X, 0), isPrecisionTouchpadScroll);
            if (verticalTarget is not null)
                _animators.GetValue(verticalTarget, static control => new(control))
                    .ApplyInput(new Vector(0, e.Delta.Y), isPrecisionTouchpadScroll);
        }
        e.Handled = true;
    }

    private void ApplyInput(Vector delta, bool isPrecisionTouchpadScroll)
    {
        if (isPrecisionTouchpadScroll)
        {
            // Keep the viewport attached to the fingers while the native delta stream is active.
            // WinUI's compositor manipulation maps less aggressively than Avalonia's conventional
            // 48-DIP wheel step, so precision input uses its own sensitivity. A short inertia tail
            // is armed below, but does not begin until native input has gone quiet.
            Vector step = delta * SmoothScrollPhysics.PrecisionTouchpadDistance;
            if (!ScrollBy(step))
            {
                Stop();
                return;
            }

            TrackPrecisionVelocity(step);
            return;
        }

        if (_lastPrecisionInputTimestamp != 0) Stop();
        AddImpulse(delta);
    }

    private void TrackPrecisionVelocity(Vector step)
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastPrecisionInputTimestamp != 0)
        {
            double elapsed = Stopwatch.GetElapsedTime(_lastPrecisionInputTimestamp, now).TotalSeconds;
            if (elapsed > 0 && elapsed <= MaximumPrecisionSampleInterval)
            {
                Vector sample = step / elapsed;
                _velocity = new Vector(
                    BlendPrecisionVelocity(_velocity.X, sample.X),
                    BlendPrecisionVelocity(_velocity.Y, sample.Y));
            }
            else
            {
                _velocity = default;
            }
        }
        else
        {
            // One isolated fractional event is not enough to infer a fling velocity reliably.
            _velocity = default;
        }

        _lastPrecisionInputTimestamp = now;
        _precisionInertiaPending = true;
        _lastFrame = null;
        RequestFrame();
    }

    private static double BlendPrecisionVelocity(double current, double sample)
    {
        if (sample == 0) return 0;
        sample = Math.Clamp(sample, -MaximumPrecisionVelocity, MaximumPrecisionVelocity);
        if (current == 0 || Math.Sign(current) != Math.Sign(sample)) return sample;
        return current + (sample - current) * PrecisionVelocityBlend;
    }

    private static bool IsPrecisionTouchpadScroll(TopLevel topLevel, Vector delta)
    {
        PrecisionInputState state = _precisionInputStates.GetValue(topLevel, static _ => new());
        long now = Stopwatch.GetTimestamp();
        if (IsPrecisionTouchpadDelta(delta.X) || IsPrecisionTouchpadDelta(delta.Y))
        {
            state.LastPrecisionTimestamp = now;
            return true;
        }

        // A precision stream can occasionally land exactly on an integer. Retain the device
        // classification briefly so one such sample does not switch to the mouse inertia path.
        if (state.LastPrecisionTimestamp == 0 ||
            Stopwatch.GetElapsedTime(state.LastPrecisionTimestamp, now).TotalSeconds > PrecisionGestureRetention)
            return false;

        state.LastPrecisionTimestamp = now;
        return true;
    }

    private static bool IsPrecisionTouchpadDelta(double delta)
    {
        // Avalonia exposes mouse wheels and precision touchpads through the same event. Traditional
        // wheel deltas are integral notches, whereas precision touchpads emit fractional deltas.
        double absoluteDelta = Math.Abs(delta);
        return absoluteDelta - (int)absoluteDelta > 1e-6;
    }

    private void AddImpulse(Vector delta)
    {
        if (_velocity == default) _lastFrame = null; // fresh gesture: don't carry a stale timestamp
        (double x, double y) = SmoothScrollPhysics.AddImpulse(
            _velocity.X, _velocity.Y, delta.X, delta.Y);
        _velocity = new Vector(x, y);
        RequestFrame();
    }

    private static bool HasNativeWheelInteraction(Visual source)
    {
        for (Visual? current = source; current is not null; current = current.GetVisualParent())
        {
            if (current is ScrollContentPresenter) return false;
            if (current is ComboBox or ButtonSpinner or ScrollBar or CalendarDatePicker or Calendar)
                return true;
        }
        return false;
    }

    private static ScrollViewer? FindScrollTarget(Visual source, double delta, bool horizontal)
    {
        if (delta == 0) return null;
        for (Visual? current = source; current is not null; current = current.GetVisualParent())
        {
            if (current is not ScrollViewer viewer) continue;
            if (CanScroll(viewer, delta, horizontal)) return viewer;
            if (!viewer.IsScrollChainingEnabled) return null;
        }
        return null;
    }

    private static bool StopPrecisionInputAtBoundary(Visual source, Vector delta)
    {
        bool foundBoundary = false;
        if (delta.X != 0 && FindBoundaryScrollHost(source, horizontal: true) is { } horizontalHost)
        {
            _animators.GetValue(horizontalHost, static control => new(control)).Stop();
            foundBoundary = true;
        }

        if (delta.Y != 0 && FindBoundaryScrollHost(source, horizontal: false) is { } verticalHost)
        {
            _animators.GetValue(verticalHost, static control => new(control)).Stop();
            foundBoundary = true;
        }

        return foundBoundary;
    }

    private static ScrollViewer? FindBoundaryScrollHost(Visual source, bool horizontal)
    {
        for (Visual? current = source; current is not null; current = current.GetVisualParent())
        {
            if (current is not ScrollViewer viewer) continue;

            ScrollBarVisibility visibility = horizontal
                ? viewer.HorizontalScrollBarVisibility
                : viewer.VerticalScrollBarVisibility;
            double extent = horizontal ? viewer.Extent.Width : viewer.Extent.Height;
            double viewport = horizontal ? viewer.Viewport.Width : viewer.Viewport.Height;
            if (visibility != ScrollBarVisibility.Disabled && extent > viewport)
                return viewer;

            if (!viewer.IsScrollChainingEnabled) return null;
        }

        return null;
    }

    private static bool CanScroll(ScrollViewer viewer, double delta, bool horizontal)
    {
        double offset = horizontal ? viewer.Offset.X : viewer.Offset.Y;
        double extent = horizontal ? viewer.Extent.Width : viewer.Extent.Height;
        double viewport = horizontal ? viewer.Viewport.Width : viewer.Viewport.Height;
        double maximum = Math.Max(0, extent - viewport);
        return delta > 0 ? offset > 0 : offset < maximum;
    }

    private void RequestFrame()
    {
        if (_frameRequested) return;
        if (TopLevel.GetTopLevel(_target) is not { } top) { Stop(); return; }
        _frameRequested = true;
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan now)
    {
        _frameRequested = false;

        if (_precisionInertiaPending)
        {
            double quietTime = Stopwatch.GetElapsedTime(_lastPrecisionInputTimestamp).TotalSeconds;
            if (quietTime < PrecisionInertiaDelay)
            {
                RequestFrame();
                return;
            }

            _precisionInertiaPending = false;
            _lastFrame = now;
            if (_velocity == default)
            {
                Stop();
                return;
            }

            RequestFrame();
            return;
        }

        if (_velocity == default) return;

        double dt = _lastFrame is { } last ? (now - last).TotalSeconds : 1.0 / 60.0;
        _lastFrame = now;
        if (dt <= 0) dt = 1.0 / 60.0;
        dt = Math.Min(dt, MaximumFrameTime);

        // Integrate the exponential velocity curve over the frame. This makes travel independent
        // of refresh rate, unlike applying a fixed fraction on every animation callback.
        var frame = _lastPrecisionInputTimestamp == 0
            ? SmoothScrollPhysics.Integrate(_velocity.X, _velocity.Y, dt)
            : SmoothScrollPhysics.IntegratePrecisionTouchpad(_velocity.X, _velocity.Y, dt);
        var step = new Vector(frame.StepX, frame.StepY);

        bool scrolled = ScrollBy(step);
        _velocity = new Vector(frame.VelocityX, frame.VelocityY);

        if (!scrolled || (_velocity.X * _velocity.X + _velocity.Y * _velocity.Y) < StopVelocity * StopVelocity)
        {
            Stop();
            return;
        }
        RequestFrame();
    }

    private bool ScrollBy(Vector step)
    {
        if (_target is DataGrid grid)
            return UpdateDataGridScroll(grid, step);

        var viewer = (ScrollViewer)_target;
        Vector oldOffset = viewer.Offset;
        double maximumX = Math.Max(0, viewer.Extent.Width - viewer.Viewport.Width);
        double maximumY = Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
        double x = Math.Clamp(oldOffset.X - step.X, 0, maximumX);
        double y = Math.Clamp(oldOffset.Y - step.Y, 0, maximumY);
        if (x == oldOffset.X && y == oldOffset.Y) return false;

        viewer.Offset = new Vector(x, y);
        return true;
    }

    private void Stop()
    {
        _velocity = default;
        _lastFrame = null;
        _lastPrecisionInputTimestamp = 0;
        _precisionInertiaPending = false;
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "UpdateScroll")]
    private static extern bool UpdateDataGridScroll(DataGrid grid, Vector offset);

    private sealed class PrecisionInputState
    {
        internal long LastPrecisionTimestamp;
    }
}
