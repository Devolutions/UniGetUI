using System;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using UniGetUI.Avalonia.Infrastructure;

namespace UniGetUI.Avalonia.Views.Controls;

/// <summary>
/// Eases DataGrid wheel scrolling to a stop (WinUI-like) instead of jumping the whole delta at once.
/// The grid has no public scroll offset, so we drive its internal UpdateScroll via reflection; if that
/// ever breaks we never attach and the stock instant behavior stands.
/// </summary>
public sealed class DataGridWheelAnimator
{
    private static readonly MethodInfo? UpdateScroll =
        typeof(DataGrid).GetMethod("UpdateScroll", BindingFlags.Instance | BindingFlags.NonPublic);

    private const double WheelStep = 50.0;      // matches the DataGrid's own per-notch pixel amount
    private const double EasePerFrame = 0.4;    // fraction of the remaining distance consumed each frame; higher = snappier

    private readonly DataGrid _grid;
    private readonly DispatcherTimer _timer;
    private readonly object?[] _args = new object?[1];
    private double _pending;

    private DataGridWheelAnimator(DataGrid grid)
    {
        _grid = grid;
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(1000.0 / 90), DispatcherPriority.Render, OnTick);
        grid.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
    }

    public static void Attach(DataGrid grid)
    {
        if (UpdateScroll is not null) _ = new DataGridWheelAnimator(grid);
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        // Fall back to the native instant scroll for horizontal/shift, or when reduced motion is on.
        if (e.Delta.Y == 0 || e.KeyModifiers == KeyModifiers.Shift || MotionPreference.ReducedMotion) return;

        _pending += e.Delta.Y * WheelStep;
        e.Handled = true;
        if (!_timer.IsEnabled) _timer.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        double step = _pending * EasePerFrame;
        if (Math.Abs(step) < 2.0) step = _pending;

        _args[0] = new Vector(0, step);
        bool scrolled = UpdateScroll!.Invoke(_grid, _args) is true;
        _pending -= step;

        if (!scrolled || _pending == 0)
        {
            _pending = 0;
            _timer.Stop();
        }
    }
}
