using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
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

    public static readonly AttachedProperty<bool> HandlesPrecisionWheelInputProperty =
        AvaloniaProperty.RegisterAttached<SmoothScrollManager, Control, bool>(
            "HandlesPrecisionWheelInput",
            defaultValue: false,
            inherits: true);

    private const double MaximumFrameTime = 1.0 / 30.0;
    private const double StopVelocity = 4.0;
    private const double PrecisionGestureRetention = 0.12;
    private const double OverpanResistance = 0.4;
    private const double MaximumOverpan = 48.0;
    private const double OverpanReleaseDelay = 0.04;
    private const double OverpanSpringStrength = 280.0;
    private const double OverpanSpringDamping = 24.0;
    private const double OverpanStopDistance = 0.1;
    private const double OverpanStopVelocity = 2.0;

    private static readonly ConditionalWeakTable<Control, SmoothScrollManager> _animators = new();
    private static readonly ConditionalWeakTable<TopLevel, PrecisionInputState> _precisionInputStates = new();
    private static IDisposable? _classHandler;

    private readonly Control _target;
    private Vector _velocity;
    private Vector _pendingPrecisionStep;
    private TimeSpan? _lastFrame;
    private bool _frameRequested;
    private long _lastPrecisionInputTimestamp;
    private Vector _overpan;
    private Vector _overpanVelocity;
    private Visual? _overpanVisual;
    private ITransform? _originalOverpanTransform;
    private ITransform? _appliedOverpanTransform;
    private TranslateTransform? _overpanTranslation;
    private readonly List<(Visual Visual, ITransform? Original, ITransform Applied)> _overpanRows = new();
    private bool _originalRowsClipToBounds;
    private bool _overpanRowsClipCaptured;
    private bool _overpanUsesRows;

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
    public static bool GetHandlesPrecisionWheelInput(Control control) =>
        control.GetValue(HandlesPrecisionWheelInputProperty);
    public static void SetHandlesPrecisionWheelInput(Control control, bool value) =>
        control.SetValue(HandlesPrecisionWheelInputProperty, value);

    private static void OnTopLevelWheel(TopLevel topLevel, PointerWheelEventArgs e)
    {
        // Modified wheel gestures may have control-specific meanings such as zooming.
        if (e.Delta == default || e.KeyModifiers != KeyModifiers.None || MotionPreference.ReducedMotion) return;
        if (e.Source is not Visual source || HasNativeWheelInteraction(source)) return;
        Control? sourceControl = source.FindAncestorOfType<Control>(includeSelf: true);
        if (sourceControl is null || !GetIsEnabled(sourceControl)) return;
        bool isPrecisionTouchpadScroll = IsPrecisionTouchpadScroll(topLevel, e.Delta);

        // A control that owns precision-wheel gestures must see the complete 2D delta stream.
        // It can resolve intent from the accumulated vector and route an unused axis back here.
        if (isPrecisionTouchpadScroll && GetHandlesPrecisionWheelInput(sourceControl))
            return;

        if (RouteInput(source, e.Delta, isPrecisionTouchpadScroll))
            e.Handled = true;
    }

    internal static bool RoutePrecisionInput(Visual source, Vector delta) =>
        RouteInput(source, delta, isPrecisionTouchpadScroll: true);

    private static bool RouteInput(Visual source, Vector delta, bool isPrecisionTouchpadScroll)
    {
        // DataGrid implements scrolling itself rather than through an ancestor ScrollViewer.
        // Resolve it first to preserve the package list's virtualization-aware inertia path.
        if (source.FindAncestorOfType<DataGrid>(includeSelf: true) is { } grid)
        {
            _animators.GetValue(grid, static control => new(control))
                .ApplyInput(delta, isPrecisionTouchpadScroll);
            return true;
        }

        ScrollViewer? horizontalTarget = FindScrollTarget(source, delta.X, horizontal: true);
        ScrollViewer? verticalTarget = FindScrollTarget(source, delta.Y, horizontal: false);
        bool handledBoundary = false;
        if (isPrecisionTouchpadScroll)
        {
            Vector boundaryDelta = new(
                horizontalTarget is null ? delta.X : 0,
                verticalTarget is null ? delta.Y : 0);
            if (boundaryDelta != default)
                handledBoundary = ApplyPrecisionInputAtBoundary(source, boundaryDelta);
        }

        if (horizontalTarget is null && verticalTarget is null)
        {
            // Keep a precision manipulation owned by the current scroll chain at a hard edge.
            // The manager turns only the unconsumed part into a resisted visual overpan.
            return handledBoundary;
        }

        if (horizontalTarget is not null && ReferenceEquals(horizontalTarget, verticalTarget))
        {
            _animators.GetValue(horizontalTarget, static control => new(control))
                .ApplyInput(delta, isPrecisionTouchpadScroll);
        }
        else
        {
            if (horizontalTarget is not null)
                _animators.GetValue(horizontalTarget, static control => new(control))
                    .ApplyInput(new Vector(delta.X, 0), isPrecisionTouchpadScroll);
            if (verticalTarget is not null)
                _animators.GetValue(verticalTarget, static control => new(control))
                    .ApplyInput(new Vector(0, delta.Y), isPrecisionTouchpadScroll);
        }

        return true;
    }

    private void ApplyInput(Vector delta, bool isPrecisionTouchpadScroll)
    {
        if (isPrecisionTouchpadScroll)
        {
            // Windows already emits the touchpad's decelerating delta stream. Apply it directly:
            // synthesizing another fling here makes even a deliberate finger stop drift afterward.
            _velocity = default;
            _lastFrame = null;
            Vector step = delta * SmoothScrollPhysics.PrecisionTouchpadDistance;
            if (_target is DataGrid)
            {
                // DataGrid.UpdateScroll assigns (rather than adds to) its pending layout scroll.
                // Multiple touchpad packets before one layout otherwise overwrite each other.
                // Preserve every fractional delta and submit one combined step per frame.
                if (_pendingPrecisionStep.X * step.X < 0)
                    _pendingPrecisionStep = new Vector(0, _pendingPrecisionStep.Y);
                if (_pendingPrecisionStep.Y * step.Y < 0)
                    _pendingPrecisionStep = new Vector(_pendingPrecisionStep.X, 0);
                _pendingPrecisionStep += step;
                RequestFrame();
            }
            else
                ApplyPrecisionInput(step);
            return;
        }

        ResetOverpan();
        AddImpulse(delta);
    }

    private void ApplyPrecisionInput(Vector step)
    {
        ApplyPrecisionAxis(step.X, horizontal: true);
        ApplyPrecisionAxis(step.Y, horizontal: false);

        if (_overpan != default)
        {
            _lastPrecisionInputTimestamp = Stopwatch.GetTimestamp();
            _overpanVelocity = default;
            UpdateOverpanTransform();
            RequestFrame();
        }
        else
        {
            ResetOverpan();
        }
    }

    private void ApplyPrecisionAxis(double step, bool horizontal)
    {
        if (step == 0) return;

        double displacement = horizontal ? _overpan.X : _overpan.Y;
        if (displacement != 0)
        {
            if (Math.Sign(displacement) == Math.Sign(step))
            {
                SetOverpanAxis(AddResistedOverpan(displacement, step), horizontal);
                return;
            }

            // Pull the exposed empty region back one-to-one with the fingers. Only the portion
            // beyond the resting point is allowed to resume ordinary scrolling.
            double restored = displacement + step;
            if (restored != 0 && Math.Sign(restored) == Math.Sign(displacement))
            {
                SetOverpanAxis(restored, horizontal);
                return;
            }

            SetOverpanAxis(0, horizontal);
            step = restored;
            if (step == 0) return;
        }

        if (!ScrollByAxis(step, horizontal) && IsAtBoundary(step, horizontal))
            SetOverpanAxis(AddResistedOverpan(0, step), horizontal);
    }

    private bool IsAtBoundary(double step, bool horizontal)
    {
        if (_target is ScrollViewer viewer)
        {
            double maximum = horizontal ? viewer.Extent.Width - viewer.Viewport.Width
                : viewer.Extent.Height - viewer.Viewport.Height;
            double offset = horizontal ? viewer.Offset.X : viewer.Offset.Y;
            return maximum > 0 && (step > 0 ? offset <= 0 : offset >= maximum);
        }

        string name = horizontal ? "PART_HorizontalScrollbar" : "PART_VerticalScrollbar";
        foreach (Visual descendant in _target.GetVisualDescendants())
        {
            if (descendant is ScrollBar bar && bar.Name == name)
                return bar.Maximum > bar.Minimum &&
                       (step > 0 ? bar.Value <= bar.Minimum : bar.Value >= bar.Maximum);
        }
        return false;
    }

    internal static bool IsPrecisionTouchpadScroll(TopLevel topLevel, Vector delta)
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

    private static bool ApplyPrecisionInputAtBoundary(Visual source, Vector delta)
    {
        bool foundBoundary = false;
        if (delta.X != 0 && FindBoundaryScrollHost(source, horizontal: true) is { } horizontalHost)
        {
            _animators.GetValue(horizontalHost, static control => new(control))
                .ApplyInput(new Vector(delta.X, 0), isPrecisionTouchpadScroll: true);
            foundBoundary = true;
        }

        if (delta.Y != 0 && FindBoundaryScrollHost(source, horizontal: false) is { } verticalHost)
        {
            _animators.GetValue(verticalHost, static control => new(control))
                .ApplyInput(new Vector(0, delta.Y), isPrecisionTouchpadScroll: true);
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

        if (_pendingPrecisionStep != default)
        {
            Vector pendingStep = _pendingPrecisionStep;
            _pendingPrecisionStep = default;
            ApplyPrecisionInput(pendingStep);
            return;
        }

        if (_overpan != default)
        {
            double quietTime = Stopwatch.GetElapsedTime(_lastPrecisionInputTimestamp).TotalSeconds;
            if (quietTime < OverpanReleaseDelay)
            {
                RequestFrame();
                return;
            }

            double springDt = _lastFrame is { } springLast
                ? (now - springLast).TotalSeconds
                : 1.0 / 60.0;
            _lastFrame = now;
            if (springDt <= 0) springDt = 1.0 / 60.0;
            springDt = Math.Min(springDt, MaximumFrameTime);

            (double x, double velocityX) = StepSpring(_overpan.X, _overpanVelocity.X, springDt);
            (double y, double velocityY) = StepSpring(_overpan.Y, _overpanVelocity.Y, springDt);
            _overpan = new Vector(x, y);
            _overpanVelocity = new Vector(velocityX, velocityY);
            UpdateOverpanTransform();

            double distanceSquared = _overpan.X * _overpan.X + _overpan.Y * _overpan.Y;
            double velocitySquared = _overpanVelocity.X * _overpanVelocity.X +
                                     _overpanVelocity.Y * _overpanVelocity.Y;
            bool isAtRest = distanceSquared < OverpanStopDistance * OverpanStopDistance &&
                            velocitySquared < OverpanStopVelocity * OverpanStopVelocity;
            if (isAtRest)
            {
                ResetOverpan();
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

    private bool ScrollByAxis(double step, bool horizontal)
    {
        if (_target is DataGrid grid)
            return UpdateDataGridScroll(grid,
                horizontal ? new Vector(step, 0) : new Vector(0, step));

        var viewer = (ScrollViewer)_target;
        Vector oldOffset = viewer.Offset;
        double maximum = horizontal
            ? Math.Max(0, viewer.Extent.Width - viewer.Viewport.Width)
            : Math.Max(0, viewer.Extent.Height - viewer.Viewport.Height);
        double oldAxis = horizontal ? oldOffset.X : oldOffset.Y;
        double newAxis = Math.Clamp(oldAxis - step, 0, maximum);
        if (newAxis == oldAxis) return false;

        viewer.Offset = horizontal
            ? new Vector(newAxis, oldOffset.Y)
            : new Vector(oldOffset.X, newAxis);
        return true;
    }

    private void SetOverpanAxis(double value, bool horizontal)
    {
        value = Math.Clamp(value, -MaximumOverpan, MaximumOverpan);
        _overpan = horizontal
            ? new Vector(value, _overpan.Y)
            : new Vector(_overpan.X, value);
    }

    private static double AddResistedOverpan(double displacement, double input)
    {
        double remaining = Math.Max(0, MaximumOverpan - Math.Abs(displacement));
        if (remaining == 0 || input == 0) return displacement;

        // Consume an exponentially smaller fraction of the remaining travel. This approaches
        // the limit asymptotically, so the user feels increasing resistance rather than a clamp.
        double added = remaining *
                       (1.0 - Math.Exp(-Math.Abs(input) * OverpanResistance / MaximumOverpan));
        return Math.CopySign(Math.Abs(displacement) + added, input);
    }

    private void UpdateOverpanTransform()
    {
        if (_overpanVisual is null && !TryAttachOverpanTransform()) return;
        if (_overpanUsesRows && _overpanVisual is not null)
            AttachDataGridRows(_overpanVisual);
        _overpanTranslation!.X = _overpan.X;
        _overpanTranslation.Y = _overpan.Y;
    }

    private bool TryAttachOverpanTransform()
    {
        Visual? visual = null;
        if (_target is DataGrid)
        {
            // Keep the viewport stationary: translating the presenter would also move its clip
            // and let rows paint over the column headers.
            foreach (Visual descendant in _target.GetVisualDescendants())
            {
                if (descendant is Control { Name: "PART_RowsPresenter" })
                {
                    visual = descendant;
                    break;
                }
            }
        }

        ScrollContentPresenter? presenter = null;
        double largestOverflow = double.NegativeInfinity;

        if (visual is null)
        {
            foreach (Visual descendant in _target.GetVisualDescendants())
            {
                if (descendant is not ScrollContentPresenter candidate || candidate.Child is not Visual)
                    continue;

                double overflow = Math.Max(0, candidate.Extent.Width - candidate.Viewport.Width) +
                                  Math.Max(0, candidate.Extent.Height - candidate.Viewport.Height);
                if (overflow <= largestOverflow) continue;
                presenter = candidate;
                largestOverflow = overflow;
            }

            visual = presenter?.Child as Visual;
        }

        if (visual is null) return false;

        _overpanVisual = visual;
        _originalOverpanTransform = visual.RenderTransform;
        _overpanTranslation = new TranslateTransform();
        if (_target is DataGrid)
        {
            _overpanUsesRows = true;
            _originalRowsClipToBounds = visual.ClipToBounds;
            _overpanRowsClipCaptured = true;
            visual.ClipToBounds = true;
            AttachDataGridRows(visual);
            return true;
        }
        if (_originalOverpanTransform is null)
        {
            _appliedOverpanTransform = _overpanTranslation;
        }
        else
        {
            var group = new TransformGroup();
            // TransformGroup stores mutable Transforms. Preserve an immutable transform by
            // snapshotting its matrix for the short-lived overpan, then restore the original.
            group.Children.Add(_originalOverpanTransform is Transform originalTransform
                ? originalTransform
                : new MatrixTransform(_originalOverpanTransform.Value));
            group.Children.Add(_overpanTranslation);
            _appliedOverpanTransform = group;
        }

        visual.RenderTransform = _appliedOverpanTransform;
        return true;
    }

    private void AttachDataGridRows(Visual rowsPresenter)
    {
        foreach (Visual row in rowsPresenter.GetVisualChildren())
        {
            bool alreadyAttached = false;
            foreach (var existing in _overpanRows)
            {
                if (!ReferenceEquals(existing.Visual, row)) continue;
                alreadyAttached = true;
                break;
            }
            if (alreadyAttached) continue;

            ITransform? original = row.RenderTransform;
            ITransform applied = _overpanTranslation!;
            if (original is not null)
            {
                var transforms = new TransformGroup();
                transforms.Children.Add(original as Transform ?? new MatrixTransform(original.Value));
                transforms.Children.Add(_overpanTranslation!);
                applied = transforms;
            }

            _overpanRows.Add((row, original, applied));
            row.RenderTransform = applied;
        }
    }

    private static (double Position, double Velocity) StepSpring(
        double position,
        double velocity,
        double elapsedSeconds)
    {
        double acceleration = -OverpanSpringStrength * position - OverpanSpringDamping * velocity;
        velocity += acceleration * elapsedSeconds;
        position += velocity * elapsedSeconds;
        // A released edge must settle at zero, never cross it and expose the opposite edge.
        if (position * (position - velocity * elapsedSeconds) <= 0)
            return (0, 0);
        return (position, velocity);
    }

    private void ResetOverpan()
    {
        _lastPrecisionInputTimestamp = 0;
        _overpan = default;
        _overpanVelocity = default;

        foreach (var row in _overpanRows)
            if (ReferenceEquals(row.Visual.RenderTransform, row.Applied))
                row.Visual.RenderTransform = row.Original;
        if (_overpanRowsClipCaptured && _overpanVisual is not null)
            _overpanVisual.ClipToBounds = _originalRowsClipToBounds;
        _overpanRows.Clear();

        if (!_overpanUsesRows && _overpanVisual is not null &&
            ReferenceEquals(_overpanVisual.RenderTransform, _appliedOverpanTransform))
            _overpanVisual.RenderTransform = _originalOverpanTransform;

        _overpanVisual = null;
        _originalOverpanTransform = null;
        _appliedOverpanTransform = null;
        _overpanTranslation = null;
        _overpanRowsClipCaptured = false;
        _overpanUsesRows = false;
    }

    private void Stop()
    {
        _velocity = default;
        _pendingPrecisionStep = default;
        _lastFrame = null;
        ResetOverpan();
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "UpdateScroll")]
    private static extern bool UpdateDataGridScroll(DataGrid grid, Vector offset);

    private sealed class PrecisionInputState
    {
        internal long LastPrecisionTimestamp;
    }
}
