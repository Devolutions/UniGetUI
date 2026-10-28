using Avalonia.Headless;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.Avalonia.Views;
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
        item.WorktreeCount = 0;
        Assert.StartsWith("Checked at ", item.DetailText);
    }

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
    public Task AddingAWorktreeAddsItsMainClone() => _session.Dispatch(async () =>
    {
        string clone = Path.Combine(_testRoot, "clone");
        await Git(_testRoot, "init", "--quiet", clone);
        File.WriteAllText(Path.Combine(clone, "README.md"), "hello");
        await Git(clone, "add", "README.md");
        await Git(clone, "commit", "--quiet", "-m", "First");
        string worktree = Path.Combine(_testRoot, "agent");
        await Git(clone, "worktree", "add", "--quiet", "-b", "agent", worktree);
        var vm = new GitRepositoriesViewModel(new GitRepositoryService(_git));

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
    }, CancellationToken.None);

    [Fact]
    public Task OneAddButtonTakesAClone_OrAFolderOfClones() => _session.Dispatch(async () =>
    {
        string dev = Path.Combine(_testRoot, "dev");
        string first = Path.Combine(dev, "first");
        string second = Path.Combine(dev, "group", "second");
        await Git(_testRoot, "init", "--quiet", first);
        await Git(_testRoot, "init", "--quiet", second);
        string inside = Path.Combine(first, "src");
        Directory.CreateDirectory(inside);
        var vm = new GitRepositoriesViewModel(new GitRepositoryService(_git));

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
    }, CancellationToken.None);

    [Fact]
    public Task CheckingFindsClonesBehindAndUpdatesThemWhenAsked() => _session.Dispatch(async () =>
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
    }, CancellationToken.None);

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
    public Task TheBranchesViewListsTheSelectedClonesBranches() => _session.Dispatch(async () =>
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

        vm.SelectedRepository = vm.Repositories[0];
        vm.DetailsViewIndex = GitRepositoriesViewModel.BranchesView;
        await vm.LoadDetailsAsync();

        Assert.True(vm.ShowBranches);
        Assert.Empty(vm.Worktrees);
        Assert.Equal(["main", "merged", "work"], vm.Branches.Select(branch => branch.Name).Order());
        GitBranchItemViewModel main = vm.Branches[0];
        Assert.True(main.IsDefault);
        Assert.False(main.CanDelete);
        Assert.True(Assert.Single(vm.Branches, branch => branch.Name == "merged").IsSafeToDelete);
        GitBranchItemViewModel work = Assert.Single(vm.Branches, branch => branch.Name == "work");
        Assert.False(work.IsSafeToDelete);
        Assert.Equal("1 commit not on the default branch", work.UniqueCommitsText);
        Assert.True(vm.HasMergedBranches);
        Assert.True(GitRepositoryList.ShowBranches);
        Assert.True(new GitRepositoriesViewModel(new GitRepositoryService(_git)).ShowBranches);
    }, CancellationToken.None);

    private async Task Commit(string repository, string content)
    {
        File.WriteAllText(Path.Combine(repository, "file.txt"), content);
        await Git(repository, "add", "file.txt");
        await Git(repository, "commit", "--quiet", "-m", content);
    }

    private async Task Git(string workingDirectory, params string[] arguments)
    {
        GitResult result = await _git.RunAsync(workingDirectory, arguments, null, CancellationToken.None);
        Assert.True(result.Success, $"git {string.Join(' ', arguments)} failed: {result.Output}");
    }
}
