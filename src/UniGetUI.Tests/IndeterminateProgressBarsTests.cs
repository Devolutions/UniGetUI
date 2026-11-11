using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UniGetUI.Avalonia.Infrastructure;

namespace UniGetUI.Tests;

public sealed class IndeterminateProgressBarsTests
{
    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(IndeterminateProgressBarsTests).Assembly);

    [Fact]
    public Task FluentThemeKeepsNoInfiniteProgressBarAnimation() => _session.Dispatch(() =>
    {
        PrepareStyles();

        Assert.True(Application.Current!.TryGetResource(typeof(ProgressBar), null, out object? resource));
        var theme = Assert.IsType<ControlTheme>(resource);
        Assert.Empty(InfiniteAnimations(theme.Children));
        Assert.Equal(0, IndeterminateProgressBars.RemoveThemeAnimations(Application.Current!));
    }, CancellationToken.None);

    [Fact]
    public Task OnlyBarsWithoutTheCompositorClassAnimateInAvalonia() => _session.Dispatch(() =>
    {
        PrepareStyles();
        var animated = Bar();
        var handedOff = Bar();
        handedOff.Classes.Add(IndeterminateProgressBars.CompositorClass);
        var window = Show(new StackPanel { Spacing = 10, Children = { animated, handedOff } });

        AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
        Dispatcher.UIThread.RunJobs();

        Assert.NotEqual(0, IndicatorOffset(animated));
        Assert.Equal(0, IndicatorOffset(handedOff));
        Assert.Equal(1, Indicator(animated).Opacity);
        Assert.Equal(0, Indicator(handedOff).Opacity);

        handedOff.Classes.Remove(IndeterminateProgressBars.CompositorClass);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(3);
        Dispatcher.UIThread.RunJobs();

        Assert.NotEqual(0, IndicatorOffset(handedOff));
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task SnapshotDescribesTheBarWhereItIsDrawn() => _session.Dispatch(() =>
    {
        PrepareStyles();
        var bar = Bar();
        bar.Margin = new Thickness(30, 20, 0, 0);
        bar.Foreground = new SolidColorBrush(Color.FromRgb(10, 20, 30));
        var window = Show(new Panel { Children = { bar } });

        var snapshot = IndeterminateBarSnapshot.Capture(bar, window);

        Assert.NotNull(snapshot);
        Assert.Equal(new Rect(30, 20, 300, 4), snapshot.Track);
        Assert.Equal(snapshot.Track, snapshot.Clip);
        Assert.Equal(Color.FromRgb(10, 20, 30), snapshot.Color);
        Assert.Equal(bar.TemplateSettings.ContainerWidth, snapshot.First.Width, 3);
        Assert.Equal(bar.TemplateSettings.Container2Width, snapshot.Second.Width, 3);
        Assert.Equal(bar.TemplateSettings.ContainerAnimationStartPosition, snapshot.First.Start, 3);
        Assert.Equal(bar.TemplateSettings.ContainerAnimationEndPosition, snapshot.First.End, 3);
        Assert.Equal(bar.TemplateSettings.Container2AnimationStartPosition, snapshot.Second.Start, 3);
        Assert.Equal(bar.TemplateSettings.Container2AnimationEndPosition, snapshot.Second.End, 3);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task SnapshotIsClippedByItsAncestors() => _session.Dispatch(() =>
    {
        PrepareStyles();
        var bar = Bar();
        var window = Show(new Border
        {
            Width = 120,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new Panel { Width = 300, Children = { bar } },
        });

        var snapshot = IndeterminateBarSnapshot.Capture(bar, window);

        Assert.NotNull(snapshot);
        Assert.Equal(300, snapshot.Track.Width, 3);
        Assert.Equal(120, snapshot.Clip.Width, 3);
        window.Close();
    }, CancellationToken.None);

    [Theory]
    [InlineData("opaque", true)]
    [InlineData("transparent", false)]
    [InlineData("invisible", false)]
    [InlineData("none", false)]
    public Task ABarIsHiddenOnlyWhenSomethingVisibleIsDrawnOverIt(string overlay, bool covered) => _session.Dispatch(() =>
    {
        PrepareStyles();
        var bar = Bar();
        var panel = new Panel { Children = { bar } };
        if (overlay != "none")
        {
            panel.Children.Add(new Border
            {
                Width = 300,
                Height = 40,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Background = overlay == "transparent" ? Brushes.Transparent : Brushes.White,
                Opacity = overlay == "invisible" ? 0 : 1,
            });
        }

        var window = Show(panel);

        Assert.Equal(covered, IndeterminateBarSnapshot.Capture(bar, window) is null);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task NoSnapshotForDeterminateOrHiddenBars() => _session.Dispatch(() =>
    {
        PrepareStyles();
        var determinate = Bar();
        determinate.IsIndeterminate = false;
        var hidden = Bar();
        var window = Show(new StackPanel
        {
            Children = { determinate, new Border { IsVisible = false, Child = hidden } },
        });

        Assert.Null(IndeterminateBarSnapshot.Capture(determinate, window));
        Assert.Null(IndeterminateBarSnapshot.Capture(hidden, window));
        window.Close();
    }, CancellationToken.None);

    private static void PrepareStyles()
    {
        Application app = Application.Current!;
        IndeterminateProgressBars.RemoveThemeAnimations(app);
        if (!app.Styles.OfType<StyleInclude>().Any(s => s.Source?.OriginalString.EndsWith("Styles.Common.axaml") == true))
        {
            app.Styles.Add(new StyleInclude(new Uri("avares://UniGetUI/"))
            {
                Source = new Uri("avares://UniGetUI/Assets/Styles/Styles.Common.axaml"),
            });
        }
    }

    private static ProgressBar Bar() => new()
    {
        IsIndeterminate = true,
        Width = 300,
        Height = 4,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
    };

    private static Window Show(Control content)
    {
        var window = new Window { Width = 500, Height = 200, Content = content };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static Border Indicator(ProgressBar bar)
        => bar.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "IndeterminateProgressBarIndicator");

    private static double IndicatorOffset(ProgressBar bar)
        => Indicator(bar).RenderTransform?.Value.M31 ?? 0;

    private static List<Animation> InfiniteAnimations(IEnumerable<IStyle> styles)
    {
        var found = new List<Animation>();
        foreach (IStyle style in styles)
        {
            if (style is StyleBase styleBase)
            {
                found.AddRange(styleBase.Animations.OfType<Animation>()
                    .Where(a => a.IterationCount == IterationCount.Infinite));
            }

            found.AddRange(InfiniteAnimations(style.Children));
        }

        return found;
    }
}
