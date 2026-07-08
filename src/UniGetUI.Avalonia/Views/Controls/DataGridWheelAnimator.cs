using System;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

    private const double WheelStep = 70.0;   // pixels travelled per notch; larger = faster scroll, more to glide over
    private const double Tau = 0.12;         // ease time constant in seconds; larger = longer, more visible glide

    private readonly DataGrid _grid;
    private readonly object?[] _args = new object?[1];
    private double _pending;
    private TimeSpan? _lastFrame;
    private bool _frameRequested;

    private DataGridWheelAnimator(DataGrid grid)
    {
        _grid = grid;
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

        if (_pending == 0) _lastFrame = null;   // fresh gesture: don't carry a stale timestamp
        _pending += e.Delta.Y * WheelStep;
        e.Handled = true;
        RequestFrame();
    }

    private void RequestFrame()
    {
        if (_frameRequested) return;
        if (TopLevel.GetTopLevel(_grid) is not { } top) { _pending = 0; return; }
        _frameRequested = true;
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan now)
    {
        _frameRequested = false;
        if (_pending == 0) return;

        double dt = _lastFrame is { } last ? (now - last).TotalSeconds : 1.0 / 60.0;
        _lastFrame = now;
        if (dt <= 0) dt = 1.0 / 60.0;
        if (dt > 0.1) dt = 0.1;   // clamp after a stall so the glide doesn't lurch

        double remaining = _pending * Math.Exp(-dt / Tau);
        double step = Math.Abs(remaining) < 0.5 ? _pending : _pending - remaining;

        _args[0] = new Vector(0, step);
        bool scrolled = UpdateScroll!.Invoke(_grid, _args) is true;
        _pending -= step;

        if (!scrolled) { _pending = 0; return; }
        if (_pending != 0) RequestFrame();
    }
}
