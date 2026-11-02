using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Avalonia.Views;
using UniGetUI.Avalonia.Views.Pages;
using UniGetUI.Core.Data;
using UniGetUI.Extensions.GitRepositories;
using UniGetUI.Interface;
using CoreSettings = UniGetUI.Core.SettingsEngine.Settings;

namespace UniGetUI.Tests;

public sealed class GitRepositoriesTests : IDisposable
{
    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(GitRepositoriesTests).Assembly);
    private readonly string _testRoot = Path.Combine(Path.GetTempPath(), nameof(GitRepositoriesTests), Guid.NewGuid().ToString("N"));
    private readonly GitRunner _git;

    public GitRepositoriesTests()
    {
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        CoreSettings.ResetSettings();

        // Repositories made here must not pick up the machine's hooks, signing or line-ending settings.
        string configuration = Path.Combine(_testRoot, "gitconfig");
        File.WriteAllText(configuration, """
            [user]
                name = UniGetUI Tests
                email = tests@example.com
            [init]
                defaultBranch = main
            [commit]
                gpgsign = false
            """);
        _git = new GitRunner(environment: new Dictionary<string, string?>
        {
            ["GIT_CONFIG_NOSYSTEM"] = "1",
            ["GIT_CONFIG_GLOBAL"] = configuration,
        });
    }

    public void Dispose()
    {
        CoreSettings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        try
        {
            foreach (string file in Directory.EnumerateFiles(_testRoot, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_testRoot, recursive: true);
        }
        catch (IOException) { }
    }

    [Fact]
    public void TheGitRepositoriesPageIsInTheSidebarOnlyWhenEnabled()
    {
        Assert.Equal(
            [PageType.Discover, PageType.Updates, PageType.Installed, PageType.Bundles, PageType.GitRepositories],
            NavigationPageOrder.DefaultPages(catalogEnabled: false, gitRepositoriesEnabled: true));
        Assert.Equal(
            [PageType.Discover, PageType.Updates, PageType.Installed, PageType.Bundles],
            NavigationPageOrder.Resolve("GitRepositories,Discover", catalogEnabled: false));
        Assert.Equal(
            [PageType.GitRepositories, PageType.Discover, PageType.Catalog, PageType.Updates, PageType.Installed, PageType.Bundles],
            NavigationPageOrder.Resolve("GitRepositories,Discover", catalogEnabled: true, gitRepositoriesEnabled: true));
    }

    [Fact]
    public void ReorderingWithGitRepositoriesHiddenKeepsItInPlace()
    {
        PageType[] full = NavigationPageOrder.Resolve("Discover,GitRepositories,Updates,Installed,Bundles,Catalog", true, true);
        PageType[] merged = NavigationPageOrder.Merge(full,
            [PageType.Bundles, PageType.Discover, PageType.Updates, PageType.Installed, PageType.Catalog]);

        Assert.Equal(
            [PageType.Bundles, PageType.GitRepositories, PageType.Discover, PageType.Updates, PageType.Installed, PageType.Catalog],
            merged);
        Assert.Equal(merged, NavigationPageOrder.Resolve(NavigationPageOrder.Serialize(merged), true, true));
    }

    [Fact]
    public Task TurningGitRepositoriesOnAndOffNeedsNoRestart() => _session.Dispatch(() =>
    {
        var sidebar = new SidebarViewModel();
        Assert.DoesNotContain(PageType.GitRepositories, sidebar.NavigationPages);

        sidebar.GitRepositoriesEnabled = true;
        Assert.Contains(PageType.GitRepositories, sidebar.NavigationPages);

        sidebar.GitRepositoriesBadgeCount = 3;
        sidebar.GitRepositoriesIsLoading = true;
        sidebar.GitRepositoriesEnabled = false;
        Assert.DoesNotContain(PageType.GitRepositories, sidebar.NavigationPages);
        Assert.False(sidebar.GitRepositoriesBadgeVisible);
        Assert.False(sidebar.GitRepositoriesIsLoading);
    }, CancellationToken.None);

    [Fact]
    public Task EveryExtensionIsOffByDefaultAndReachable() => _session.Dispatch(() =>
    {
        Assert.NotEmpty(AppExtensions.All);
        Assert.Equal(AppExtensions.All.Count, AppExtensions.All.Select(extension => extension.Id).Distinct().Count());
        foreach (AppExtension extension in AppExtensions.All)
        {
            Assert.False(extension.IsEnabled);
            Assert.Contains(extension.Id, IpcAppPages.SupportedPages);
            Assert.Equal(extension.Id, IpcAppPages.ToPageName(extension.Page.ToString()));
            Assert.IsType(extension.SettingsPage, extension.CreateSettingsPage());
        }
    }, CancellationToken.None);

    [Fact]
    public Task TheSettingsPageAndTheGitPageShareTheirChoices() => _session.Dispatch(() =>
    {
        Assert.Equal(
            GitRepositoryList.CheckIntervals.Select(minutes => minutes > 0 ? minutes.ToString(System.Globalization.CultureInfo.InvariantCulture) : ""),
            GitRepositoriesSettingsViewModel.CheckIntervalItems.Select(item => item.Value));

        var vm = new GitRepositoriesViewModel(new GitRepositoryService(_git));
        // As its page under Settings > Extensions saves them.
        CoreSettings.SetValue(CoreSettings.K.GitRepositoriesCheckInterval, "60");
        CoreSettings.Set(CoreSettings.K.GitRepositoriesAutoUpdate, true);
        vm.ReloadPreferences();

        Assert.Equal(Array.IndexOf(GitRepositoryList.CheckIntervals, 60), vm.CheckIntervalIndex);
        Assert.True(vm.AutoUpdate);
        vm.Stop();
    }, CancellationToken.None);

    [Fact]
    public void GitRepositoriesIsAnIpcNavigationTarget()
    {
        Assert.Contains("git-repositories", IpcAppPages.SupportedPages);
        Assert.Equal("git-repositories", IpcAppPages.NormalizePageName(" Git-Repositories "));
        Assert.Equal("git-repositories", IpcAppPages.ToPageName(nameof(PageType.GitRepositories)));
    }

    [Fact]
    public void TheRepositoryListKeepsEachCloneOnceWithItsBranch()
    {
        string clone = Path.Combine(_testRoot, "clone");

        Assert.True(GitRepositoryList.Add(clone));
        Assert.False(GitRepositoryList.Add(clone + Path.DirectorySeparatorChar));
        Assert.Equal([clone], GitRepositoryList.Load());
        Assert.Null(GitRepositoryList.GetBranch(clone));

        GitRepositoryList.SetBranch(clone, " develop ");
        Assert.Equal("develop", GitRepositoryList.GetBranch(clone));
        GitRepositoryList.SetBranch(clone, "  ");
        Assert.Null(GitRepositoryList.GetBranch(clone));

        GitRepositoryList.SetBranch(clone, "develop");
        GitRepositoryList.Remove(clone);
        Assert.Empty(GitRepositoryList.Load());
        Assert.Null(GitRepositoryList.GetBranch(clone));
    }

    [Fact]
    public void OnlyTheOfferedCheckIntervalsAreKept()
    {
        Assert.Equal(0, GitRepositoryList.CheckIntervalMinutes);
        GitRepositoryList.CheckIntervalMinutes = 60;
        Assert.Equal(60, GitRepositoryList.CheckIntervalMinutes);
        CoreSettings.SetValue(CoreSettings.K.GitRepositoriesCheckInterval, "7");
        Assert.Equal(0, GitRepositoryList.CheckIntervalMinutes);
        GitRepositoryList.CheckIntervalMinutes = 0;
        Assert.Equal("", CoreSettings.GetValue(CoreSettings.K.GitRepositoriesCheckInterval));
    }

    [Theory]
    [InlineData(GitRepositoryState.UpToDate, 0, GitBadgeKind.Ok)]
    [InlineData(GitRepositoryState.Behind, 2, GitBadgeKind.Update)]
    [InlineData(GitRepositoryState.HasLocalChanges, 2, GitBadgeKind.Warning)]
    [InlineData(GitRepositoryState.Diverged, 1, GitBadgeKind.Warning)]
    [InlineData(GitRepositoryState.NotOnDefaultBranch, 0, GitBadgeKind.Warning)]
    [InlineData(GitRepositoryState.Error, 0, GitBadgeKind.Error)]
    public void EveryStateHasABadgeAndAnExplanation(GitRepositoryState state, int behind, GitBadgeKind kind)
    {
        var status = new GitRepositoryStatus("C:/repo", state, "main", "main", "origin", "origin/main",
            Behind: behind, Error: "boom");
        var item = new GitRepositoryItemViewModel("C:/repo", null) { Status = status };

        Assert.Equal(kind, GitStatusText.Kind(status));
        Assert.NotEmpty(item.StateText);
        Assert.NotEmpty(item.StateDetail);
        Assert.Equal(state == GitRepositoryState.Behind, item.CanUpdate);
        Assert.Equal(GitBadgeKind.Unknown, GitStatusText.Kind(null));
    }

    [Fact]
    public void TheLineUnderTheBranchOnlyShowsWhatIsKnown()
    {
        var item = new GitRepositoryItemViewModel("C:/repo", null);
        Assert.Equal("", item.DetailText);

        item.WorktreeCount = 17;
        Assert.Equal("17 worktrees", item.DetailText);

        item.LastChecked = new DateTime(2026, 10, 28, 9, 41, 0);
        Assert.StartsWith("17 worktrees · Checked at ", item.DetailText);

        item.CloneSize = 2L * 1024 * 1024;
        Assert.StartsWith("17 worktrees · Checked at ", item.DetailText);
        Assert.Equal("2.0 MB", item.TotalSizeText);
        Assert.Equal("2.0 MB on disk", item.SizeText);
        item.WorktreesSize = 3L * 1024 * 1024 * 1024;
        Assert.Equal(2L * 1024 * 1024 + 3L * 1024 * 1024 * 1024, item.TotalSize);
        Assert.Equal("2.0 MB on disk, 3.0 GB in worktrees", item.SizeText);
        item.CloneSize = null;
        item.WorktreesSize = null;
        Assert.Null(item.TotalSize);
        Assert.Equal("", item.TotalSizeText);

        item.WorktreeCount = 0;
        Assert.StartsWith("Checked at ", item.DetailText);
    }

    [Fact]
    public Task SizeColumnsAppearInTheRepositoryAndWorktreeLists() => _session.Dispatch(() =>
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any())
            Application.Current.Styles.Add(new FluentTheme());

        var vm = new GitRepositoriesViewModel(new GitRepositoryService(_git)) { IsGitMissing = true };
        var repository = new GitRepositoryItemViewModel(Path.Combine(_testRoot, "clone"), null) { WorktreeCount = 1 };
        vm.Repositories.Add(repository);
        vm.SelectedRepository = repository;
        var worktree = new GitWorktreeItemViewModel(new GitWorktree(
            Path.Combine(_testRoot, "agent"), null, "agent", false, false, null, false));
        vm.Worktrees.Add(worktree);
        var view = new GitRepositoriesPage(vm);
        var window = new Window { Width = 900, Height = 700, Content = view };

        try
        {
            window.Show();
            window.UpdateLayout();
            repository.CloneSize = 2L * 1024 * 1024;
            repository.WorktreesSize = 3L * 1024 * 1024;
            worktree.Size = 3L * 1024 * 1024;
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            TextBlock total = view.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Text == "5.0 MB");
            TextBlock size = view.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Text == "3.0 MB");
            Assert.True(total.IsEffectivelyVisible);
            Assert.Equal("2.0 MB on disk, 3.0 MB in worktrees", ToolTip.GetTip(total));
            Assert.True(size.IsEffectivelyVisible);
            Assert.DoesNotContain("on disk", repository.DetailText);

            Button repositoryHeader = view.FindControl<Button>("RepositorySizeSort")!;
            Button worktreeHeader = view.FindControl<Button>("WorktreeSizeSort")!;
            Assert.True(repositoryHeader.IsEffectivelyVisible);
            Assert.True(worktreeHeader.IsEffectivelyVisible);
            Assert.Equal("Total size", AutomationProperties.GetName(repositoryHeader));
            Assert.Equal("Size", AutomationProperties.GetName(worktreeHeader));
            repositoryHeader.Command!.Execute(null);
            worktreeHeader.Command!.Execute(null);
            Assert.Equal(GitListSort.SizeDescending, vm.RepositorySort);
            Assert.Equal(GitListSort.SizeDescending, vm.WorktreeSort);
            Assert.Equal("Total size ▼", AutomationProperties.GetName(repositoryHeader));
            Assert.Equal("Size ▼", AutomationProperties.GetName(worktreeHeader));
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    }, CancellationToken.None);

    [Theory]
    [InlineData(1200, 12, 2)]
    [InlineData(1200, 12, 12)]
    [InlineData(900, 2, 12)]
    [InlineData(700, 2, 2)]
    public Task GitListColumnsAlignAtDifferentWidthsAndScrollbarStates(
        int width, int repositoryCount, int worktreeCount) => _session.Dispatch(() =>
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any())
            Application.Current.Styles.Add(new FluentTheme());

        var vm = new GitRepositoriesViewModel(new GitRepositoryService(_git)) { IsGitMissing = true };
        var repositories = new List<GitRepositoryItemViewModel>();
        for (int i = 0; i < repositoryCount; i++)
        {
            string path = Path.Combine(_testRoot, $"clone-{i}");
            var repository = new GitRepositoryItemViewModel(path, null)
            {
                CloneSize = (i + 1L) * 1024 * 1024,
                WorktreeCount = 6,
                LastChecked = new DateTime(2026, 11, 1, 21, 22, 0),
                Status = new GitRepositoryStatus(path, i == 1 ? GitRepositoryState.Error : GitRepositoryState.UpToDate,
                    "main", "main", Error: i == 1 ? "Git failed" : null),
            };
            repositories.Add(repository);
            vm.Repositories.Add(repository);
        }
        vm.SelectedRepository = repositories[0];

        var worktrees = new List<GitWorktreeItemViewModel>();
        for (int i = 0; i < worktreeCount; i++)
        {
            var worktree = new GitWorktreeItemViewModel(new GitWorktree(
                Path.Combine(_testRoot, $"worktree-{i}"), null, "main", i == 0, false, null, false,
                HasLocalChanges: i == 1, UniqueCommits: i == 1 ? 3 : 0))
            {
                Size = (i + 1L) * 1024 * 1024,
            };
            worktrees.Add(worktree);
            vm.Worktrees.Add(worktree);
        }

        var view = new GitRepositoriesPage(vm);
        var window = new Window { Width = width, Height = 700, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();

            var repoList = view.FindControl<ListBox>("RepositoryList")!;
            var repoScroll = repoList.GetVisualDescendants().OfType<ScrollViewer>().First();
            var worktreeScroll = view.FindControl<ScrollViewer>("WorktreeScroll")!;
            Assert.Equal(repositoryCount > 2, repoScroll.Extent.Height > repoScroll.Viewport.Height);
            Assert.Equal(worktreeCount > 2, worktreeScroll.Extent.Height > worktreeScroll.Viewport.Height);

            Grid Row(object item) => view.GetVisualDescendants().OfType<Grid>()
                .Single(grid => grid.Classes.Contains("git-list-row") && ReferenceEquals(grid.DataContext, item));
            Control Cell(Grid row, string className) => row.GetVisualDescendants().OfType<Control>()
                .Single(control => control.Classes.Contains(className));
            TextBlock Name(Grid row, string name) => row.GetVisualDescendants().OfType<TextBlock>()
                .Single(text => text.Text == name);
            static double Left(Control control, Window root) => control.TranslatePoint(default, root)!.Value.X;
            static double Right(Control control, Window root) => Left(control, root) + control.Bounds.Width;
            void SameLeft(Control first, Control second) =>
                Assert.InRange(Math.Abs(Left(first, window) - Left(second, window)), 0, 0.75);
            void SameRight(Control first, Control second) =>
                Assert.InRange(Math.Abs(Right(first, window) - Right(second, window)), 0, 0.75);

            Grid repoRow = Row(repositories[0]);
            Grid mainRow = Row(worktrees[0]);
            Grid otherRow = Row(worktrees[1]);
            var repoName = view.FindControl<TextBlock>("RepositoryNameLabel")!;
            var worktreeName = view.FindControl<TextBlock>("WorktreeNameLabel")!;
            var repoStatus = view.FindControl<TextBlock>("RepositoryStatusHeader")!;
            var worktreeStatus = view.FindControl<TextBlock>("WorktreeStatusHeader")!;
            var repoSize = view.FindControl<TextBlock>("RepositorySizeLabel")!;
            var worktreeSize = view.FindControl<TextBlock>("WorktreeSizeLabel")!;
            var repoActions = view.FindControl<TextBlock>("RepositoryActionsHeader")!;
            var worktreeActions = view.FindControl<TextBlock>("WorktreeActionsHeader")!;

            SameLeft(repoName, worktreeName);
            SameLeft(repoName, Name(repoRow, repositories[0].Name));
            SameLeft(worktreeName, Name(mainRow, worktrees[0].Name));
            SameLeft(repoStatus, worktreeStatus);
            SameLeft(repoStatus, Cell(repoRow, "git-status-cell"));
            SameLeft(worktreeStatus, Cell(mainRow, "git-status-cell"));
            SameRight(repoSize, worktreeSize);
            SameRight(repoSize, Cell(repoRow, "git-size-cell"));
            SameRight(worktreeSize, Cell(mainRow, "git-size-cell"));
            SameRight(worktreeSize, Cell(otherRow, "git-size-cell"));
            SameLeft(repoActions, worktreeActions);
            SameLeft(repoActions, Cell(repoRow, "git-actions"));
            SameLeft(worktreeActions, Cell(mainRow, "git-actions"));
            SameLeft(worktreeActions, Cell(otherRow, "git-actions"));

            Button MainOpen() => Cell(mainRow, "git-actions").GetVisualDescendants().OfType<Button>()
                .Single(button => button.Content as string == "Open folder");
            Button OtherOpen() => Cell(otherRow, "git-actions").GetVisualDescendants().OfType<Button>()
                .Single(button => button.Content as string == "Open folder");
            SameLeft(MainOpen(), OtherOpen());

            Control statusCell = Cell(otherRow, "git-status-cell");
            Border[] visibleTags = [.. statusCell.GetVisualDescendants().OfType<Border>()
                .Where(border => border.Classes.Contains("worktree-status") && border.IsEffectivelyVisible)];
            Assert.Equal(2, visibleTags.Length);
            Assert.All(visibleTags, tag => Assert.True(Right(tag, window) <= Right(statusCell, window) + 0.75));
            if (width == 700)
            {
                Assert.True(((TextBlock)visibleTags[0].Child!).Bounds.Height > 16);
                TextBlock details = Name(repoRow, repositories[0].DetailText);
                Assert.True(details.Bounds.Height < 20);
                Assert.Equal(repositories[0].DetailText, ToolTip.GetTip(details));
            }

            repositories[0].IsBusy = true;
            window.UpdateLayout();
            Control busyCell = Cell(repoRow, "git-status-cell");
            ProgressBar progress = busyCell.GetVisualDescendants().OfType<ProgressBar>().Single();
            Assert.True(Right(progress, window) <= Right(busyCell, window) + 0.75);
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    }, CancellationToken.None);

    [Fact]
    public Task SortIndicatorsKeepSizeLabelsAlignedAndAnnounceTheSortDirection() => _session.Dispatch(() =>
    {
        if (!Application.Current!.Styles.OfType<FluentTheme>().Any())
            Application.Current.Styles.Add(new FluentTheme());

        var vm = new GitRepositoriesViewModel(new GitRepositoryService(_git)) { IsGitMissing = true };
        var small = new GitRepositoryItemViewModel(Path.Combine(_testRoot, "small"), null) { CloneSize = 10 };
        var large = new GitRepositoryItemViewModel(Path.Combine(_testRoot, "large"), null) { CloneSize = 30 };
        vm.Repositories.Add(small);
        vm.Repositories.Add(large);
        vm.SelectedRepository = small;
        var first = new GitWorktreeItemViewModel(new GitWorktree(
            Path.Combine(_testRoot, "first"), null, "main", true, false, null, false))
        { Size = 10 };
        var second = new GitWorktreeItemViewModel(new GitWorktree(
            Path.Combine(_testRoot, "second"), null, "main", false, false, null, false))
        { Size = 30 };
        vm.Worktrees.Add(first);
        vm.Worktrees.Add(second);
        var view = new GitRepositoriesPage(vm);
        var window = new Window { Width = 900, Height = 700, Content = view };
        try
        {
            window.Show();
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var repoHeader = view.FindControl<Button>("RepositorySizeSort")!;
            var worktreeHeader = view.FindControl<Button>("WorktreeSizeSort")!;
            var repoLabel = view.FindControl<TextBlock>("RepositorySizeLabel")!;
            var worktreeLabel = view.FindControl<TextBlock>("WorktreeSizeLabel")!;
            double repoRight = repoLabel.TranslatePoint(new Point(repoLabel.Bounds.Width, 0), window)!.Value.X;
            double worktreeRight = worktreeLabel.TranslatePoint(new Point(worktreeLabel.Bounds.Width, 0), window)!.Value.X;

            void Click(Button button)
            {
                Point point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseDown(point, MouseButton.Left);
                window.MouseUp(point, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }

            Click(repoHeader);
            Click(worktreeHeader);
            Assert.Equal(new[] { large, small }, vm.Repositories);
            Assert.Equal(new[] { second, first }, vm.Worktrees);
            Assert.Same(small, vm.SelectedRepository);
            Assert.Equal("Total size ▼", AutomationProperties.GetName(repoHeader));
            Assert.Equal("Size ▼", AutomationProperties.GetName(worktreeHeader));
            Assert.InRange(Math.Abs(repoRight - repoLabel.TranslatePoint(new Point(repoLabel.Bounds.Width, 0), window)!.Value.X), 0, 0.75);
            Assert.InRange(Math.Abs(worktreeRight - worktreeLabel.TranslatePoint(new Point(worktreeLabel.Bounds.Width, 0), window)!.Value.X), 0, 0.75);

            Click(repoHeader);
            Click(worktreeHeader);
            Assert.Equal(new[] { small, large }, vm.Repositories);
            Assert.Equal(new[] { first, second }, vm.Worktrees);
            Assert.Equal("Total size ▲", AutomationProperties.GetName(repoHeader));
            Assert.Equal("Size ▲", AutomationProperties.GetName(worktreeHeader));
            Assert.InRange(Math.Abs(repoRight - repoLabel.TranslatePoint(new Point(repoLabel.Bounds.Width, 0), window)!.Value.X), 0, 0.75);
            Assert.InRange(Math.Abs(worktreeRight - worktreeLabel.TranslatePoint(new Point(worktreeLabel.Bounds.Width, 0), window)!.Value.X), 0, 0.75);
        }
        finally
        {
            window.Close();
            vm.Stop();
        }
    }, CancellationToken.None);

    [Fact]
    public Task RepositoriesSortBySizeAndName_KeepingUnmeasuredOnesLast() => _session.Dispatch(() =>
    {
        var vm = new GitRepositoriesViewModel(new GitRepositoryService(_git)) { IsGitMissing = true };
        var small = new GitRepositoryItemViewModel(Path.Combine(_testRoot, "b-small"), null) { CloneSize = 10 };
        var unknown = new GitRepositoryItemViewModel(Path.Combine(_testRoot, "a-unknown"), null);
        var large = new GitRepositoryItemViewModel(Path.Combine(_testRoot, "c-large"), null) { CloneSize = 5, WorktreesSize = 100 };
        vm.Repositories.Add(small);
        vm.Repositories.Add(unknown);
        vm.Repositories.Add(large);
        vm.SelectedRepository = small;

        try
        {
            vm.SortRepositoriesBySizeCommand.Execute(null);
            Assert.Equal(new[] { large, small, unknown }, vm.Repositories);
            vm.SortRepositoriesBySizeCommand.Execute(null);
            Assert.Equal(GitListSort.SizeAscending, vm.RepositorySort);
            Assert.Equal(new[] { small, large, unknown }, vm.Repositories);
            Assert.Same(small, vm.SelectedRepository);

            // A size measured later moves its row.
            unknown.CloneSize = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { unknown, small, large }, vm.Repositories);

            vm.SortRepositoriesBySizeCommand.Execute(null);
            Assert.Equal(GitListSort.Default, vm.RepositorySort);
            Assert.Equal(new[] { small, unknown, large }, vm.Repositories);

            vm.SortRepositoriesByNameCommand.Execute(null);
            Assert.Equal(new[] { unknown, small, large }, vm.Repositories);
            vm.SortRepositoriesByNameCommand.Execute(null);
            Assert.Equal(new[] { large, small, unknown }, vm.Repositories);
            Assert.Same(small, vm.SelectedRepository);
        }
        finally
        {
            vm.Stop();
        }
    }, CancellationToken.None);

    [Fact]
    public Task WorktreesSortBySize_IncludingOnesListedLater() => _session.Dispatch(() =>
    {
        var vm = new GitRepositoriesViewModel(new GitRepositoryService(_git)) { IsGitMissing = true };
        GitWorktreeItemViewModel Worktree(string name) => new(new GitWorktree(
            Path.Combine(_testRoot, name), null, name, false, false, null, false));
        GitWorktreeItemViewModel main = Worktree("main"), agent = Worktree("agent"), other = Worktree("other");

        try
        {
            vm.SortWorktreesBySizeCommand.Execute(null);
            vm.Worktrees.Add(main);
            vm.Worktrees.Add(agent);
            vm.Worktrees.Add(other);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { main, agent, other }, vm.Worktrees);

            agent.Size = 300;
            other.Size = 20;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { agent, other, main }, vm.Worktrees);

            main.Size = 1_000;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { main, agent, other }, vm.Worktrees);

            vm.SortWorktreesBySizeCommand.Execute(null);
            Assert.Equal(new[] { other, agent, main }, vm.Worktrees);
        }
        finally
        {
            vm.Stop();
        }
    }, CancellationToken.None);

    [Fact]
    public void FailedRefreshShowsABoundedSummaryAndMarksRemoteInformationStale()
    {
        string output = "From https://example.com/repo\n"
            + string.Concat(Enumerable.Repeat(" - [deleted] (none) -> origin/old\n", 300))
            + "fatal: Authentication failed\n";
        var item = new GitRepositoryItemViewModel("C:/repo", null)
        {
            Status = new GitRepositoryStatus("C:/repo", GitRepositoryState.Error,
                "ai/feature", "master", "origin", "origin/master", Error: output, RemoteRefreshFailed: true),
        };

        Assert.Equal("Could not refresh from remote", item.StateText);
        Assert.Equal("fatal: Authentication failed", item.StateDetail);
        Assert.Equal(output, item.Status.Error);
        Assert.Equal("ai/feature → master", item.BranchText);
        Assert.Contains("Remote information may be out of date", item.DetailText);
        Assert.Equal("Retry", item.CheckText);
        Assert.True(item.HasErrorDetails);
        Assert.False(item.CanUpdate);

        item.Status = item.Status with
        {
            State = GitRepositoryState.NotOnDefaultBranch,
            Error = null,
            RemoteRefreshFailed = false,
        };
        Assert.Equal("On ai/feature instead of master", item.StateText);
        Assert.Equal("Check", item.CheckText);
        Assert.False(item.HasErrorDetails);
        Assert.DoesNotContain("out of date", item.DetailText);
        Assert.False(item.CanUpdate);
    }

    [Fact]
    public Task ErrorDetailsAreScrollableAndCopyTheCompleteOutput() => DispatchAsync(async () =>
    {
        string output = string.Concat(Enumerable.Repeat(" - [deleted] (none) -> origin/old\n", 300))
            + "fatal: Authentication failed";
        var owner = new Window();
        try
        {
            var dialog = GitRepositoriesViewModel.CreateErrorDetailsDialog(output, owner);
            ContentControl? presenter = dialog.FindControl<ContentControl>("BodyPresenter");
            Assert.NotNull(presenter);
            var body = Assert.IsType<StackPanel>(presenter.Content);
            var summary = Assert.IsType<TextBlock>(body.Children[0]);
            Assert.Equal("fatal: Authentication failed", summary.Text);
            var scroll = Assert.IsType<ScrollViewer>(body.Children[1]);
            Assert.Equal(320, scroll.MaxHeight);
            var text = Assert.IsType<SelectableTextBlock>(scroll.Content);
            Assert.Equal(output, text.Text);
            Assert.Equal(TextWrapping.Wrap, text.TextWrapping);
            var copy = Assert.IsType<Button>(body.Children[2]);
            Assert.True(copy.IsEnabled);

            copy.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

            Assert.NotNull(owner.Clipboard);
            Assert.Equal(output, await owner.Clipboard.TryGetTextAsync());
            Assert.Equal("Retry", dialog.FindControl<Button>("PrimaryButton")?.Content);
            Assert.Equal("Close", dialog.FindControl<Button>("SecondaryButton")?.Content);
        }
        finally
        {
            owner.Close();
        }
    });

    [Fact]
    public void ScanningAFolderFindsMainClonesButNotWorktreesOrHiddenFolders()
    {
        string root = Path.Combine(_testRoot, "dev");
        string MakeClone(params string[] parts)
        {
            string path = Path.Combine([root, .. parts]);
            Directory.CreateDirectory(Path.Combine(path, ".git"));
            return path;
        }
        string first = MakeClone("first");
        string grouped = MakeClone("group", "second");
        MakeClone("group", "second", "vendor", "nested");
        MakeClone(".hidden", "third");
        MakeClone("too", "deep", "fourth");
        string worktree = Path.Combine(root, "worktree");
        Directory.CreateDirectory(worktree);
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: elsewhere");

        Assert.Equal([first, grouped], GitRepositoriesViewModel.FindClones(root));
    }

    [Fact]
    public Task AddingAWorktreeAddsItsMainClone() => DispatchAsync(async () =>
    {
        string clone = Path.Combine(_testRoot, "clone");
        await Git(_testRoot, "init", "--quiet", clone);
        File.WriteAllText(Path.Combine(clone, "README.md"), "hello");
        await Git(clone, "add", "README.md");
        await Git(clone, "commit", "--quiet", "-m", "First");
        string worktree = Path.Combine(_testRoot, "agent");
        await Git(clone, "worktree", "add", "--quiet", "-b", "agent", worktree);
        var vm = new GitRepositoriesViewModel(new GitRepositoryService(_git));

        try
        {
            Assert.True(await vm.AddPathAsync(worktree, quiet: false));
            Assert.False(await vm.AddPathAsync(clone, quiet: true));

            GitRepositoryItemViewModel item = Assert.Single(vm.Repositories);
            Assert.True(GitRepositoryService.SamePath(clone, item.Path));
            Assert.Equal(GitRepositoryState.NoUpstream, item.Status?.State);
            Assert.Equal(1, item.WorktreeCount);
            Assert.Same(item, vm.SelectedRepository);
            Assert.Equal(2, vm.Worktrees.Count);
            Assert.True(vm.Notice.IsOpen);
            Assert.Equal(InfoBarSeverity.Informational, vm.Notice.Severity);
            Assert.True(GitRepositoryService.SamePath(clone, Assert.Single(GitRepositoryList.Load())));

            // Sizes are measured in the background, after the list is shown.
            await WaitForAsync(() => vm.Worktrees.Count == 2
                && vm.Worktrees.All(worktree => worktree.HasSize) && item.WorktreesSize is not null);
            Assert.All(vm.Worktrees, worktree => Assert.True(worktree.Size > 0));
            Assert.Equal(vm.Worktrees.Single(worktree => worktree.IsMain).Size, item.CloneSize);
            Assert.Equal(vm.Worktrees.Single(worktree => !worktree.IsMain).Size, item.WorktreesSize);
            Assert.Contains("on disk", item.SizeText);
        }
        finally
        {
            vm.Stop();
        }
    });

    [Fact]
    public Task AddingACloneDoesNotWaitForItsSize_AndSharesTheMeasurement() => DispatchAsync(async () =>
    {
        string clone = Path.Combine(_testRoot, "clone");
        await Git(_testRoot, "init", "--quiet", clone);
        await Commit(clone, "first");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int calls = 0;
        var vm = new GitRepositoriesViewModel(new GitRepositoryService(_git), (path, excluded, cancellationToken) =>
        {
            Interlocked.Increment(ref calls);
            started.TrySetResult();
            release.Wait(cancellationToken);
            return 12_345;
        });

        try
        {
            Task<bool> add = vm.AddPathAsync(clone, quiet: false);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(await add.WaitAsync(TimeSpan.FromSeconds(15)));
            GitRepositoryItemViewModel repository = Assert.Single(vm.Repositories);
            await vm.CheckAllAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(vm.IsBusy);
            Assert.False(vm.IsLoadingDetails);
            Assert.False(Assert.Single(vm.Worktrees).IsCheckingChanges);
            Assert.Null(repository.CloneSize);
            Assert.Equal(1, Volatile.Read(ref calls));

            release.Set();
            await WaitForAsync(() => repository.CloneSize == 12_345
                && vm.Worktrees.Count == 1 && vm.Worktrees[0].Size == 12_345);
            await vm.CheckAllAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await WaitForAsync(() => vm.Worktrees.Count == 1 && vm.Worktrees[0].Size == 12_345);
            Assert.Equal(1, Volatile.Read(ref calls));
        }
        finally
        {
            release.Set();
            vm.Stop();
        }
    });

    [Fact]
    public Task SizeScansRunAtMostTwoAtATime() => DispatchAsync(async () =>
    {
        string clone = Path.Combine(_testRoot, "clone");
        await Git(_testRoot, "init", "--quiet", clone);
        await Commit(clone, "first");
        for (int i = 1; i <= 3; i++)
            await Git(clone, "worktree", "add", "--quiet", "-b", $"agent-{i}", Path.Combine(_testRoot, $"agent-{i}"));

        var twoStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        int calls = 0;
        var vm = new GitRepositoriesViewModel(new GitRepositoryService(_git), (_, _, cancellationToken) =>
        {
            if (Interlocked.Increment(ref calls) == 2)
                twoStarted.TrySetResult();
            release.Wait(cancellationToken);
            return 1_024;
        });

        try
        {
            Assert.True(await vm.AddPathAsync(clone, quiet: false).WaitAsync(TimeSpan.FromSeconds(15)));
            await twoStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            await Task.Delay(500);
            Assert.Equal(2, Volatile.Read(ref calls));
            Assert.False(vm.IsBusy);
            Assert.False(vm.IsLoadingDetails);

            release.Set();
            GitRepositoryItemViewModel repository = Assert.Single(vm.Repositories);
            await WaitForAsync(() => vm.Worktrees.Count == 4 && vm.Worktrees.All(worktree => worktree.HasSize)
                && repository.CloneSize == 1_024 && repository.WorktreesSize == 3_072);
            Assert.Equal(4, Volatile.Read(ref calls));
        }
        finally
        {
            release.Set();
            vm.Stop();
        }
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public Task RefreshingDuringAScanDoesNotRestoreTheOldSize(bool honorsCancellation) => DispatchAsync(async () =>
    {
        string clone = Path.Combine(_testRoot, "clone");
        await Git(_testRoot, "init", "--quiet", clone);
        await Commit(clone, "first");
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirst = new ManualResetEventSlim();
        int calls = 0;
        var vm = new GitRepositoriesViewModel(new GitRepositoryService(_git), (path, excluded, cancellationToken) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.TrySetResult();
                try { releaseFirst.Wait(honorsCancellation ? cancellationToken : CancellationToken.None); }
                catch (OperationCanceledException)
                {
                    firstCancelled.TrySetResult();
                    throw;
                }
                return 100;
            }
            secondStarted.TrySetResult();
            return 200;
        });

        try
        {
            Assert.True(await vm.AddPathAsync(clone, quiet: false).WaitAsync(TimeSpan.FromSeconds(15)));
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            GitRepositoryItemViewModel repository = Assert.Single(vm.Repositories);

            await vm.RefreshDetailsCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(15));
            await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            if (honorsCancellation)
                await firstCancelled.Task.WaitAsync(TimeSpan.FromSeconds(15));
            else
            {
                await WaitForAsync(() => vm.Worktrees.Count == 1 && vm.Worktrees[0].Size == 200);
                Assert.Null(repository.CloneSize);
                releaseFirst.Set();
            }
            await WaitForAsync(() => repository.CloneSize == 200
                && vm.Worktrees.Count == 1 && vm.Worktrees[0].Size == 200);
            Assert.Equal(2, Volatile.Read(ref calls));
            Assert.Equal(200, repository.CloneSize);
        }
        finally
        {
            releaseFirst.Set();
            vm.Stop();
        }
    });

    [Fact]
    public Task StoppingTheExtensionCancelsAnInProgressSizeScan() => DispatchAsync(async () =>
    {
        string clone = Path.Combine(_testRoot, "clone");
        await Git(_testRoot, "init", "--quiet", clone);
        await Commit(clone, "first");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var vm = new GitRepositoriesViewModel(new GitRepositoryService(_git), (path, excluded, cancellationToken) =>
        {
            started.TrySetResult();
            try { release.Wait(cancellationToken); }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
            return 100;
        });

        try
        {
            Assert.True(await vm.AddPathAsync(clone, quiet: false).WaitAsync(TimeSpan.FromSeconds(15)));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(15));
            vm.Stop();
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Null(Assert.Single(vm.Repositories).CloneSize);
        }
        finally
        {
            release.Set();
            vm.Stop();
        }
    });

    [Fact]
    public Task ANewNestedWorktreeInvalidatesCachedSizes() => DispatchAsync(async () =>
    {
        string clone = Path.Combine(_testRoot, "clone");
        await Git(_testRoot, "init", "--quiet", clone);
        await Commit(clone, "first");
        var vm = new GitRepositoriesViewModel(new GitRepositoryService(_git));
        try
        {
            Assert.True(await vm.AddPathAsync(clone, quiet: false));
            GitRepositoryItemViewModel repository = Assert.Single(vm.Repositories);
            await WaitForAsync(() => repository.CloneSize is not null);

            string nested = Path.Combine(clone, "agent");
            await Git(clone, "worktree", "add", "--quiet", "-b", "agent", nested);
            File.WriteAllBytes(Path.Combine(nested, "build-output.bin"), new byte[5_000]);
            await vm.CheckAllAsync();
            await WaitForAsync(() => repository.WorktreeCount == 1
                && repository.CloneSize is not null && repository.WorktreesSize is not null
                && vm.Worktrees.Count == 2 && vm.Worktrees.All(worktree => worktree.HasSize));

            Assert.Equal(DirectorySize.Measure(clone, [nested]), repository.CloneSize);
            Assert.Equal(DirectorySize.Measure(nested), repository.WorktreesSize);
            Assert.Equal(repository.CloneSize, vm.Worktrees.Single(worktree => worktree.IsMain).Size);
            Assert.Equal(repository.WorktreesSize, vm.Worktrees.Single(worktree => !worktree.IsMain).Size);
        }
        finally
        {
            vm.Stop();
        }
    });

    [Fact]
    public Task DiscoveringWorktreesInvalidatesAScanWithoutKnownPaths() => DispatchAsync(async () =>
    {
        string clone = Path.Combine(_testRoot, "clone");
        string nested = Path.Combine(clone, "agent");
        await Git(_testRoot, "init", "--quiet", clone);
        await Commit(clone, "first");
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var releaseFirst = new ManualResetEventSlim();
        int calls = 0;
        var vm = new GitRepositoriesViewModel(new GitRepositoryService(_git), (path, excluded, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1)
            {
                firstStarted.TrySetResult();
                releaseFirst.Wait();
                return 999;
            }
            if (!GitRepositoryService.SamePath(path, clone)) return 200;
            return excluded?.Any(folder => GitRepositoryService.SamePath(folder, nested)) == true ? 100 : 999;
        });

        try
        {
            Assert.True(await vm.AddPathAsync(clone, quiet: false).WaitAsync(TimeSpan.FromSeconds(15)));
            await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(15));
            GitRepositoryItemViewModel repository = Assert.Single(vm.Repositories);
            // A worktree listing can finish while the first size scan still has no known paths.
            repository.SizeWorktreePaths = null;

            await Git(clone, "worktree", "add", "--quiet", "-b", "agent", nested);
            await vm.LoadDetailsAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await WaitForAsync(() => vm.Worktrees.Count == 2 && vm.Worktrees.Any(item => !item.IsMain && item.Size == 200));
            releaseFirst.Set();
            await WaitForAsync(() => repository.CloneSize == 100 && repository.WorktreesSize == 200);
        }
        finally
        {
            releaseFirst.Set();
            vm.Stop();
        }
    });

    [Fact]
    public Task LoadingBranchDetailsSchedulesTheCloneSize() => DispatchAsync(async () =>
    {
        string clone = Path.Combine(_testRoot, "clone");
        await Git(_testRoot, "init", "--quiet", clone);
        await Commit(clone, "first");
        GitRepositoryList.Add(clone);
        var vm = new GitRepositoriesViewModel(new GitRepositoryService(_git), (_, _, _) => 12_345);
        try
        {
            vm.DetailsViewIndex = GitRepositoriesViewModel.BranchesView;
            GitRepositoryItemViewModel repository = Assert.Single(vm.Repositories);
            vm.SelectedRepository = repository;
            await vm.LoadDetailsAsync().WaitAsync(TimeSpan.FromSeconds(15));

            Assert.True(vm.ShowBranches);
            Assert.Empty(vm.Worktrees);
            await WaitForAsync(() => repository.CloneSize == 12_345);
            Assert.False(vm.IsLoadingDetails);
        }
        finally
        {
            vm.Stop();
        }
    });

    [Fact]
    public Task OneAddButtonTakesAClone_OrAFolderOfClones() => DispatchAsync(async () =>
    {
        string dev = Path.Combine(_testRoot, "dev");
        string first = Path.Combine(dev, "first");
        string second = Path.Combine(dev, "group", "second");
        await Git(_testRoot, "init", "--quiet", first);
        await Git(_testRoot, "init", "--quiet", second);
        string inside = Path.Combine(first, "src");
        Directory.CreateDirectory(inside);
        var vm = new GitRepositoriesViewModel(new GitRepositoryService(_git));

        try
        {
            // A folder inside a clone adds that clone, not what is under the folder.
            await vm.AddPathsAsync([inside]);
            Assert.True(GitRepositoryService.SamePath(first, Assert.Single(vm.Repositories).Path));

            // A folder that is no clone adds every clone found under it.
            await vm.AddPathsAsync([dev]);
            Assert.Equal(2, vm.Repositories.Count);
            Assert.Contains(vm.Repositories, item => GitRepositoryService.SamePath(second, item.Path));
            Assert.Equal("1 git clone was added", vm.Notice.Title);

            await vm.AddPathsAsync([dev]);
            Assert.Equal("Every git clone found is already in the list", vm.Notice.Title);
            await vm.AddPathsAsync([Path.Combine(_testRoot, "Data")]);
            Assert.StartsWith("No git clones were found in ", vm.Notice.Title);
        }
        finally
        {
            vm.Stop();
        }
    });

    [Fact]
    public Task CheckingFindsClonesBehindAndUpdatesThemWhenAsked() => DispatchAsync(async () =>
    {
        string origin = Path.Combine(_testRoot, "origin.git");
        await Git(_testRoot, "init", "--quiet", "--bare", origin);
        string other = Path.Combine(_testRoot, "other");
        await Git(_testRoot, "clone", "--quiet", origin, other);
        await Commit(other, "first");
        await Git(other, "push", "--quiet", "-u", "origin", "main");
        string clone = Path.Combine(_testRoot, "clone");
        await Git(_testRoot, "clone", "--quiet", origin, clone);
        await Commit(other, "second");
        await Git(other, "push", "--quiet");
        GitRepositoryList.Add(clone);
        var vm = new GitRepositoriesViewModel(new GitRepositoryService(_git));

        try
        {
            await vm.CheckAllAsync();
            Assert.Equal(1, vm.BehindCount);
            Assert.Equal(GitRepositoryState.Behind, vm.Repositories[0].Status?.State);
            Assert.NotNull(vm.Repositories[0].LastChecked);

            vm.AutoUpdate = true;
            await Commit(other, "third");
            await Git(other, "push", "--quiet");
            await vm.CheckAllAsync();

            Assert.True(CoreSettings.Get(CoreSettings.K.GitRepositoriesAutoUpdate));
            Assert.Equal(0, vm.BehindCount);
            Assert.Equal(GitRepositoryState.UpToDate, vm.Repositories[0].Status?.State);
            Assert.False(vm.IsBusy);
        }
        finally
        {
            vm.Stop();
        }
    });

    [Fact]
    public Task FailedRefreshBlocksUpdatesUntilARefreshSucceeds() => DispatchAsync(async () =>
    {
        string origin = Path.Combine(_testRoot, "origin.git");
        await Git(_testRoot, "init", "--quiet", "--bare", origin);
        string other = Path.Combine(_testRoot, "other");
        await Git(_testRoot, "clone", "--quiet", origin, other);
        await Commit(other, "first");
        await Git(other, "push", "--quiet", "-u", "origin", "main");
        string clone = Path.Combine(_testRoot, "clone");
        await Git(_testRoot, "clone", "--quiet", origin, clone);
        await Commit(other, "second");
        await Git(other, "push", "--quiet");
        GitRepositoryList.Add(clone);
        var vm = new GitRepositoriesViewModel(new GitRepositoryService(_git));
        try
        {
            GitRepositoryItemViewModel item = Assert.Single(vm.Repositories);
            await vm.CheckAllAsync();
            Assert.True(item.CanUpdate);
            string head = (await _git.RunAsync(clone, "rev-parse", "HEAD")).StandardOutput;

            await Git(clone, "remote", "set-url", "origin", Path.Combine(_testRoot, "gone.git"));
            vm.AutoUpdate = true;
            await vm.CheckAllAsync();

            Assert.NotNull(item.Status);
            Assert.True(item.Status.RemoteRefreshFailed);
            Assert.True(item.IsError);
            Assert.False(item.CanUpdate);
            Assert.Equal("main", item.Status.CurrentBranch);
            Assert.Equal(0, vm.BehindCount);
            await vm.InitializeAsync();
            Assert.True(item.Status.RemoteRefreshFailed);
            Assert.False(item.CanUpdate);
            // Even a direct command invocation must refresh, not trust cached tracking refs.
            await vm.UpdateCommand.ExecuteAsync(item);
            Assert.Equal(head, (await _git.RunAsync(clone, "rev-parse", "HEAD")).StandardOutput);
            Assert.True(item.IsError);
            Assert.Contains("gone.git", item.Status.Error);

            await Git(clone, "remote", "set-url", "origin", origin);
            await vm.CheckCommand.ExecuteAsync(item);
            Assert.Equal(GitRepositoryState.UpToDate, item.Status.State);
            Assert.False(item.Status.RemoteRefreshFailed);
            Assert.False(item.HasErrorDetails);
            Assert.Equal("Check", item.CheckText);
            Assert.DoesNotContain("out of date", item.DetailText);
            Assert.Equal((await _git.RunAsync(other, "rev-parse", "HEAD")).StandardOutput,
                (await _git.RunAsync(clone, "rev-parse", "HEAD")).StandardOutput);
        }
        finally
        {
            vm.Stop();
        }
    });

    [Fact]
    public void ThePanelRemembersItsViewAndASensibleHeight()
    {
        Assert.False(GitRepositoryList.ShowBranches);
        Assert.Equal(GitRepositoryList.DefaultDetailsHeight, GitRepositoryList.DetailsHeight);

        GitRepositoryList.ShowBranches = true;
        GitRepositoryList.DetailsHeight = 412.6;
        Assert.True(GitRepositoryList.ShowBranches);
        Assert.Equal(413, GitRepositoryList.DetailsHeight);

        GitRepositoryList.DetailsHeight = 10;
        Assert.Equal(GitRepositoryList.MinimumDetailsHeight, GitRepositoryList.DetailsHeight);
        CoreSettings.SetValue(CoreSettings.K.GitRepositoriesDetailsHeight, "tall");
        Assert.Equal(GitRepositoryList.DefaultDetailsHeight, GitRepositoryList.DetailsHeight);
    }

    [Fact]
    public Task TheBranchesViewListsTheSelectedClonesBranches() => DispatchAsync(async () =>
    {
        string origin = Path.Combine(_testRoot, "origin.git");
        await Git(_testRoot, "init", "--quiet", "--bare", origin);
        string clone = Path.Combine(_testRoot, "clone");
        await Git(_testRoot, "clone", "--quiet", origin, clone);
        await Commit(clone, "first");
        await Git(clone, "push", "--quiet", "-u", "origin", "main");
        await Git(clone, "branch", "merged");
        await Git(clone, "branch", "work");
        await Git(clone, "switch", "--quiet", "work");
        await Commit(clone, "unmerged");
        await Git(clone, "switch", "--quiet", "main");
        GitRepositoryList.Add(clone);
        var vm = new GitRepositoriesViewModel(new GitRepositoryService(_git));

        try
        {
            vm.SelectedRepository = vm.Repositories[0];
            vm.DetailsViewIndex = GitRepositoriesViewModel.BranchesView;
            await vm.LoadDetailsAsync();

            Assert.True(vm.ShowBranches);
            Assert.Empty(vm.Worktrees);
            Assert.Equal(new[] { "main", "merged", "work" }, vm.Branches.Select(branch => branch.Name).Order());
            GitBranchItemViewModel main = vm.Branches[0];
            Assert.True(main.IsDefault);
            Assert.False(main.CanDelete);
            Assert.True(Assert.Single(vm.Branches, branch => branch.Name == "merged").IsSafeToDelete);
            GitBranchItemViewModel work = Assert.Single(vm.Branches, branch => branch.Name == "work");
            Assert.False(work.IsSafeToDelete);
            Assert.Equal("1 commit not on the default branch", work.UniqueCommitsText);
            Assert.True(vm.HasMergedBranches);
            Assert.True(GitRepositoryList.ShowBranches);
            var reloaded = new GitRepositoriesViewModel(new GitRepositoryService(_git));
            try { Assert.True(reloaded.ShowBranches); }
            finally { reloaded.Stop(); }
        }
        finally
        {
            vm.Stop();
        }
    });

    // The generic overload awaits the operation; Dispatch(Action) would make an async lambda async void.
    private Task DispatchAsync(Func<Task> action) => _session.Dispatch<bool>(async () =>
    {
        await action();
        return true;
    }, CancellationToken.None);

    private async Task Commit(string repository, string content)
    {
        File.WriteAllText(Path.Combine(repository, "file.txt"), content);
        await Git(repository, "add", "file.txt");
        await Git(repository, "commit", "--quiet", "-m", content);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!condition())
            await Task.Delay(20, timeout.Token);
    }

    private async Task Git(string workingDirectory, params string[] arguments)
    {
        GitResult result = await _git.RunAsync(workingDirectory, arguments, null, CancellationToken.None);
        Assert.True(result.Success, $"git {string.Join(' ', arguments)} failed: {result.Output}");
    }
}
