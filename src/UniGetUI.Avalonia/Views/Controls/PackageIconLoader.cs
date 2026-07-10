using System;
using Avalonia;
using Avalonia.Controls;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.Avalonia.Views.Controls;

// Calls PackageWrapper.EnsureIconLoaded when the icon element attaches or is rebound, so only
// realized (visible) rows in the virtualized list load their icons.
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
        // A recycled container also fires this while detached; only load when realized.
        if (control.IsLoaded) TryLoad(control);
    }

    private static void TryLoad(Control control)
    {
        if (control.DataContext is PackageWrapper wrapper) wrapper.EnsureIconLoaded();
    }
}
