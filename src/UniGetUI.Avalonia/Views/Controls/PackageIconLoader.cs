using System;
using Avalonia;
using Avalonia.Controls;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.Avalonia.Views.Controls;

/// <summary>
/// Attached behaviour that calls <see cref="PackageWrapper.EnsureIconLoaded"/> when the icon element
/// attaches to the visual tree or is rebound to a new wrapper. In the virtualized list only the
/// realized (visible) rows attach, so only those load their icons — instead of every result eagerly
/// loading one in the wrapper constructor (thousands for a broad search).
/// </summary>
public static class PackageIconLoader
{
    public static readonly AttachedProperty<bool> TrackProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Track", typeof(PackageIconLoader));

    public static void SetTrack(Control control, bool value) => control.SetValue(TrackProperty, value);
    public static bool GetTrack(Control control) => control.GetValue(TrackProperty);

    static PackageIconLoader()
    {
        TrackProperty.Changed.AddClassHandler<Control>((control, e) =>
        {
            if (e.GetNewValue<bool>())
            {
                control.AttachedToVisualTree += OnAttached;
                control.DataContextChanged += OnDataContextChanged;
                if (control.IsLoaded) TryLoad(control);
            }
            else
            {
                control.AttachedToVisualTree -= OnAttached;
                control.DataContextChanged -= OnDataContextChanged;
            }
        });
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
        => TryLoad((Control)sender!);

    private static void OnDataContextChanged(object? sender, EventArgs e)
    {
        var control = (Control)sender!;
        // Only when realized: a recycled container fires this while detached too.
        if (control.IsLoaded) TryLoad(control);
    }

    private static void TryLoad(Control control)
    {
        if (control.DataContext is PackageWrapper wrapper) wrapper.EnsureIconLoaded();
    }
}
