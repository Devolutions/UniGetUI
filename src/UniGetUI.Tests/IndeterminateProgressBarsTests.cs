using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
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
    public Task OnlyBarsWithoutTheCompositorClassFadeBetweenModes() => _session.Dispatch(() =>
    {
        PrepareStyles();
        var plain = Bar();
        var handedOff = Bar();
        handedOff.Classes.Add(IndeterminateProgressBars.CompositorClass);
        var window = Show(new StackPanel { Spacing = 10, Children = { plain, handedOff } });

        Assert.NotEmpty(Root(plain, "DeterminateRoot").Transitions!);
        Assert.NotEmpty(Root(plain, "IndeterminateRoot").Transitions!);
        Assert.Null(Root(handedOff, "DeterminateRoot").Transitions);
        Assert.Null(Root(handedOff, "IndeterminateRoot").Transitions);

        handedOff.IsIndeterminate = false;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(1, Root(handedOff, "DeterminateRoot").Opacity);
        Assert.Equal(0, Root(handedOff, "IndeterminateRoot").Opacity);
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

    [Fact]
    public void RoundedShapeMatchesUniformAndOneSidedCorners()
    {
        Assert.Equal(new RoundedShape(0, 0, 100, 4, 2), RoundedShape.For(100, 4, new CornerRadius(2)));
        Assert.Equal(new RoundedShape(0, -2.5, 300, 5, 2.5), RoundedShape.For(300, 2.5, new CornerRadius(0, 0, 6, 6)));
        Assert.Equal(new RoundedShape(0, 0, 300, 13, 3), RoundedShape.For(300, 10, new CornerRadius(3, 3, 0, 0)));
        Assert.Equal(new RoundedShape(-5, 0, 305, 10, 5), RoundedShape.For(300, 10, new CornerRadius(0, 6, 6, 0)));
        Assert.Equal(new RoundedShape(0, 0, 304, 10, 4), RoundedShape.For(300, 10, new CornerRadius(4, 0, 0, 4)));
        Assert.Null(RoundedShape.For(300, 10, new CornerRadius(1, 2, 3, 4)));
        Assert.False(RoundedShape.Supports(new CornerRadius(1, 2, 3, 4)));
    }

    [Fact]
    public void RoundedShapeScalesOversizedCornersLikeSkia()
    {
        Assert.Equal(new CornerRadius(2), RoundedShape.Fit(100, 4, new CornerRadius(10)));
        Assert.Equal(new CornerRadius(0, 0, 2.5, 2.5), RoundedShape.Fit(300, 2.5, new CornerRadius(0, 0, 6, 6)));
        Assert.Equal(new CornerRadius(1), RoundedShape.Fit(100, 4, new CornerRadius(1)));
    }

    [Fact]
    public Task SnapshotKeepsPerCornerRounding() => _session.Dispatch(() =>
    {
        PrepareStyles();
        var bar = Bar();
        bar.Height = 2.5;
        bar.MinHeight = 0;
        bar.CornerRadius = new CornerRadius(0, 0, 6, 6);
        var window = Show(new Panel { Children = { bar } });

        var snapshot = IndeterminateBarSnapshot.Capture(bar, window);

        Assert.NotNull(snapshot);
        double height = snapshot.Track.Height;
        Assert.Equal(Math.Min(6, height), snapshot.TrackShape.Radius, 3);
        Assert.Equal(-snapshot.TrackShape.Radius, snapshot.TrackShape.OffsetY, 3);
        Assert.Equal(height + snapshot.TrackShape.Radius, snapshot.TrackShape.Height, 3);
        Assert.Equal(-snapshot.FirstShape.Radius, snapshot.FirstShape.OffsetY, 3);
        Assert.True(snapshot.FirstShape.Radius > 0);
        window.Close();
    }, CancellationToken.None);

    [Theory]
    [InlineData(0, false)]
    [InlineData(5, true)]
    public Task OnlyContentDrawnAboveABarCoversIt(int coverZIndex, bool covered) => _session.Dispatch(() =>
    {
        PrepareStyles();
        var bar = Bar();
        bar.Background = null;
        var below = new Border
        {
            Width = 300,
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.White,
            ZIndex = coverZIndex,
        };
        var window = Show(new Panel { Children = { below, bar } });

        Assert.Equal(covered, IndeterminateBarSnapshot.Capture(bar, window) is null);
        Assert.Equal(covered, IndeterminateBarSnapshot.IsDrawnAbove(below, bar));
        window.Close();
    }, CancellationToken.None);

    [Theory]
    [InlineData(40, 20, 255, "Partial")]
    [InlineData(0, 300, 255, "Full")]
    [InlineData(0, 300, 128, "Partial")]
    [InlineData(0, 300, 0, "None")]
    public Task CoverageSeesEveryOverlapNotJustSamplePoints(double left, double width, byte alpha, string expected) => _session.Dispatch(() =>
    {
        PrepareStyles();
        var bar = Bar();
        var overlay = new Border
        {
            Margin = new Thickness(left, 0, 0, 0),
            Width = width,
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255)),
        };
        var window = Show(new Panel { Children = { bar, overlay } });

        var snapshot = IndeterminateBarSnapshot.Capture(bar, window, out BarCoverage coverage);

        Assert.Equal(Enum.Parse<BarCoverage>(expected), coverage);
        Assert.Equal(coverage == BarCoverage.None, snapshot is not null);
        window.Close();
    }, CancellationToken.None);

    [Theory]
    [InlineData(0, "Partial")]
    [InlineData(200, "None")]
    public Task CoverageOnlyCountsWhatAClippingParentLetsThrough(double childLeft, string expected) => _session.Dispatch(() =>
    {
        PrepareStyles();
        var bar = Bar();
        var clipping = new Border
        {
            Width = 120,
            Height = 40,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new Border
            {
                Margin = new Thickness(childLeft, 0, 0, 0),
                Width = 300 - childLeft,
                Height = 40,
                HorizontalAlignment = HorizontalAlignment.Left,
                Background = Brushes.White,
            },
        };
        var window = Show(new Panel { Children = { bar, clipping } });

        IndeterminateBarSnapshot.Capture(bar, window, out BarCoverage coverage);

        Assert.Equal(Enum.Parse<BarCoverage>(expected), coverage);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task CopiedIndicatorAnimationsMatchFluent() => _session.Dispatch(() =>
    {
        PrepareStyles();
        var fluent = new FluentTheme();
        Assert.True(fluent.TryGetResource(typeof(ProgressBar), null, out object? resource));
        var original = Describe(Assert.IsType<ControlTheme>(resource).Children);

        var common = Application.Current!.Styles.OfType<StyleInclude>()
            .Single(s => s.Source?.OriginalString.EndsWith("Styles.Common.axaml") == true);
        var copied = Describe(((Styles)common.Loaded).OfType<IStyle>());

        Assert.Equal(4, original.Count);
        Assert.Equal(original, copied);
    }, CancellationToken.None);

    private static SortedDictionary<string, string> Describe(IEnumerable<IStyle> styles)
    {
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (IStyle style in styles)
        {
            if (style is Style { Selector: { } selector } withSelector
                && selector.ToString() is var text
                && text.Contains("indeterminate", StringComparison.Ordinal)
                && System.Text.RegularExpressions.Regex.Match(text, @":(horizontal|vertical)") is { Success: true } orientation
                && System.Text.RegularExpressions.Regex.Match(text, @"#(IndeterminateProgressBarIndicator2?)\b") is { Success: true } part)
            {
                foreach (Animation animation in withSelector.Animations.OfType<Animation>().Where(a => a.IterationCount == IterationCount.Infinite))
                {
                    var frames = animation.Children.Select(frame =>
                        $"{frame.KeyTime}|{frame.KeySpline?.ControlPointX1},{frame.KeySpline?.ControlPointY1},{frame.KeySpline?.ControlPointX2},{frame.KeySpline?.ControlPointY2}|"
                        + string.Join(";", frame.Setters.OfType<Setter>().Select(s => $"{s.Property?.Name}={BoundProperty(s.Value)}")));
                    found[$"{orientation.Groups[1].Value}/{part.Groups[1].Value}"] =
                        $"{animation.Duration}|{animation.IterationCount}|{string.Join(" ", frames)}";
                }
            }

            foreach (var entry in Describe(style.Children))
                found[entry.Key] = entry.Value;
        }

        return found;
    }

    private static string BoundProperty(object? value)
    {
        string text = value switch
        {
            global::Avalonia.Data.CompiledBinding compiled => compiled.Path?.ToString() ?? "",
            global::Avalonia.Data.ReflectionBinding reflection => reflection.Path ?? "",
            _ => value?.ToString() ?? "",
        };
        int dot = text.LastIndexOf('.');
        return dot >= 0 ? text[(dot + 1)..] : text;
    }

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

    private static Panel Root(ProgressBar bar, string name)
        => bar.GetVisualDescendants().OfType<Panel>().Single(p => p.Name == name);

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
