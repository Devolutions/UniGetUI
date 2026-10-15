using System.Reflection;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Data;
using CoreSettings = UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Tests;

public sealed class SidebarNavigationOrderTests : IDisposable
{
    private readonly HeadlessUnitTestSession _session = HeadlessUnitTestSession.StartNew(typeof(Application));
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), nameof(SidebarNavigationOrderTests), Guid.NewGuid().ToString("N"));

    public SidebarNavigationOrderTests()
    {
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        CoreSettings.ResetSettings();
    }

    public void Dispose()
    {
        _session.Dispose();
        CoreSettings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        Directory.Delete(_testRoot, recursive: true);
    }

    [Theory]
    [InlineData("Updates", "Bundles", 999d, new[] { PageType.Discover, PageType.Installed, PageType.Bundles, PageType.Updates })]
    [InlineData("Bundles", "Discover", -999d, new[] { PageType.Bundles, PageType.Discover, PageType.Updates, PageType.Installed })]
    public Task DraggingAPageReordersTheSidebarAndKeepsEveryPageVisible(
        string from, string to, double overshoot, PageType[] expected) => _session.Dispatch(() =>
    {
        var (window, vm, list) = ShowSidebar();
        Point start = Center(window, Item(list, from));
        Point end = Center(window, Item(list, to)) + new Point(0, overshoot);

        window.MouseDown(start, MouseButton.Left);
        for (int step = 1; step <= 10; step++)
            window.MouseMove(start + ((end - start) * step / 10d), RawInputModifiers.LeftMouseButton);
        window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.Equal(expected, vm.NavigationPages);
        Assert.Equal(expected.Select(page => page.ToString()), Items(list).Select(item => (string?)item.Tag));
        Assert.True(Item(list, from).IsFocused);
        AssertAllPresented(list);
        Assert.NotEqual("", CoreSettings.GetValue(CoreSettings.K.NavigationPageOrder));
    }, CancellationToken.None);

    [Fact]
    public Task EscapeCancelsADragWithoutReordering() => _session.Dispatch(() =>
    {
        var (window, vm, list) = ShowSidebar();
        PageType[] before = vm.NavigationPages;
        Point start = Center(window, Item(list, "Discover"));
        Point end = Center(window, Item(list, "Bundles"));

        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Point(0, 20), RawInputModifiers.LeftMouseButton);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        Assert.Equal(before, vm.NavigationPages);
        Assert.All(Items(list), item => Assert.Null(item.RenderTransform));
        AssertAllPresented(list);
        Assert.Equal("", CoreSettings.GetValue(CoreSettings.K.NavigationPageOrder));
    }, CancellationToken.None);

    [Fact]
    public Task AltArrowsMoveTheFocusedPageAndKeepItFocused() => _session.Dispatch(() =>
    {
        var (window, vm, list) = ShowSidebar();
        ListBoxItem updates = Item(list, "Updates");
        updates.Focus(NavigationMethod.Directional);

        window.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.ArrowDown, null);
        window.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.ArrowDown, null);
        window.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.ArrowDown, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([PageType.Discover, PageType.Installed, PageType.Bundles, PageType.Updates], vm.NavigationPages);
        Assert.True(updates.IsFocused);

        window.KeyPress(Key.Up, RawInputModifiers.Alt, PhysicalKey.ArrowUp, null);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Equal([PageType.Discover, PageType.Installed, PageType.Updates, PageType.Bundles], vm.NavigationPages);
        Assert.True(updates.IsFocused);
        AssertAllPresented(list);
    }, CancellationToken.None);

    [Theory]
    [InlineData(700)]
    [InlineData(330)]
    public Task TouchDraggingReordersInsteadOfScrollingTheSidebar(int windowHeight) => _session.Dispatch(() =>
    {
        var (window, vm, list) = ShowSidebar(windowHeight);
        var scrollViewer = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        Point start = Center(window, Item(list, "Discover"));
        Point end = Center(window, Item(list, "Bundles"));
        var touch = new TouchInjector(window);

        touch.Send(RawPointerEventType.TouchBegin, start);
        for (int step = 1; step <= 20; step++)
            touch.Send(RawPointerEventType.TouchUpdate, start + ((end - start) * step / 20d));
        touch.Send(RawPointerEventType.TouchEnd, end);
        window.UpdateLayout();

        Assert.Equal([PageType.Updates, PageType.Installed, PageType.Bundles, PageType.Discover], vm.NavigationPages);
        Assert.Equal(0d, scrollViewer.Offset.Y);
        AssertAllPresented(list);
    }, CancellationToken.None);

    [Fact]
    public Task MovingAPageWithTheKeyboardAnnouncesItsNewPosition() => _session.Dispatch(() =>
    {
        var (window, _, list) = ShowSidebar();
        var announcements = new List<string>();
        void Collect(object? _, AccessibilityAnnouncement announcement) => announcements.Add(announcement.Message);
        AccessibilityAnnouncementService.AnnouncementRequested += Collect;
        try
        {
            Item(list, "Updates").Focus(NavigationMethod.Directional);
            window.KeyPress(Key.Down, RawInputModifiers.Alt, PhysicalKey.ArrowDown, null);
            Dispatcher.UIThread.RunJobs();
        }
        finally
        {
            AccessibilityAnnouncementService.AnnouncementRequested -= Collect;
        }

        string name = AutomationProperties.GetName(Item(list, "Updates"))!;
        Assert.Equal([$"{name} moved to position 3 of 4"], announcements);
    }, CancellationToken.None);

    [Fact]
    public Task ScrollingTheSidebarDuringADragStillDropsWhereThePointerIs() => _session.Dispatch(() =>
    {
        var (window, vm, list) = ShowSidebar(330);
        var scrollViewer = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        Assert.True(scrollViewer.Extent.Height > scrollViewer.Viewport.Height);
        Point start = Center(window, Item(list, "Discover"));

        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Point(0, 20), RawInputModifiers.LeftMouseButton);
        scrollViewer.Offset = new Vector(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Point end = Center(window, Item(list, "Bundles")) + new Point(0, 10);
        window.MouseMove(end, RawInputModifiers.LeftMouseButton);
        window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([PageType.Updates, PageType.Installed, PageType.Bundles, PageType.Discover], vm.NavigationPages);
    }, CancellationToken.None);

    [Fact]
    public Task ADetachedSidebarStopsFollowingTheOrderUntilItIsShownAgain() => _session.Dispatch(() =>
    {
        var (window, vm, list) = ShowSidebar();
        var view = (SidebarView)window.Content!;
        string[] before = Items(list).Select(item => (string)item.Tag!).ToArray();

        window.Content = null;
        Dispatcher.UIThread.RunJobs();
        vm.ReorderNavigationPages([PageType.Bundles, PageType.Installed, PageType.Updates, PageType.Discover]);
        Assert.Equal(before, Items(list).Select(item => (string)item.Tag!));

        window.Content = view;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Assert.Equal(["Bundles", "Installed", "Updates", "Discover"], Items(list).Select(item => (string)item.Tag!));
        AssertAllPresented(list);
    }, CancellationToken.None);

    [Fact]
    public Task PressingAPageNavigatesOnReleaseAndDraggingItDoesNotNavigate() => _session.Dispatch(() =>
    {
        var (window, vm, list) = ShowSidebar();
        var view = (SidebarView)window.Content!;
        var requests = new List<PageType>();
        vm.NavigationRequested += (_, page) =>
        {
            requests.Add(page);
            view.IsHitTestVisible = false;
        };
        Point start = Center(window, Item(list, "Updates"));
        Point end = Center(window, Item(list, "Bundles")) + new Point(0, 10);

        window.MouseDown(start, MouseButton.Left);
        Assert.Empty(requests);
        for (int step = 1; step <= 10; step++)
            window.MouseMove(start + ((end - start) * step / 10d), RawInputModifiers.LeftMouseButton);
        window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(requests);
        Assert.Equal([PageType.Discover, PageType.Installed, PageType.Bundles, PageType.Updates], vm.NavigationPages);

        Point click = Center(window, Item(list, "Installed"));
        window.MouseDown(click, MouseButton.Left);
        Assert.Empty(requests);
        window.MouseUp(click, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal([PageType.Installed], requests);
    }, CancellationToken.None);

    [Fact]
    public Task KeyboardSelectionStillNavigatesImmediately() => _session.Dispatch(() =>
    {
        var (window, vm, list) = ShowSidebar();
        var requests = new List<PageType>();
        vm.NavigationRequested += (_, page) => requests.Add(page);
        Item(list, "Discover").Focus(NavigationMethod.Directional);

        window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([PageType.Updates], requests);
    }, CancellationToken.None);

    [Fact]
    public Task HoveringAfterAMissedReleaseDoesNotStartADrag() => _session.Dispatch(() =>
    {
        var (window, vm, list) = ShowSidebar();
        var requests = new List<PageType>();
        vm.NavigationRequested += (_, page) => requests.Add(page);
        Point start = Center(window, Item(list, "Updates"));

        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(start + new Point(0, 2), RawInputModifiers.LeftMouseButton);
        window.MouseMove(Center(window, Item(list, "Bundles")), RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();

        Assert.All(Items(list), item => Assert.Null(item.RenderTransform));
        Assert.Empty(requests);
        Assert.Null(list.SelectedItem);
        Assert.Equal([PageType.Discover, PageType.Updates, PageType.Installed, PageType.Bundles], vm.NavigationPages);
    }, CancellationToken.None);

    [Fact]
    public Task ReleasingAfterTheSidebarScrollsDropsWhereThePointerIs() => _session.Dispatch(() =>
    {
        var (window, vm, list) = ShowSidebar(330);
        var scrollViewer = list.GetVisualDescendants().OfType<ScrollViewer>().First();
        Point start = Center(window, Item(list, "Discover"));
        Point held = start + new Point(0, 20);

        window.MouseDown(start, MouseButton.Left);
        window.MouseMove(held, RawInputModifiers.LeftMouseButton);
        scrollViewer.Offset = new Vector(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        window.MouseUp(held, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.True(scrollViewer.Offset.Y > 0);
        Assert.Equal([PageType.Updates, PageType.Discover, PageType.Installed, PageType.Bundles], vm.NavigationPages);
    }, CancellationToken.None);

    private static (Window Window, SidebarViewModel ViewModel, ListBox List) ShowSidebar(int height = 700)
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any())
            Application.Current.Styles.Add(new FluentTheme());

        var vm = new SidebarViewModel();
        var view = new SidebarView { DataContext = vm };
        var window = new Window { Width = 300, Height = height, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (window, vm, view.FindControl<ListBox>("NavListBox")!);
    }

    private static ListBoxItem[] Items(ListBox list) => list.Items.OfType<ListBoxItem>().ToArray();

    private static ListBoxItem Item(ListBox list, string tag) => Items(list).Single(item => (string?)item.Tag == tag);

    private static Point Center(Window window, ListBoxItem item)
        => item.TranslatePoint(new Point(20, item.Bounds.Height / 2d), window)!.Value;

    private static void AssertAllPresented(ListBox list)
    {
        double previousTop = double.MinValue;
        foreach (ListBoxItem item in Items(list))
        {
            Assert.NotNull(item.GetVisualParent());
            Assert.True(item.IsEffectivelyVisible);
            Assert.True(item.Bounds.Height > 0);
            double top = item.TranslatePoint(default, list)!.Value.Y;
            Assert.True(top > previousTop);
            previousTop = top;
        }
    }

    private sealed class TouchInjector
    {
        private const BindingFlags AnyInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly Type RawTouchEventArgsType =
            typeof(RawPointerEventArgs).Assembly.GetType("Avalonia.Input.Raw.RawTouchEventArgs")
            ?? throw Unsupported("the Avalonia.Input.Raw.RawTouchEventArgs type");

        private readonly IInputDevice _device = Activator.CreateInstance(typeof(TouchDevice), nonPublic: true) as IInputDevice
            ?? throw Unsupported("a constructible TouchDevice");
        private readonly Action<RawInputEventArgs> _input;
        private readonly IInputRoot _root;
        private ulong _timestamp = 1;

        public TouchInjector(Window window)
        {
            _input = typeof(ITopLevelImpl).GetProperty("Input", AnyInstance)?.GetValue(window.PlatformImpl) as Action<RawInputEventArgs>
                ?? throw Unsupported("the ITopLevelImpl.Input callback");
            _root = typeof(TopLevel).GetProperties(AnyInstance).Where(property => property.GetIndexParameters().Length == 0)
                    .Select(property => property.GetValue(window)).OfType<IInputRoot>().FirstOrDefault()
                ?? typeof(TopLevel).GetFields(AnyInstance).Select(field => field.GetValue(window)).OfType<IInputRoot>().FirstOrDefault()
                ?? throw Unsupported("the window's IInputRoot");
        }

        private static InvalidOperationException Unsupported(string what) => new(
            $"Touch injection relies on Avalonia internals and could not find {what}. "
            + "Avalonia probably changed them; update TouchInjector for the new version.");

        public void Send(RawPointerEventType type, Point position)
        {
            _input((RawInputEventArgs)Activator.CreateInstance(
                RawTouchEventArgsType, _device, _timestamp++, _root, type, position, RawInputModifiers.None, 1L)!);
            Dispatcher.UIThread.RunJobs();
        }
    }
}
