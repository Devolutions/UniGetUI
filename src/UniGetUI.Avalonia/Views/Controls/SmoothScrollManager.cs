using System;
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

    private static readonly ConditionalWeakTable<Control, SmoothScrollManager> _animators = new();
    private static IDisposable? _classHandler;

    private readonly Control _target;
    private Vector _velocity;
    private TimeSpan? _lastFrame;
    private bool _frameRequested;

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

        // DataGrid implements scrolling itself rather than through an ancestor ScrollViewer.
        // Resolve it first to preserve the package list's virtualization-aware inertia path.
        if (source.FindAncestorOfType<DataGrid>(includeSelf: true) is { } grid)
        {
            _animators.GetValue(grid, static control => new(control)).AddImpulse(e.Delta);
            e.Handled = true;
            return;
        }

        ScrollViewer? horizontalTarget = FindScrollTarget(source, e.Delta.X, horizontal: true);
        ScrollViewer? verticalTarget = FindScrollTarget(source, e.Delta.Y, horizontal: false);
        if (horizontalTarget is null && verticalTarget is null) return;

        if (horizontalTarget is not null && ReferenceEquals(horizontalTarget, verticalTarget))
        {
            _animators.GetValue(horizontalTarget, static control => new(control)).AddImpulse(e.Delta);
        }
        else
        {
            if (horizontalTarget is not null)
                _animators.GetValue(horizontalTarget, static control => new(control))
                    .AddImpulse(new Vector(e.Delta.X, 0));
            if (verticalTarget is not null)
                _animators.GetValue(verticalTarget, static control => new(control))
                    .AddImpulse(new Vector(0, e.Delta.Y));
        }
        e.Handled = true;
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
        if (_velocity == default) return;

        double dt = _lastFrame is { } last ? (now - last).TotalSeconds : 1.0 / 60.0;
        _lastFrame = now;
        if (dt <= 0) dt = 1.0 / 60.0;
        dt = Math.Min(dt, MaximumFrameTime);

        // Integrate the exponential velocity curve over the frame. This makes travel independent
        // of refresh rate, unlike applying a fixed fraction on every animation callback.
        var frame = SmoothScrollPhysics.Integrate(_velocity.X, _velocity.Y, dt);
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
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "UpdateScroll")]
    private static extern bool UpdateDataGridScroll(DataGrid grid, Vector offset);
}
