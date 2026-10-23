using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using UniGetUI.Avalonia.Views.Controls;

namespace UniGetUI.Tests;

public sealed class LinkTextBlockTests
{
    private const string Label = "Homepage: ";
    private const string Url = "https://github.com/Devolutions/UniGetUI";
    private const string Version = "Version: 1.0";
    private const string Download = "Download installer";

    private static readonly int UrlStart = Label.Length;
    private static readonly int VersionStart = UrlStart + Url.Length + Environment.NewLine.Length;

    // The spacer adds a second line break before the download link.
    private static readonly int DownloadStart = VersionStart + Version.Length + 2 * Environment.NewLine.Length;

    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(LinkTextBlockTests).Assembly);

    private sealed class Probe
    {
        public int Opened;
        public int Downloaded;
    }

    [Fact]
    public Task ClickOnALinkActivatesIt() => _session.Dispatch(() =>
    {
        var (window, text, probe) = Show();

        Click(window, text, UrlStart + 5);
        RunDispatcherPastDoubleClickTime();

        Assert.Equal(1, probe.Opened);
        Assert.Equal(0, probe.Downloaded);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task ClickOnALinkAfterLineBreaksActivatesThatLink() => _session.Dispatch(() =>
    {
        var (window, text, probe) = Show();

        Click(window, text, DownloadStart + 3);
        RunDispatcherPastDoubleClickTime();

        Assert.Equal(0, probe.Opened);
        Assert.Equal(1, probe.Downloaded);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task ClickOnPlainTextActivatesNothing() => _session.Dispatch(() =>
    {
        var (window, text, probe) = Show();

        Click(window, text, 2);
        Click(window, text, VersionStart + 2);
        RunDispatcherPastDoubleClickTime();

        Assert.Equal(0, probe.Opened + probe.Downloaded);
        window.Close();
    }, CancellationToken.None);

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public Task MultiClickOnALinkSelectsTextWithoutActivatingIt(int clicks) => _session.Dispatch(() =>
    {
        var (window, text, probe) = Show();

        for (int i = 0; i < clicks; i++)
            Click(window, text, UrlStart + 10);
        RunDispatcherPastDoubleClickTime();

        Assert.Equal(0, probe.Opened);
        Assert.False(string.IsNullOrEmpty(text.SelectedText));
        Assert.NotNull(text.SelectionBrush);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task DragSelectsAcrossTheLabelAndTheLink() => _session.Dispatch(() =>
    {
        var (window, text, probe) = Show();

        Drag(window, text, 0, UrlStart + 8);

        Assert.StartsWith(Label + Url[..5], text.SelectedText);
        Assert.Equal(0, probe.Opened);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task DragSelectsAcrossRows() => _session.Dispatch(() =>
    {
        var (window, text, probe) = Show();

        Drag(window, text, UrlStart + 3, VersionStart + 5);

        Assert.Contains(Url[3..], text.SelectedText);
        Assert.Contains(Version[..4], text.SelectedText);
        Assert.Equal(0, probe.Opened);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task RightClickOnALinkOffersCopyLink() => _session.Dispatch(() =>
    {
        var (window, text, probe) = Show();

        MenuFlyout menu = RightClick(window, text, UrlStart + 5);
        MenuItem copyLink = CopyLinkItem(menu);
        Assert.True(copyLink.IsVisible);

        copyLink.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(Url, RunDispatcherUntilDone(window.Clipboard!.TryGetTextAsync()));
        Assert.Equal(0, probe.Opened);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task RightClickOutsideALinkHidesCopyLink() => _session.Dispatch(() =>
    {
        var (window, text, _) = Show();

        Assert.False(CopyLinkItem(RightClick(window, text, VersionStart + 2)).IsVisible);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task RightClickOnAnActionLinkHidesCopyLink() => _session.Dispatch(() =>
    {
        var (window, text, _) = Show();

        Assert.False(CopyLinkItem(RightClick(window, text, DownloadStart + 3)).IsVisible);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task DragThatReturnsToItsStartDoesNotActivateTheLink() => _session.Dispatch(() =>
    {
        var (window, text, probe) = Show();

        Point start = At(window, text, UrlStart + 5);
        window.MouseDown(start, MouseButton.Left);
        Pump(window);
        window.MouseMove(start + new Point(150, 0), RawInputModifiers.LeftMouseButton);
        Pump(window);
        window.MouseMove(start, RawInputModifiers.LeftMouseButton);
        Pump(window);
        window.MouseUp(start, MouseButton.Left);
        RunDispatcherPastDoubleClickTime();

        Assert.Equal(0, probe.Opened);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task RebuildingTheTextCancelsAPendingClick() => _session.Dispatch(() =>
    {
        var (window, text, probe) = Show();

        Click(window, text, UrlStart + 5);
        text.Clear();
        text.AddRun(new Run("Loaded"));
        RunDispatcherPastDoubleClickTime();

        Assert.Equal(0, probe.Opened);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task TabStepsThroughTheLinksAndEnterOpensThem() => _session.Dispatch(() =>
    {
        var (window, text, probe) = Show();

        text.Focus(NavigationMethod.Tab);
        Assert.Equal(Url, text.SelectedText);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(1, probe.Opened);

        window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null);
        Assert.Equal(Download, text.SelectedText);
        window.KeyPress(Key.Space, RawInputModifiers.None, PhysicalKey.Space, null);
        Assert.Equal(1, probe.Downloaded);

        window.KeyPress(Key.Tab, RawInputModifiers.Shift, PhysicalKey.Tab, null);
        Assert.Equal(Url, text.SelectedText);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task ScreenReadersSeeEachLinkAsAnInvokableHyperlink() => _session.Dispatch(() =>
    {
        var (window, text, probe) = Show();

        AutomationPeer peer = ControlAutomationPeer.CreatePeerForElement(text);
        var links = peer.GetChildren();
        Assert.Equal([Url, Download], links.Select(link => link.GetName()));
        Assert.All(links, link => Assert.Equal(AutomationControlType.Hyperlink, link.GetAutomationControlType()));

        Assert.IsAssignableFrom<IInvokeProvider>(links[0]).Invoke();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, probe.Opened);

        text.Clear();
        text.AddLink("Other", "https://example.com", () => { }, Brushes.Blue);
        Assert.Equal(["Other"], peer.GetChildren().Select(link => link.GetName()));
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task FocusingALaterLinkFromAutomationKeepsThatLinkFocused() => _session.Dispatch(() =>
    {
        var (window, text, probe) = Show();
        var other = new Button { Content = "Elsewhere" };
        window.Content = null;
        window.Content = new StackPanel { Children = { other, text } };
        window.UpdateLayout();
        other.Focus();
        Assert.False(text.IsFocused);

        AutomationPeer download = ControlAutomationPeer.CreatePeerForElement(text).GetChildren()[1];
        download.SetFocus();

        Assert.True(download.HasKeyboardFocus());
        Assert.Equal(Download, text.SelectedText);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        Assert.Equal(1, probe.Downloaded);
        Assert.Equal(0, probe.Opened);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task ShiftClickOnALinkExtendsTheSelectionWithoutActivatingIt() => _session.Dispatch(() =>
    {
        var (window, text, probe) = Show();

        Click(window, text, 2);
        Point point = At(window, text, UrlStart + 10);
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.Shift);
        Pump(window);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.Shift);
        RunDispatcherPastDoubleClickTime();

        Assert.Equal(0, probe.Opened);
        Assert.StartsWith(Label[2..] + Url[..5], text.SelectedText);
        window.Close();
    }, CancellationToken.None);

    [Fact]
    public Task CopyFromTheMenuCopiesTheSelection() => _session.Dispatch(() =>
    {
        var (window, text, _) = Show();

        Drag(window, text, UrlStart + 3, VersionStart + 5);
        string selected = text.SelectedText;
        MenuFlyout menu = RightClick(window, text, VersionStart + 2);
        MenuItem copy = menu.Items.OfType<MenuItem>().Single(item => (string?)item.Header == "Copy");
        Assert.True(copy.IsEnabled);

        copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(selected, RunDispatcherUntilDone(window.Clipboard!.TryGetTextAsync()));
        window.Close();
    }, CancellationToken.None);

    private static (Window Window, LinkTextBlock Text, Probe Probe) Show()
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any())
            Application.Current.Styles.Add(new FluentTheme());

        var probe = new Probe();
        var text = new LinkTextBlock { VerticalAlignment = VerticalAlignment.Top };
        text.AddRun(new Run(Label) { FontWeight = FontWeight.Bold });
        text.AddLink(Url, Url, () => probe.Opened++, Brushes.Blue);
        text.StartLine();
        text.AddRun(new Run(Version), "Installed version differs");
        text.StartLine();
        text.AddLineBreak();
        text.AddLink(Download, null, () => probe.Downloaded++, Brushes.Blue);

        var window = new Window { Width = 800, Height = 300, Content = text };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        return (window, text, probe);
    }

    private static Point At(Window window, LinkTextBlock text, int position)
    {
        // Just inside the left edge, so the nearest caret is before the character, as a user would aim.
        Rect character = text.TextLayout.HitTestTextPosition(position);
        return text.TranslatePoint(new Point(character.Left + 1, character.Center.Y), window)!.Value;
    }

    private static void Click(Window window, LinkTextBlock text, int position)
    {
        Point point = At(window, text, position);
        window.MouseDown(point, MouseButton.Left);
        Pump(window);
        window.MouseUp(point, MouseButton.Left);
        Pump(window);
    }

    // A real window runs a layout pass between input events; selection changes rely on it.
    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    private static void Drag(Window window, LinkTextBlock text, int from, int to)
    {
        Point end = At(window, text, to);
        window.MouseDown(At(window, text, from), MouseButton.Left);
        Pump(window);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        Pump(window);
        window.MouseUp(end, MouseButton.Left);
        RunDispatcherPastDoubleClickTime();
    }

    private static MenuFlyout RightClick(Window window, LinkTextBlock text, int position)
    {
        Point point = At(window, text, position);
        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        var menu = Assert.IsType<MenuFlyout>(text.ContextFlyout);
        Assert.True(menu.IsOpen);
        return menu;
    }

    private static MenuItem CopyLinkItem(MenuFlyout menu) =>
        menu.Items.OfType<MenuItem>().Single(item => (string?)item.Header == "Copy link");

    private static void RunDispatcherPastDoubleClickTime()
    {
        TimeSpan doubleClickTime =
            Application.Current?.PlatformSettings?.GetDoubleTapTime(PointerType.Mouse) ?? TimeSpan.FromMilliseconds(500);
        var frame = new DispatcherFrame();
        DispatcherTimer.RunOnce(() => frame.Continue = false, doubleClickTime + TimeSpan.FromMilliseconds(300));
        Dispatcher.UIThread.PushFrame(frame);
    }

    private static T RunDispatcherUntilDone<T>(Task<T> task)
    {
        var frame = new DispatcherFrame();
        task.ContinueWith(_ => Dispatcher.UIThread.Post(() => frame.Continue = false), TaskScheduler.Default);
        Dispatcher.UIThread.PushFrame(frame);
        return task.Result;
    }
}
