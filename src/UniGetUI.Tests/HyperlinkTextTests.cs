using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using UniGetUI.Avalonia.Views.Controls;

namespace UniGetUI.Tests;

public sealed class HyperlinkTextTests
{
    private const string LongUrl =
        "https://github.com/Devolutions/UniGetUI/releases/download/v2026.1.0/UniGetUI.Installer.x64.exe";

    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(HyperlinkTextTests).Assembly);

    [Fact]
    public Task SingleClickActivatesTheLink() => _session.Dispatch(() =>
    {
        var (window, link, activations) = ShowLink();

        Click(window, link);
        RunDispatcherPastDoubleClickTime();

        Assert.Equal(1, activations());
        window.Close();
    }, CancellationToken.None);

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public Task MultiClickSelectsTextWithoutActivatingTheLink(int clicks) => _session.Dispatch(() =>
    {
        var (window, link, activations) = ShowLink();

        for (int i = 0; i < clicks; i++)
            Click(window, link);
        RunDispatcherPastDoubleClickTime();

        Assert.Equal(0, activations());
        Assert.False(string.IsNullOrEmpty(link.SelectedText));
        Assert.NotNull(link.SelectionBrush);
        if (clicks == 3)
            Assert.Equal(LongUrl, link.SelectedText);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task DragSelectsTextWithoutActivatingTheLink() => _session.Dispatch(() =>
    {
        var (window, link, activations) = ShowLink();

        window.MouseDown(link.TranslatePoint(new Point(5, link.Bounds.Height / 2), window)!.Value, MouseButton.Left);
        Point end = link.TranslatePoint(new Point(200, link.Bounds.Height / 2), window)!.Value;
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        window.MouseUp(end, MouseButton.Left);
        RunDispatcherPastDoubleClickTime();

        Assert.False(string.IsNullOrEmpty(link.SelectedText));
        Assert.NotNull(link.SelectionBrush);
        Assert.Equal(0, activations());
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task DragThatReturnsToItsStartDoesNotActivateTheLink() => _session.Dispatch(() =>
    {
        var (window, link, activations) = ShowLink();

        Point start = link.TranslatePoint(new Point(40, link.Bounds.Height / 2), window)!.Value;
        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Point(150, 0), RawInputModifiers.LeftMouseButton);
        window.MouseMove(start, RawInputModifiers.LeftMouseButton);
        window.MouseUp(start, MouseButton.Left);
        RunDispatcherPastDoubleClickTime();

        Assert.Equal(0, activations());
        window.Close();
    }, CancellationToken.None);

    private static (Window Window, HyperlinkText Link, Func<int> Activations) ShowLink(double width = 800)
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any())
            Application.Current.Styles.Add(new FluentTheme());

        var link = new HyperlinkText
        {
            Text = LongUrl,
            VerticalAlignment = VerticalAlignment.Top,
        };
        int activations = 0;
        link.Activated += (_, _) => activations++;
        var window = new Window { Width = width, Height = 200, Content = link };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (window, link, () => activations);
    }

    private static void Click(Window window, HyperlinkText link)
    {
        Point point = link.TranslatePoint(new Point(40, link.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void RunDispatcherPastDoubleClickTime()
    {
        TimeSpan doubleClickTime =
            Application.Current?.PlatformSettings?.GetDoubleTapTime(PointerType.Mouse) ?? TimeSpan.FromMilliseconds(500);
        var frame = new DispatcherFrame();
        DispatcherTimer.RunOnce(() => frame.Continue = false, doubleClickTime + TimeSpan.FromMilliseconds(300));
        Dispatcher.UIThread.PushFrame(frame);
    }
}
