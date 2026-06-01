using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Media;
using Avalonia.Styling;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// WinUI-style page entrance: the incoming page fades in while sliding up a few pixels
/// (mimics the Frame NavigationThemeTransition); the outgoing page fades out.
/// </summary>
public sealed class EntrancePageTransition : IPageTransition
{
    public TimeSpan Duration { get; set; } = TimeSpan.FromMilliseconds(220);

    /// <summary>How far (px) the incoming page slides up as it fades in.</summary>
    public double VerticalOffset { get; set; } = 28;

    public async Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
    {
        // Drop the outgoing page immediately so only the incoming page animates in.
        from?.Opacity = 0;

        if (to is null)
            return;

        var enter = new Animation
        {
            Duration = Duration,
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 0d),
                        new Setter(TranslateTransform.YProperty, VerticalOffset),
                    },
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters =
                    {
                        new Setter(Visual.OpacityProperty, 1d),
                        new Setter(TranslateTransform.YProperty, 0d),
                    },
                },
            },
        };

        try
        {
            await enter.RunAsync(to, cancellationToken);
        }
        finally
        {
            // Restore even if cancelled, so the presenter is never reused while stranded invisible.
            from?.Opacity = 1;
        }
    }
}
