using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.Views;
using UniGetUI.Avalonia.Views.Pages;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;
using CoreSettings = UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Tests;

public sealed class MorePageTests : IDisposable
{
    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(MorePageTests).Assembly);
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), nameof(MorePageTests), Guid.NewGuid().ToString("N"));

    public MorePageTests()
    {
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        CoreSettings.ResetSettings();
    }

    public void Dispose()
    {
        CoreSettings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        Directory.Delete(_testRoot, recursive: true);
    }

    [Fact]
    public void WithNothingMovedEveryPageStaysInTheSidebar()
    {
        var vm = new SidebarViewModel();

        Assert.Equal([PageType.Discover, PageType.Updates, PageType.Installed, PageType.Bundles], vm.NavigationPages);
        Assert.Empty(vm.MorePages);
    }

    [Fact]
    public void PagesMoveBetweenTheSidebarAndTheMorePageAndResetBringsThemBack()
    {
        var vm = new SidebarViewModel();

        vm.MovePageToMore(PageType.Updates);
        vm.MovePageToMore(PageType.Bundles);
        Assert.Equal([PageType.Discover, PageType.Installed], vm.NavigationPages);
        Assert.Equal([PageType.Updates, PageType.Bundles], vm.MorePages);

        vm.ReorderMorePages([PageType.Bundles, PageType.Updates]);
        Assert.Equal([PageType.Bundles, PageType.Updates], vm.MorePages);

        vm.PinPageToSidebar(PageType.Updates, 0);
        Assert.Equal([PageType.Updates, PageType.Discover, PageType.Installed], vm.NavigationPages);
        Assert.Equal([PageType.Bundles], vm.MorePages);

        var reloaded = new SidebarViewModel();
        Assert.Equal(vm.NavigationPages, reloaded.NavigationPages);
        Assert.Equal(vm.MorePages, reloaded.MorePages);

        vm.ResetNavigationPages();
        Assert.Equal([PageType.Discover, PageType.Updates, PageType.Installed, PageType.Bundles], vm.NavigationPages);
        Assert.Empty(vm.MorePages);
        Assert.Equal("", CoreSettings.GetValue(CoreSettings.K.NavigationPageOrder));
        Assert.Equal("", CoreSettings.GetValue(CoreSettings.K.NavigationMorePages));
    }

    [Fact]
    public void PinningWithoutAPositionAddsThePageAtTheEndOfTheSidebar()
    {
        var vm = new SidebarViewModel();
        vm.MovePageToMore(PageType.Discover);

        vm.PinPageToSidebar(PageType.Discover);

        Assert.Equal([PageType.Updates, PageType.Installed, PageType.Bundles, PageType.Discover], vm.NavigationPages);
        Assert.Empty(vm.MorePages);
    }

    [Fact]
    public void AnExtensionTurnedOnLandsOnTheMorePageAndKeepsItsPlaceWhileHidden()
    {
        var vm = new SidebarViewModel();
        vm.SetExtensionEnabled(PageType.GitRepositories, true);
        Assert.DoesNotContain(PageType.GitRepositories, vm.NavigationPages);
        Assert.Equal([PageType.GitRepositories], vm.MorePages);

        vm.MovePageToMore(PageType.Updates, 0);
        vm.SetExtensionEnabled(PageType.GitRepositories, false);
        Assert.Equal([PageType.Updates], vm.MorePages);

        vm.SetExtensionEnabled(PageType.GitRepositories, true);
        Assert.Equal([PageType.Updates, PageType.GitRepositories], vm.MorePages);
        Assert.DoesNotContain(PageType.GitRepositories, vm.NavigationPages);
    }

    [Fact]
    public void AnExtensionTurnedOffFromTheSidebarComesBackToTheSidebar()
    {
        var vm = new SidebarViewModel();
        vm.SetExtensionEnabled(PageType.GitRepositories, true);
        vm.PinPageToSidebar(PageType.GitRepositories, 0);

        vm.SetExtensionEnabled(PageType.GitRepositories, false);
        vm.SetExtensionEnabled(PageType.GitRepositories, true);
        Assert.Equal(PageType.GitRepositories, vm.NavigationPages[0]);
        Assert.Empty(vm.MorePages);

        vm.MovePageToMore(PageType.GitRepositories);
        vm.SetExtensionEnabled(PageType.GitRepositories, false);
        vm.SetExtensionEnabled(PageType.GitRepositories, true);
        Assert.Equal([PageType.GitRepositories], vm.MorePages);
    }

    [Fact]
    public void ResetBringsBackAnExtensionThatWasTurnedOffOnTheMorePage()
    {
        var vm = new SidebarViewModel();
        vm.SetExtensionEnabled(PageType.GitRepositories, true);
        vm.SetExtensionEnabled(PageType.GitRepositories, false);

        vm.ResetNavigationPages();
        vm.SetExtensionEnabled(PageType.GitRepositories, true);

        Assert.Contains(PageType.GitRepositories, vm.NavigationPages);
        Assert.Empty(vm.MorePages);
    }

    [Fact]
    public void AnExtensionTurnedOnOutsideTheSettingsToggleStillLandsOnTheMorePage()
    {
        _ = new SidebarViewModel();
        CoreSettings.Set(CoreSettings.K.EnableGitRepositories, true);

        var vm = new SidebarViewModel();

        Assert.DoesNotContain(PageType.GitRepositories, vm.NavigationPages);
        Assert.Equal([PageType.GitRepositories], vm.MorePages);
    }

    [Fact]
    public void MovingToMoreAtAPositionCountsOnlyTheTilesShown()
    {
        CoreSettings.Set(CoreSettings.K.EnableGitRepositories, true);
        new SidebarViewModel().MovePageToMore(PageType.GitRepositories);
        CoreSettings.Set(CoreSettings.K.EnableGitRepositories, false);
        var vm = new SidebarViewModel();
        vm.MovePageToMore(PageType.Updates);

        vm.MovePageToMore(PageType.Bundles, 1);

        Assert.Equal([PageType.Updates, PageType.Bundles], vm.MorePages);
        CoreSettings.Set(CoreSettings.K.EnableGitRepositories, true);
        Assert.Equal([PageType.GitRepositories, PageType.Updates, PageType.Bundles], new SidebarViewModel().MorePages);
    }

    [Theory]
    [InlineData(true, PageType.Updates)]
    [InlineData(false, PageType.Managers)]
    public void CyclingFromAPageOffTheSidebarLandsOnTheSidebarWhenDiscoverMoved(bool forward, PageType expected)
    {
        PageType[] pages = [PageType.Updates, PageType.Installed, PageType.Settings, PageType.Managers];

        PageType next = forward
            ? MainWindowViewModel.GetNextPage(PageType.More, pages)
            : MainWindowViewModel.GetPreviousPage(PageType.More, pages);

        Assert.Equal(expected, next);
    }

    [Fact]
    public async Task RepeatedRequestsForAPageBeingOpenedShareOneNavigation()
    {
        var coalescer = new NavigationCoalescer();
        var pending = new TaskCompletionSource<bool>();
        int started = 0;
        Task<bool> Navigate(CancellationToken _)
        {
            started++;
            return pending.Task;
        }

        Task<bool> first = coalescer.RunAsync(PageType.More, true, Navigate);
        Task<bool> second = coalescer.RunAsync(PageType.More, true, Navigate);
        Assert.Equal(1, started);

        pending.SetResult(false);
        Assert.False(await first);
        Assert.False(await second);

        pending = new TaskCompletionSource<bool>();
        Task<bool> third = coalescer.RunAsync(PageType.More, true, Navigate);
        Task<bool> other = coalescer.RunAsync(PageType.Updates, true, Navigate);
        Task<bool> back = coalescer.RunAsync(PageType.More, false, Navigate);
        Assert.Equal(4, started);
        pending.SetResult(true);
        Assert.True(await third);
        Assert.True(await other);
        Assert.True(await back);
    }

    [Fact]
    public async Task ARequestForAnotherPageInBetweenDoesNotSplitTheSharedNavigation()
    {
        var coalescer = new NavigationCoalescer();
        var more = new TaskCompletionSource<bool>();
        var updates = new TaskCompletionSource<bool>();
        var started = new List<PageType>();

        Task<bool> first = coalescer.RunAsync(PageType.More, true, _ => { started.Add(PageType.More); return more.Task; });
        Task<bool> between = coalescer.RunAsync(PageType.Updates, true, _ => { started.Add(PageType.Updates); return updates.Task; });
        Task<bool> again = coalescer.RunAsync(PageType.More, true, _ => { started.Add(PageType.More); return more.Task; });

        Assert.Equal([PageType.More, PageType.Updates], started);
        more.SetResult(false);
        updates.SetResult(true);
        Assert.False(await first);
        Assert.False(await again);
        Assert.True(await between);
    }

    [Fact]
    public async Task ARequestCancelledBeforeItStartsNeverNavigates()
    {
        var coalescer = new NavigationCoalescer();
        int started = 0;
        Task<bool> Navigate(CancellationToken _)
        {
            started++;
            return Task.FromResult(true);
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coalescer.RunAsync(PageType.More, true, Navigate, new CancellationToken(canceled: true)));
        Assert.Equal(0, started);

        Assert.True(await coalescer.RunAsync(PageType.More, true, Navigate));
        Assert.Equal(1, started);
    }

    [Fact]
    public async Task CancellingOneCallerLeavesTheSharedNavigationRunningForTheOthers()
    {
        var coalescer = new NavigationCoalescer();
        var pending = new TaskCompletionSource<bool>();
        CancellationToken shared = default;
        using var firstCancellation = new CancellationTokenSource();
        using var joinedCancellation = new CancellationTokenSource();
        Task<bool> first = coalescer.RunAsync(PageType.More, true, token => { shared = token; return pending.Task; }, firstCancellation.Token);
        Task<bool> joined = coalescer.RunAsync(PageType.More, true, _ => pending.Task, joinedCancellation.Token);

        firstCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(shared.IsCancellationRequested);
        Assert.False(joined.IsCompleted);
        pending.SetResult(true);
        Assert.True(await joined);
    }

    [Fact]
    public async Task TheSharedNavigationIsCancelledOnceEveryCallerHasCancelled()
    {
        var coalescer = new NavigationCoalescer();
        var pending = new TaskCompletionSource<bool>();
        CancellationToken shared = default;
        using var firstCancellation = new CancellationTokenSource();
        using var joinedCancellation = new CancellationTokenSource();
        Task<bool> first = coalescer.RunAsync(PageType.More, true, token => { shared = token; return pending.Task; }, firstCancellation.Token);
        Task<bool> joined = coalescer.RunAsync(PageType.More, true, _ => pending.Task, joinedCancellation.Token);

        firstCancellation.Cancel();
        Assert.False(shared.IsCancellationRequested);
        joinedCancellation.Cancel();

        Assert.True(shared.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => joined);

        int started = 0;
        Task<bool> retry = coalescer.RunAsync(PageType.More, true, _ => { started++; return Task.FromResult(true); });
        Assert.Equal(1, started);
        Assert.True(await retry);
        pending.SetResult(true);
    }

    [Fact]
    public Task AnInvisibleSidebarThatIgnoresTheMouseIsNotADropTarget() => _session.Dispatch(() =>
    {
        var vm = new SidebarViewModel();
        var page = new MorePage { DataContext = vm };
        var hiddenPane = new SidebarView { DataContext = vm, Width = 250, HorizontalAlignment = HorizontalAlignment.Left, Opacity = 0, IsHitTestVisible = false };
        var panel = new Panel();
        panel.Children.Add(page);
        panel.Children.Add(hiddenPane);
        var window = Show(panel, 1000);
        Point overPane = new(100, 300);

        Assert.Null(NavigationDropTargets.At(window, overPane, page));

        hiddenPane.IsHitTestVisible = true;
        Assert.Same(hiddenPane, NavigationDropTargets.At(window, overPane, page));
    }, CancellationToken.None);

    [Fact]
    public Task AStraightHorizontalDragFromTheSidebarStillReachesTheMorePage() => _session.Dispatch(() =>
    {
        var vm = new SidebarViewModel();
        var sidebar = new SidebarView { DataContext = vm };
        var page = new MorePage { DataContext = vm };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*") };
        Grid.SetColumn(page, 1);
        grid.Children.Add(sidebar);
        grid.Children.Add(page);
        var window = Show(grid, 1000);
        var requests = new List<PageType>();
        vm.NavigationRequested += (_, target) => requests.Add(target);
        ListBoxItem installed = Item(sidebar.FindControl<ListBox>("NavListBox")!, "Installed");
        Point start = installed.TranslatePoint(new Point(20, installed.Bounds.Height / 2d), window)!.Value;

        Drag(window, start, start + new Point(500, 0));

        Assert.Equal([PageType.Installed], vm.MorePages);
        Assert.Empty(requests);
    }, CancellationToken.None);

    [Fact]
    public void AnExtensionAlreadyOnStaysInTheSidebar()
    {
        CoreSettings.Set(CoreSettings.K.EnableGitRepositories, true);

        var vm = new SidebarViewModel();

        Assert.Contains(PageType.GitRepositories, vm.NavigationPages);
        Assert.Empty(vm.MorePages);
    }

    [Fact]
    public Task DraggingASidebarPageOntoMoreMovesItToTheMorePage() => _session.Dispatch(() =>
    {
        var (window, vm, view) = ShowSidebar();
        var list = view.FindControl<ListBox>("NavListBox")!;
        var more = view.FindControl<ListBoxItem>("MoreNavBtn")!;
        var requests = new List<PageType>();
        vm.NavigationRequested += (_, page) => requests.Add(page);
        Point start = Center(window, Item(list, "Updates"));
        Point end = Center(window, more);

        window.MouseDown(start, MouseButton.Left);
        for (int step = 1; step <= 10; step++)
            window.MouseMove(start + ((end - start) * step / 10d), RawInputModifiers.LeftMouseButton);
        Assert.Contains("drop-target", more.Classes);
        window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([PageType.Discover, PageType.Installed, PageType.Bundles], vm.NavigationPages);
        Assert.Equal([PageType.Updates], vm.MorePages);
        Assert.Equal(["Discover", "Installed", "Bundles"], list.Items.OfType<ListBoxItem>().Select(item => (string)item.Tag!));
        Assert.DoesNotContain("drop-target", more.Classes);
        Assert.Empty(requests);
    }, CancellationToken.None);

    [Fact]
    public Task RightClickingASidebarPageOffersToMoveItWithoutOpeningIt() => _session.Dispatch(() =>
    {
        var (window, vm, view) = ShowSidebar();
        var list = view.FindControl<ListBox>("NavListBox")!;
        var requests = new List<PageType>();
        vm.NavigationRequested += (_, page) => requests.Add(page);
        ListBoxItem installed = Item(list, "Installed");
        Point point = Center(window, installed);

        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(requests);
        Assert.Null(list.SelectedItem);
        ContextMenu menu = Assert.IsType<ContextMenu>(installed.ContextMenu);
        Assert.True(menu.IsOpen);

        menu.Items.OfType<MenuItem>().Single().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        menu.Close();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([PageType.Installed], vm.MorePages);
        Assert.DoesNotContain(PageType.Installed, vm.NavigationPages);
        Assert.Empty(requests);
    }, CancellationToken.None);

    [Fact]
    public Task MoreStaysSelectedOnEveryPageItLeadsTo() => _session.Dispatch(() =>
    {
        var (_, vm, view) = ShowSidebar();
        var list = view.FindControl<ListBox>("NavListBox")!;
        var footer = view.FindControl<ListBox>("FooterNavListBox")!;
        var more = view.FindControl<ListBoxItem>("MoreNavBtn")!;
        vm.MovePageToMore(PageType.Bundles);

        foreach (PageType page in new[] { PageType.More, PageType.Bundles, PageType.OwnLog, PageType.ReleaseNotes })
        {
            vm.SelectNavButtonForPage(page);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(more, footer.SelectedItem);
            Assert.Null(list.SelectedItem);
        }

        vm.SelectNavButtonForPage(PageType.Discover);
        Dispatcher.UIThread.RunJobs();
        Assert.Null(footer.SelectedItem);
        Assert.Equal("Discover", (string?)((ListBoxItem?)list.SelectedItem)?.Tag);
    }, CancellationToken.None);

    [Fact]
    public Task ClickingMoreOpensTheMorePageEvenWhenItIsAlreadySelected() => _session.Dispatch(() =>
    {
        var (window, vm, view) = ShowSidebar();
        var more = view.FindControl<ListBoxItem>("MoreNavBtn")!;
        var requests = new List<PageType>();
        vm.NavigationRequested += (_, page) => requests.Add(page);
        vm.SelectNavButtonForPage(PageType.OwnLog);
        Dispatcher.UIThread.RunJobs();

        window.MouseDown(Center(window, more), MouseButton.Left);
        window.MouseUp(Center(window, more), MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(PageType.More, Assert.Single(requests.Distinct()));
    }, CancellationToken.None);

    [Fact]
    public Task TheMorePageShowsMovedPagesWithTheirBadgesAndCanPinThemBack() => _session.Dispatch(() =>
    {
        var vm = new SidebarViewModel();
        vm.MovePageToMore(PageType.Updates);
        vm.MovePageToMore(PageType.Bundles);
        var (_, page) = ShowMorePage(vm);
        var tiles = page.FindControl<WrapPanel>("PageTiles")!;
        var tools = page.FindControl<WrapPanel>("ToolTiles")!;

        Assert.Equal([PageType.Updates, PageType.Bundles], page.PageTileOrder);
        Assert.Equal(7, tools.Children.Count);
        Assert.False(page.FindControl<Border>("EmptyPagesCard")!.IsVisible);
        Assert.True(page.FindControl<TextBlock>("PagesHeader")!.IsVisible);

        vm.UpdatesBadgeCount = 5;
        Button updates = Tile(tiles, PageType.Updates);
        Border badge = updates.GetLogicalDescendants().OfType<Border>().Single(border => border.Child is TextBlock);
        Assert.True(badge.IsVisible);
        Assert.Equal("5", ((TextBlock)badge.Child!).Text);

        updates.ContextMenu!.Items.OfType<MenuItem>().Single().RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([PageType.Discover, PageType.Installed, PageType.Updates], vm.NavigationPages);
        Assert.Equal([PageType.Bundles], page.PageTileOrder);
    }, CancellationToken.None);

    [Fact]
    public Task ClickingATileOpensItsPage() => _session.Dispatch(() =>
    {
        var vm = new SidebarViewModel();
        vm.MovePageToMore(PageType.Installed);
        var (window, page) = ShowMorePage(vm);
        var requests = new List<PageType>();
        vm.NavigationRequested += (_, target) => requests.Add(target);
        Point point = Grip(window, Tile(page.FindControl<WrapPanel>("PageTiles")!, PageType.Installed));

        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([PageType.Installed], requests);
    }, CancellationToken.None);

    [Fact]
    public Task DraggingATileOverAnotherReordersTheMorePage() => _session.Dispatch(() =>
    {
        var vm = new SidebarViewModel();
        vm.MovePageToMore(PageType.Updates);
        vm.MovePageToMore(PageType.Bundles);
        var (window, page) = ShowMorePage(vm);
        var tiles = page.FindControl<WrapPanel>("PageTiles")!;
        var requests = new List<PageType>();
        vm.NavigationRequested += (_, target) => requests.Add(target);

        Drag(window, Grip(window, Tile(tiles, PageType.Updates)), Center(window, Tile(tiles, PageType.Bundles)));

        Assert.Equal([PageType.Bundles, PageType.Updates], vm.MorePages);
        Assert.Equal([PageType.Bundles, PageType.Updates], page.PageTileOrder);
        Assert.Empty(requests);
    }, CancellationToken.None);

    [Fact]
    public Task DraggingATileOntoTheSidebarPinsItWhereItIsDropped() => _session.Dispatch(() =>
    {
        var vm = new SidebarViewModel();
        vm.MovePageToMore(PageType.Bundles);
        var sidebar = new SidebarView { DataContext = vm };
        var page = new MorePage { DataContext = vm };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*") };
        Grid.SetColumn(page, 1);
        grid.Children.Add(sidebar);
        grid.Children.Add(page);
        var window = Show(grid, 1000);
        var list = sidebar.FindControl<ListBox>("NavListBox")!;
        ListBoxItem discover = Item(list, "Discover");
        Point overDiscoverTop = discover.TranslatePoint(new Point(20, 4), window)!.Value;

        Drag(window, Grip(window, Tile(page.FindControl<WrapPanel>("PageTiles")!, PageType.Bundles)), overDiscoverTop, release: false);
        Assert.True(sidebar.FindControl<Border>("ExternalDropIndicator")!.IsVisible);
        window.MouseUp(overDiscoverTop, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([PageType.Bundles, PageType.Discover, PageType.Updates, PageType.Installed], vm.NavigationPages);
        Assert.Empty(vm.MorePages);
        Assert.False(sidebar.FindControl<Border>("ExternalDropIndicator")!.IsVisible);
        Assert.Empty(page.PageTileOrder);
    }, CancellationToken.None);

    [Fact]
    public Task DraggingASidebarPageOntoTheMorePageDropsItWhereThePointerIs() => _session.Dispatch(() =>
    {
        var vm = new SidebarViewModel();
        vm.MovePageToMore(PageType.Bundles);
        var sidebar = new SidebarView { DataContext = vm };
        var page = new MorePage { DataContext = vm };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*") };
        Grid.SetColumn(page, 1);
        grid.Children.Add(sidebar);
        grid.Children.Add(page);
        var window = Show(grid, 1000);
        var requests = new List<PageType>();
        vm.NavigationRequested += (_, target) => requests.Add(target);
        ListBoxItem updates = Item(sidebar.FindControl<ListBox>("NavListBox")!, "Updates");
        Button bundles = Tile(page.FindControl<WrapPanel>("PageTiles")!, PageType.Bundles);
        Point start = updates.TranslatePoint(new Point(20, updates.Bounds.Height / 2d), window)!.Value;
        Point beforeBundles = bundles.TranslatePoint(new Point(10, bundles.Bounds.Height / 2d), window)!.Value;

        Drag(window, start, beforeBundles, release: false);
        Assert.True(page.FindControl<Border>("PageDropIndicator")!.IsVisible);
        Assert.Equal(0.35, updates.Opacity);
        Assert.Equal(0d, ((TranslateTransform)updates.RenderTransform!).Y);
        Assert.Contains("drop-active", page.FindControl<Border>("PageDropZone")!.Classes);
        window.MouseUp(beforeBundles, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([PageType.Updates, PageType.Bundles], vm.MorePages);
        Assert.Equal([PageType.Discover, PageType.Installed], vm.NavigationPages);
        Assert.Equal([PageType.Updates, PageType.Bundles], page.PageTileOrder);
        Assert.False(page.FindControl<Border>("PageDropIndicator")!.IsVisible);
        Assert.DoesNotContain("drop-active", page.FindControl<Border>("PageDropZone")!.Classes);
        Assert.Empty(requests);
    }, CancellationToken.None);

    [Fact]
    public Task DraggingASidebarPageOntoAnEmptyMorePageAddsIt() => _session.Dispatch(() =>
    {
        var vm = new SidebarViewModel();
        var sidebar = new SidebarView { DataContext = vm };
        var page = new MorePage { DataContext = vm };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*") };
        Grid.SetColumn(page, 1);
        grid.Children.Add(sidebar);
        grid.Children.Add(page);
        var window = Show(grid, 1000);
        ListBoxItem installed = Item(sidebar.FindControl<ListBox>("NavListBox")!, "Installed");
        Point start = installed.TranslatePoint(new Point(20, installed.Bounds.Height / 2d), window)!.Value;
        Point overPage = page.TranslatePoint(new Point(200, 200), window)!.Value;

        Drag(window, start, overPage);

        Assert.Equal([PageType.Installed], vm.MorePages);
        Assert.DoesNotContain(PageType.Installed, vm.NavigationPages);
    }, CancellationToken.None);

    [Fact]
    public void MoreShowsADotWhileAPageOnTheMorePageHasABadge()
    {
        var vm = new SidebarViewModel();
        var changes = new List<string?>();
        vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        vm.UpdatesBadgeCount = 4;
        Assert.False(vm.MoreBadgeVisible);

        vm.MovePageToMore(PageType.Updates);
        Assert.True(vm.MoreBadgeVisible);
        Assert.Contains(nameof(SidebarViewModel.MoreBadgeVisible), changes);

        vm.UpdatesBadgeCount = 0;
        Assert.False(vm.MoreBadgeVisible);

        vm.BundlesBadgeVisible = true;
        vm.MovePageToMore(PageType.Bundles);
        Assert.True(vm.MoreBadgeVisible);

        vm.PinPageToSidebar(PageType.Bundles);
        Assert.False(vm.MoreBadgeVisible);

        vm.SetPendingSelfUpdate("3.4.0");
        Assert.True(vm.MoreBadgeVisible);
    }

    [Fact]
    public Task TheIconRailHidesItsScrollBarSoBadgesStayVisible() => _session.Dispatch(() =>
    {
        var vm = new SidebarViewModel();
        var rail = new SidebarView { DataContext = vm, ShowLabels = false };
        var pane = new SidebarView { DataContext = vm };

        Assert.Equal(ScrollBarVisibility.Hidden, ScrollViewer.GetVerticalScrollBarVisibility(rail.FindControl<ListBox>("NavListBox")!));
        Assert.Equal(ScrollBarVisibility.Auto, ScrollViewer.GetVerticalScrollBarVisibility(pane.FindControl<ListBox>("NavListBox")!));

        rail.ShowLabels = true;
        Assert.Equal(ScrollBarVisibility.Auto, ScrollViewer.GetVerticalScrollBarVisibility(rail.FindControl<ListBox>("NavListBox")!));
    }, CancellationToken.None);

    [Fact]
    public Task ASidebarPageCanBeDroppedOnTheMorePageThroughTheOpenPanesDismissLayer() => _session.Dispatch(() =>
    {
        var vm = new SidebarViewModel();
        var sidebar = new SidebarView { DataContext = vm };
        var page = new MorePage { DataContext = vm };
        var dismiss = new Border { Background = Brushes.Transparent };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("300,*") };
        Grid.SetColumn(page, 1);
        Grid.SetColumn(dismiss, 1);
        grid.Children.Add(page);
        grid.Children.Add(dismiss);
        grid.Children.Add(sidebar);
        var window = Show(grid, 1000);
        ListBoxItem installed = Item(sidebar.FindControl<ListBox>("NavListBox")!, "Installed");
        Point start = installed.TranslatePoint(new Point(20, installed.Bounds.Height / 2d), window)!.Value;
        Point overPage = page.TranslatePoint(new Point(200, 200), window)!.Value;
        Assert.Same(dismiss, window.InputHitTest(overPage));

        Drag(window, start, overPage);

        Assert.Equal([PageType.Installed], vm.MorePages);
    }, CancellationToken.None);

    [Fact]
    public Task TheEndOfListMarkerWrapsToTheNextRowWhenTheLastTileEndsARow() => _session.Dispatch(() =>
    {
        var vm = new SidebarViewModel();
        vm.MovePageToMore(PageType.Updates);
        var page = new MorePage { DataContext = vm };
        var window = Show(page, 280);
        Button updates = Tile(page.FindControl<WrapPanel>("PageTiles")!, PageType.Updates);
        Point below = updates.TranslatePoint(new Point(20, updates.Bounds.Height + 30), window)!.Value;

        Assert.Equal(1, page.UpdateExternalDrop(below));

        Border marker = page.FindControl<Border>("PageDropIndicator")!;
        Assert.True(Canvas.GetLeft(marker) < 0);
        Assert.True(Canvas.GetTop(marker) > updates.Bounds.Bottom);
        page.EndExternalDrop();
    }, CancellationToken.None);

    [Fact]
    public Task DraggingNearAnEdgeScrollsThatViewAndRefreshesTheDropTarget() => _session.Dispatch(() =>
    {
        var viewer = new ScrollViewer { Content = new Border { Height = 2000 } };
        var window = Show(viewer, 400);
        int refreshed = 0;
        var scroller = new EdgeAutoScroller(() => refreshed++);

        scroller.Update(viewer, window, new Point(200, 350));
        Assert.Equal(0d, scroller.PendingStep);

        scroller.Update(viewer, window, new Point(200, viewer.Bounds.Height - 5));
        Assert.True(scroller.PendingStep > 0);
        scroller.Step();
        Assert.True(viewer.Offset.Y > 0);
        Assert.Equal(1, refreshed);

        scroller.Update(viewer, window, new Point(200, 5));
        Assert.True(scroller.PendingStep < 0);
        scroller.Stop();
        Assert.Equal(0d, scroller.PendingStep);
    }, CancellationToken.None);

    [Fact]
    public void TheMoreDotNotifiesOnlyWhenItTurnsOnOrOff()
    {
        var vm = new SidebarViewModel();
        vm.MovePageToMore(PageType.Updates);
        int notifications = 0;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SidebarViewModel.MoreBadgeVisible))
                notifications++;
        };

        vm.UpdatesBadgeCount = 3;
        vm.UpdatesBadgeCount = 5;
        vm.UpdatesBadgeCount = 0;

        Assert.Equal(2, notifications);
    }

    [Fact]
    public void PinningAtAPositionPutsThePageBeforeThePageThere()
    {
        CoreSettings.Set(CoreSettings.K.EnableGitRepositories, true);
        CoreSettings.Set(CoreSettings.K.EnableAgentSkills, true);
        var vm = new SidebarViewModel();
        Assert.Equal([PageType.GitRepositories, PageType.AgentSkills], vm.NavigationPages.TakeLast(2).ToArray());

        vm.MovePageToMore(PageType.GitRepositories);
        vm.PinPageToSidebar(PageType.GitRepositories, vm.NavigationPages.Length - 1);

        Assert.Equal([PageType.GitRepositories, PageType.AgentSkills], vm.NavigationPages.TakeLast(2).ToArray());
        Assert.Empty(vm.MorePages);
    }

    [Fact]
    public Task AnEmptyMorePageOffersTheExtensionsThatAreOff() => _session.Dispatch(() =>
    {
        var vm = new SidebarViewModel();
        var (_, page) = ShowMorePage(vm);
        int requests = 0;
        vm.ExtensionsRequested += (_, _) => requests++;

        Assert.True(page.FindControl<Border>("EmptyPagesCard")!.IsVisible);
        Assert.False(page.FindControl<TextBlock>("PagesHeader")!.IsVisible);
        Assert.Contains(CoreTools.Translate(AppExtensions.GitRepositories.Name), page.FindControl<TextBlock>("EmptyPagesText")!.Text);
        Button browse = page.FindControl<Button>("BrowseExtensionsButton")!;
        Assert.True(browse.IsVisible);

        browse.Command!.Execute(null);
        Assert.Equal(1, requests);

        vm.MovePageToMore(PageType.Bundles);
        Assert.False(page.FindControl<Border>("EmptyPagesCard")!.IsVisible);
        Assert.True(page.FindControl<TextBlock>("PagesHeader")!.IsVisible);
    }, CancellationToken.None);

    [Fact]
    public Task WithEveryExtensionOnTheEmptyMorePageOnlyExplainsMovingPages() => _session.Dispatch(() =>
    {
        foreach (AppExtension extension in AppExtensions.All.Where(extension => extension.IsAvailable()))
            CoreSettings.Set(extension.EnabledSetting, true);
        var vm = new SidebarViewModel();
        foreach (PageType moved in vm.MorePages)
            vm.PinPageToSidebar(moved);
        var (_, page) = ShowMorePage(vm);

        Assert.True(page.FindControl<Border>("EmptyPagesCard")!.IsVisible);
        Assert.False(page.FindControl<Button>("BrowseExtensionsButton")!.IsVisible);
        Assert.Equal(CoreTools.Translate("Keep the navigation menu short"), page.FindControl<TextBlock>("EmptyPagesTitle")!.Text);
    }, CancellationToken.None);

    [Fact]
    public Task TheEmptyMorePageCardFollowsExtensionsTurnedOnAndOffWhileItIsOpen() => _session.Dispatch(() =>
    {
        foreach (AppExtension extension in AppExtensions.All.Where(extension => extension.IsAvailable()))
            CoreSettings.Set(extension.EnabledSetting, true);
        var vm = new SidebarViewModel();
        foreach (PageType moved in vm.MorePages)
            vm.PinPageToSidebar(moved);
        var (_, page) = ShowMorePage(vm);
        TextBlock title = page.FindControl<TextBlock>("EmptyPagesTitle")!;
        Button browse = page.FindControl<Button>("BrowseExtensionsButton")!;
        Assert.Equal(CoreTools.Translate("Keep the navigation menu short"), title.Text);

        vm.SetExtensionEnabled(PageType.GitRepositories, false);
        Assert.Equal(CoreTools.Translate("Add more pages"), title.Text);
        Assert.True(browse.IsVisible);
        Assert.Contains(CoreTools.Translate(AppExtensions.GitRepositories.Name), page.FindControl<TextBlock>("EmptyPagesText")!.Text);

        vm.SetExtensionEnabled(PageType.GitRepositories, true);
        vm.PinPageToSidebar(PageType.GitRepositories);
        Assert.Equal(CoreTools.Translate("Keep the navigation menu short"), title.Text);
        Assert.False(browse.IsVisible);
    }, CancellationToken.None);

    [Fact]
    public Task EscapeCancelsATileDrag() => _session.Dispatch(() =>
    {
        var vm = new SidebarViewModel();
        vm.MovePageToMore(PageType.Updates);
        vm.MovePageToMore(PageType.Bundles);
        var (window, page) = ShowMorePage(vm);
        var tiles = page.FindControl<WrapPanel>("PageTiles")!;
        Point end = Center(window, Tile(tiles, PageType.Bundles));

        Drag(window, Grip(window, Tile(tiles, PageType.Updates)), end, release: false);
        Assert.Contains("dragging", Tile(tiles, PageType.Updates).Classes);
        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal([PageType.Updates, PageType.Bundles], vm.MorePages);
        Assert.Equal([PageType.Updates, PageType.Bundles], page.PageTileOrder);
        Assert.All(tiles.Children, tile => Assert.DoesNotContain("dragging", tile.Classes));
    }, CancellationToken.None);

    private static void Drag(Window window, Point start, Point end, bool release = true)
    {
        window.MouseDown(start, MouseButton.Left);
        for (int step = 1; step <= 10; step++)
        {
            window.MouseMove(start + ((end - start) * step / 10d), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }
        if (!release)
            return;
        window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static (Window Window, SidebarViewModel ViewModel, SidebarView View) ShowSidebar()
    {
        var vm = new SidebarViewModel();
        var view = new SidebarView { DataContext = vm };
        return (Show(view, 300), vm, view);
    }

    private static (Window Window, MorePage Page) ShowMorePage(SidebarViewModel vm)
    {
        var page = new MorePage { DataContext = vm };
        return (Show(page, 900), page);
    }

    private static Window Show(Control content, double width)
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any())
            Application.Current.Styles.Add(new FluentTheme());
        foreach (string brush in new[] { "SettingsCardBackground", "SettingsCardHoverBackground" })
        {
            if (!Application.Current.TryGetResource(brush, null, out _))
                Application.Current.Resources[brush] = Brushes.Transparent;
        }

        var window = new Window { Width = width, Height = 700, Content = content };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static ListBoxItem Item(ListBox list, string tag)
        => list.Items.OfType<ListBoxItem>().Single(item => (string?)item.Tag == tag);

    private static Button Tile(WrapPanel tiles, PageType page)
        => tiles.Children.OfType<Button>().Single(tile => (PageType?)tile.Tag == page);

    private static Point Grip(Window window, Button tile)
        => tile.TranslatePoint(new Point(tile.Padding.Left + 14, tile.Padding.Top + 14), window)!.Value;

    private static Point Center(Window window, Control control)
        => control.TranslatePoint(new Point(control.Bounds.Width / 2d, control.Bounds.Height / 2d), window)!.Value;
}
