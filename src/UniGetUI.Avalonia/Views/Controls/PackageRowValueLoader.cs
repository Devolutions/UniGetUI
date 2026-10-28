using System;
using Avalonia;
using Avalonia.Controls;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.Avalonia.Views.Controls;

public static class PackageRowValueLoader
{
    public static readonly AttachedProperty<bool> TrackProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("Track", typeof(PackageRowValueLoader));

    public static void SetTrack(Control control, bool value) => control.SetValue(TrackProperty, value);
    public static bool GetTrack(Control control) => control.GetValue(TrackProperty);

    static PackageRowValueLoader()
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
        if (control.IsLoaded) TryLoad(control);
    }

    private static void TryLoad(Control control)
    {
        if (control.DataContext is not PackageWrapper wrapper) return;

        wrapper.EnsureInstallerHostLoaded();
        wrapper.EnsureDownloadSizeLoaded();
        wrapper.EnsureReleaseDateLoaded();
    }
}
