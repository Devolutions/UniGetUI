using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using UniGetUI.Avalonia.Infrastructure;

namespace UniGetUI.Tests;

public sealed class IndeterminateBarRegistryTests
{
    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(IndeterminateBarRegistryTests).Assembly);

    [Fact]
    public Task ASupportedBarIsHandedOffAndShownOnlyWhenItChanges() => _session.Dispatch(() =>
    {
        var (window, bar) = ShowBar();
        var surface = new FakeSurface();
        var registry = new IndeterminateBarRegistry(_ => surface);

        registry.Register(bar);
        registry.SyncAll();
        registry.SyncAll();

        Assert.True(HandedOff(bar));
        FakeLayer layer = Assert.Single(surface.Layers);
        Assert.Equal(1, layer.Shows);
        Assert.True(registry.IsPolling);

        Assert.Equal(300, layer.LastSnapshot!.Track.Width, 3);
        Assert.Equal(window.RenderScaling, layer.LastScale);

        bar.Width = 200;
        Settle();
        registry.SyncAll();
        Assert.Equal(2, layer.Shows);
        Assert.Equal(200, layer.LastSnapshot!.Track.Width, 3);

        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task AFinishedBarIsHiddenOnceAndStopsPolling() => _session.Dispatch(() =>
    {
        var (window, bar) = ShowBar();
        var surface = new FakeSurface();
        var registry = new IndeterminateBarRegistry(_ => surface);
        registry.Register(bar);
        registry.SyncAll();

        bar.IsIndeterminate = false;
        registry.SyncAll();
        registry.SyncAll();

        FakeLayer layer = Assert.Single(surface.Layers);
        Assert.Equal(1, layer.Hides);
        Assert.False(registry.IsPolling);
        window.Close();
    }, CancellationToken.None);

    public static TheoryData<string> UnsupportedBars => new() { "vertical", "gradient", "corners" };

    [Theory]
    [MemberData(nameof(UnsupportedBars))]
    public Task ABarTheCompositorCannotDrawKeepsTheAvaloniaAnimation(string kind) => _session.Dispatch(() =>
    {
        var (window, bar) = ShowBar(b =>
        {
            switch (kind)
            {
                case "vertical":
                    b.Orientation = Orientation.Vertical;
                    b.Width = 4;
                    b.Height = 100;
                    break;
                case "gradient":
                    b.Foreground = new LinearGradientBrush
                    {
                        GradientStops = { new GradientStop(Colors.Red, 0), new GradientStop(Colors.Blue, 1) },
                    };
                    break;
                case "corners":
                    b.CornerRadius = new CornerRadius(1, 2, 3, 4);
                    break;
            }
        });
        var surface = new FakeSurface();
        var registry = new IndeterminateBarRegistry(_ => surface);

        registry.Register(bar);
        registry.SyncAll();

        Assert.False(HandedOff(bar));
        Assert.Equal(0, Assert.Single(surface.Layers).Shows);
        Assert.False(registry.IsPolling);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task ABarThatBecomesUnsupportedIsHandedBack() => _session.Dispatch(() =>
    {
        var (window, bar) = ShowBar();
        var surface = new FakeSurface();
        var registry = new IndeterminateBarRegistry(_ => surface);
        registry.Register(bar);
        registry.SyncAll();
        Assert.True(HandedOff(bar));

        bar.CornerRadius = new CornerRadius(1, 2, 3, 4);
        registry.SyncAll();

        Assert.False(HandedOff(bar));
        Assert.Equal(1, Assert.Single(surface.Layers).Hides);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task AMinimizedWindowStopsPolling() => _session.Dispatch(() =>
    {
        var (window, bar) = ShowBar();
        var surface = new FakeSurface();
        var registry = new IndeterminateBarRegistry(_ => surface);
        registry.Register(bar);
        registry.SyncAll();
        Assert.True(registry.IsPolling);

        window.WindowState = WindowState.Minimized;
        registry.SyncAll();

        Assert.False(registry.IsPolling);
        Assert.Equal(1, Assert.Single(surface.Layers).Hides);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task ACoveredBarKeepsPollingSoItReappearsWhenUncovered() => _session.Dispatch(() =>
    {
        var bar = Bar();
        var cover = new Border
        {
            Width = 300,
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.White,
        };
        var window = Show(new Panel { Children = { bar, cover } });
        var surface = new FakeSurface();
        var registry = new IndeterminateBarRegistry(_ => surface);
        registry.Register(bar);
        registry.SyncAll();

        Assert.Equal(0, surface.Layers[0].Shows);
        Assert.True(registry.IsPolling);

        cover.Opacity = 0;
        registry.SyncAll();
        Assert.Equal(1, surface.Layers[0].Shows);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task APartlyCoveredBarIsHandedBackUntilUncovered() => _session.Dispatch(() =>
    {
        var bar = Bar();
        var cover = new Border
        {
            Margin = new Thickness(40, 0, 0, 0),
            Width = 20,
            Height = 40,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = Brushes.White,
        };
        var window = Show(new Panel { Children = { bar, cover } });
        var surface = new FakeSurface();
        var registry = new IndeterminateBarRegistry(_ => surface);
        registry.Register(bar);
        registry.SyncAll();

        Assert.False(HandedOff(bar));
        Assert.Equal(0, surface.Layers[0].Shows);
        Assert.True(registry.IsPolling);

        cover.IsVisible = false;
        registry.SyncAll();

        Assert.True(HandedOff(bar));
        Assert.Equal(1, surface.Layers[0].Shows);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task ABarUnderAClippedOpaqueCoverKeepsAnimatingWhereItShows() => _session.Dispatch(() =>
    {
        var bar = Bar();
        var window = Show(new Panel
        {
            Children =
            {
                bar,
                new Border
                {
                    Width = 120,
                    Height = 40,
                    ClipToBounds = true,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Child = new Border { Width = 300, Height = 40, HorizontalAlignment = HorizontalAlignment.Left, Background = Brushes.White },
                },
            },
        });
        var surface = new FakeSurface();
        var registry = new IndeterminateBarRegistry(_ => surface);
        registry.Register(bar);
        registry.SyncAll();

        Assert.False(HandedOff(bar));
        Assert.Equal(0, surface.Layers[0].Shows);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task AFullyCoveredBarStaysHandedOffButHidden() => _session.Dispatch(() =>
    {
        var bar = Bar();
        var window = Show(new Panel
        {
            Children =
            {
                bar,
                new Border
                {
                    Width = 300,
                    Height = 40,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Background = Brushes.White,
                },
            },
        });
        var surface = new FakeSurface();
        var registry = new IndeterminateBarRegistry(_ => surface);
        registry.Register(bar);
        registry.SyncAll();

        Assert.True(HandedOff(bar));
        Assert.Equal(0, surface.Layers[0].Shows);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task AnAncestorMovingWithoutLayoutResyncsAtOnce() => _session.Dispatch(() =>
    {
        var bar = Bar();
        var slide = new TranslateTransform();
        var page = new Border { RenderTransform = slide, Child = bar };
        var window = Show(new Panel { Children = { page } });
        var surface = new FakeSurface();
        var registry = new IndeterminateBarRegistry(_ => surface);
        registry.Register(bar);
        Dispatcher.UIThread.RunJobs();
        FakeLayer layer = surface.Layers[0];
        Assert.Equal(1, layer.Shows);
        Assert.Equal(0, layer.LastSnapshot!.Track.X, 3);

        slide.X = 50;
        registry.NotifyAppearanceChanged(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(2, layer.Shows);
        Assert.Equal(50, layer.LastSnapshot!.Track.X, 3);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task AnAncestorFadingInShowsTheBarWithoutWaitingForThePoll() => _session.Dispatch(() =>
    {
        var bar = Bar();
        var page = new Border { Opacity = 0, Child = bar };
        var unrelated = new Border();
        var window = Show(new Panel { Children = { page, unrelated } });
        var surface = new FakeSurface();
        var registry = new IndeterminateBarRegistry(_ => surface);
        registry.Register(bar);
        Dispatcher.UIThread.RunJobs();
        FakeLayer layer = surface.Layers[0];
        Assert.Equal(0, layer.Shows);

        page.Opacity = 0.5;
        unrelated.Opacity = 0.5;
        registry.NotifyAppearanceChanged(unrelated);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(0, layer.Shows);

        registry.NotifyAppearanceChanged(page);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, layer.Shows);
        Assert.Equal(0.5, layer.LastSnapshot!.Opacity, 3);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task AFailingLayerHandsEveryBarBackAndDisablesTheCompositor() => _session.Dispatch(() =>
    {
        var first = Bar();
        var second = Bar();
        var window = Show(new StackPanel { Spacing = 10, Children = { first, second } });
        var surface = new FakeSurface { ThrowOnShow = true };
        var registry = new IndeterminateBarRegistry(_ => surface);
        registry.Register(first);
        registry.Register(second);

        registry.SyncAll();

        Assert.True(registry.IsDisabled);
        Assert.False(HandedOff(first));
        Assert.False(HandedOff(second));
        Assert.All(surface.Layers, l => Assert.True(l.Disposed));
        Assert.True(surface.Disposed);
        Assert.False(registry.IsPolling);

        registry.Register(first);
        Assert.False(registry.IsRegistered(first));
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task AFailingSurfaceDisablesTheCompositorWithoutHandingOff() => _session.Dispatch(() =>
    {
        var (window, bar) = ShowBar();
        var registry = new IndeterminateBarRegistry(_ => throw new InvalidOperationException("no compositor"));

        registry.Register(bar);

        Assert.True(registry.IsDisabled);
        Assert.False(HandedOff(bar));
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task AWindowWithoutASurfaceLeavesItsBarsAlone() => _session.Dispatch(() =>
    {
        var (window, bar) = ShowBar();
        var registry = new IndeterminateBarRegistry(_ => null);

        registry.Register(bar);

        Assert.False(registry.IsDisabled);
        Assert.False(registry.IsRegistered(bar));
        Assert.False(HandedOff(bar));
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task UnregisteringReleasesTheBar() => _session.Dispatch(() =>
    {
        var (window, bar) = ShowBar();
        var surface = new FakeSurface();
        var registry = new IndeterminateBarRegistry(_ => surface);
        registry.Register(bar);
        registry.SyncAll();

        registry.Unregister(bar);

        Assert.False(HandedOff(bar));
        Assert.True(surface.Layers[0].Disposed);
        Assert.False(registry.IsPolling);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task ClosingTheWindowReleasesItsBarsAndSurface() => _session.Dispatch(() =>
    {
        var (window, bar) = ShowBar();
        var surface = new FakeSurface();
        var registry = new IndeterminateBarRegistry(_ => surface);
        registry.Register(bar);
        registry.SyncAll();

        window.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.False(registry.IsRegistered(bar));
        Assert.True(surface.Layers[0].Disposed);
        Assert.True(surface.Disposed);
        Assert.False(registry.IsPolling);
    }, CancellationToken.None);

    private static bool HandedOff(ProgressBar bar) => bar.Classes.Contains(IndeterminateProgressBars.CompositorClass);

    private static (Window Window, ProgressBar Bar) ShowBar(Action<ProgressBar>? configure = null)
    {
        var bar = Bar();
        configure?.Invoke(bar);
        return (Show(new Panel { Children = { bar } }), bar);
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
        PrepareStyles();
        var window = new Window { Width = 500, Height = 200, Content = content };
        window.Show();
        Settle();
        return window;
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
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

    private sealed class FakeSurface : IIndeterminateBarSurface
    {
        public List<FakeLayer> Layers { get; } = [];

        public bool ThrowOnShow { get; init; }

        public bool Disposed { get; private set; }

        public IIndeterminateBarLayer CreateLayer()
        {
            var layer = new FakeLayer(ThrowOnShow);
            Layers.Add(layer);
            return layer;
        }

        public void Dispose() => Disposed = true;
    }

    private sealed class FakeLayer(bool throwOnShow) : IIndeterminateBarLayer
    {
        public int Shows { get; private set; }

        public int Hides { get; private set; }

        public bool Disposed { get; private set; }

        public IndeterminateBarSnapshot? LastSnapshot { get; private set; }

        public double LastScale { get; private set; }

        public void Show(IndeterminateBarSnapshot snapshot, double scale)
        {
            if (throwOnShow)
                throw new System.Runtime.InteropServices.COMException("compositor gone");
            Shows++;
            LastSnapshot = snapshot;
            LastScale = scale;
        }

        public void Hide() => Hides++;

        public void Dispose() => Disposed = true;
    }
}
