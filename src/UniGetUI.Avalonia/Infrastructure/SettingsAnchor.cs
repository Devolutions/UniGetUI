using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// Scrolls a named control inside a settings sub-page into view and briefly highlights it,
/// so a search result lands the user on the exact setting they typed.
/// </summary>
public static class SettingsAnchor
{
    public static void ScrollToAndHighlight(Control root, string anchorName)
    {
        // The page may still be sliding into the transitioning frame; wait until it's attached
        // and laid out so BringIntoView has real bounds to work with.
        if (root.IsLoaded)
        {
            Dispatcher.UIThread.Post(() => Run(root, anchorName), DispatcherPriority.Loaded);
            return;
        }

        void OnLoaded(object? sender, RoutedEventArgs e)
        {
            root.Loaded -= OnLoaded;
            Dispatcher.UIThread.Post(() => Run(root, anchorName), DispatcherPriority.Loaded);
        }

        root.Loaded += OnLoaded;
    }

    private static void Run(Control root, string anchorName)
    {
        var target = root.GetVisualDescendants()
                         .OfType<Control>()
                         .FirstOrDefault(c => c.Name == anchorName);
        if (target is null) return;

        // A SettingsCard's visible surface is an inner Border.settings-card inset 40px per side;
        // adorn that so the outline hugs the card rather than the full-width control wrapper.
        Control highlightTarget = target.GetVisualDescendants()
                                        .OfType<Border>()
                                        .FirstOrDefault(b => b.Classes.Contains("settings-card"))
                                  ?? target;

        highlightTarget.BringIntoView();
        Highlight(highlightTarget);
    }

    private static void Highlight(Control target)
    {
        var layer = AdornerLayer.GetAdornerLayer(target);
        if (layer is null) return;

        Color accent = target.TryFindResource("SystemAccentColor", target.ActualThemeVariant, out var res)
                       && res is Color c ? c : Colors.DodgerBlue;

        var adorner = new Border
        {
            BorderBrush = new SolidColorBrush(accent),
            BorderThickness = new Thickness(2),
            CornerRadius = target is Border b ? b.CornerRadius : new CornerRadius(8),
            IsHitTestVisible = false,
        };
        AdornerLayer.SetAdornedElement(adorner, target);
        layer.Children.Add(adorner);

        void Remove() { if (layer.Children.Contains(adorner)) layer.Children.Remove(adorner); }

        // A gentle pulse that holds, then fades out and removes itself.
        var fade = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(1800),
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0d),    Setters = { new Setter(Visual.OpacityProperty, 1d) } },
                new KeyFrame { Cue = new Cue(0.55d), Setters = { new Setter(Visual.OpacityProperty, 1d) } },
                new KeyFrame { Cue = new Cue(1d),    Setters = { new Setter(Visual.OpacityProperty, 0d) } },
            },
        };
        _ = fade.RunAsync(adorner).ContinueWith(
            _ => Dispatcher.UIThread.Post(Remove),
            TaskScheduler.Default);
    }
}
