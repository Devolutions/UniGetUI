namespace UniGetUI.Extensions.GitRepositories.Tests;

public sealed class GitRepositoryServiceTests : IDisposable
{
    private readonly GitSandbox _sandbox = new();

    private GitRepositoryService Service => _sandbox.Service;

    public void Dispose() => _sandbox.Dispose();

    [Fact]
    public async Task Locate_FindsTheRootFromASubfolder()
    {
        _sandbox.CreateOrigin();
        string clone = _sandbox.Clone("clone");
        string subfolder = Path.Combine(clone, "src", "deep");
        Directory.CreateDirectory(subfolder);

        GitRepositoryLocation? location = await Service.LocateAsync(subfolder);

        Assert.NotNull(location);
        Assert.Equal(clone, location.Root);
        Assert.Equal(clone, location.MainClonePath);
        Assert.False(location.IsLinkedWorktree);
    }

    [Fact]
    public async Task Locate_ResolvesALinkedWorktreeToItsMainClone()
    {
        _sandbox.CreateOrigin();
        string clone = _sandbox.Clone("clone");
        string worktree = Path.Combine(_sandbox.Root, "wt");
        _sandbox.Git(clone, "worktree", "add", "--quiet", "-b", "feature", worktree);

        GitRepositoryLocation? location = await Service.LocateAsync(worktree);

        Assert.NotNull(location);
        Assert.Equal(worktree, location.Root);
        Assert.Equal(clone, location.MainClonePath);
        Assert.True(location.IsLinkedWorktree);
    }

    [Fact]
    public async Task Locate_ReturnsNullOutsideARepository()
    {
        string folder = Path.Combine(_sandbox.Root, "plain");
        Directory.CreateDirectory(folder);

        Assert.Null(await Service.LocateAsync(folder));
        Assert.Null(await Service.LocateAsync(Path.Combine(_sandbox.Root, "missing")));
    }

    [Fact]
    public async Task Check_ReportsAFreshCloneAsUpToDate()
    {
        _sandbox.CreateOrigin();
        string clone = _sandbox.Clone("clone");

        GitRepositoryStatus status = await Service.CheckAsync(clone);

        Assert.Equal(GitRepositoryState.UpToDate, status.State);
        Assert.Equal(clone, status.Path);
        Assert.Equal("main", status.CurrentBranch);
        Assert.Equal("main", status.DefaultBranch);
        Assert.Equal("origin", status.Remote);
        Assert.Equal("origin/main", status.TrackingRef);
        Assert.False(status.CanUpdate);
    }

    [Fact]
    public async Task CheckThenUpdate_FastForwardsACloneThatIsBehind()
    {
        _sandbox.CreateOrigin();
        string clone = _sandbox.Clone("clone");
        _sandbox.PushFromElsewhere(commits: 2);

        GitRepositoryStatus status = await Service.CheckAsync(clone);
        Assert.Equal(GitRepositoryState.Behind, status.State);
        Assert.Equal(2, status.Behind);
        Assert.True(status.CanUpdate);

        GitUpdateResult result = await Service.UpdateAsync(clone, fetch: false);

        Assert.Equal(GitUpdateOutcome.Updated, result.Outcome);
        Assert.Equal(GitRepositoryState.UpToDate, result.Status.State);
        Assert.Equal(_sandbox.Head(clone, "origin/main"), _sandbox.Head(clone));
    }

    [Fact]
    public async Task Update_FetchesByItself()
    {
        _sandbox.CreateOrigin();
        string clone = _sandbox.Clone("clone");
        _sandbox.PushFromElsewhere();

        GitUpdateResult result = await Service.UpdateAsync(clone);

        Assert.Equal(GitUpdateOutcome.Updated, result.Outcome);
        Assert.Equal(_sandbox.Head(_sandbox.Origin, "main"), _sandbox.Head(clone));
    }

    [Fact]
    public async Task Update_ReportsAnUpToDateClone()
    {
        _sandbox.CreateOrigin();
        string clone = _sandbox.Clone("clone");

        GitUpdateResult result = await Service.UpdateAsync(clone);

        Assert.Equal(GitUpdateOutcome.AlreadyUpToDate, result.Outcome);
    }

    [Fact]
    public async Task Update_RefusesACloneWithChangedTrackedFiles()
    {
        _sandbox.CreateOrigin();
        string clone = _sandbox.Clone("clone");
        _sandbox.PushFromElsewhere();
        File.WriteAllText(Path.Combine(clone, "README.md"), "edited locally");
        string head = _sandbox.Head(clone);

        GitUpdateResult result = await Service.UpdateAsync(clone);

        Assert.Equal(GitUpdateOutcome.Refused, result.Outcome);
        Assert.Equal(GitRepositoryState.HasLocalChanges, result.Status.State);
        Assert.True(result.Status.HasLocalChanges);
        Assert.Equal(1, result.Status.Behind);
        Assert.Equal(head, _sandbox.Head(clone));
        Assert.Equal("edited locally", File.ReadAllText(Path.Combine(clone, "README.md")));
    }

    [Fact]
    public async Task Inspect_IgnoresUntrackedFilesAndNestedWorktrees()
    {
        _sandbox.CreateOrigin();
        string clone = _sandbox.Clone("clone");
        File.WriteAllText(Path.Combine(clone, "scratch.txt"), "not tracked");
        // Agents keep their worktrees inside the main clone, as untracked folders.
        _sandbox.Git(clone, "worktree", "add", "--quiet", "-b", "agent",
            Path.Combine(clone, ".claude", "worktrees", "agent"));

        GitRepositoryStatus status = await Service.InspectAsync(clone);

        Assert.Equal(GitRepositoryState.UpToDate, status.State);
        Assert.False(status.HasLocalChanges);
    }

    [Fact]
    public async Task Update_RefusesACloneOnAnotherBranch()
    {
        _sandbox.CreateOrigin();
        string clone = _sandbox.Clone("clone");
        _sandbox.PushFromElsewhere();
        _sandbox.Git(clone, "switch", "--quiet", "-c", "feature");

        GitUpdateResult result = await Service.UpdateAsync(clone);

        Assert.Equal(GitUpdateOutcome.Refused, result.Outcome);
        Assert.Equal(GitRepositoryState.NotOnDefaultBranch, result.Status.State);
        Assert.Equal("feature", result.Status.CurrentBranch);
        Assert.Equal("main", result.Status.DefaultBranch);
    }

    [Fact]
    public async Task Update_RefusesADivergedClone()
    {
        _sandbox.CreateOrigin();
        string clone = _sandbox.Clone("clone");
        _sandbox.Commit(clone, "local.txt", "local");
        string head = _sandbox.Head(clone);
        _sandbox.PushFromElsewhere();

        GitUpdateResult result = await Service.UpdateAsync(clone);

        Assert.Equal(GitUpdateOutcome.Refused, result.Outcome);
        Assert.Equal(GitRepositoryState.Diverged, result.Status.State);
        Assert.Equal(1, result.Status.Ahead);
        Assert.Equal(1, result.Status.Behind);
        Assert.Equal(head, _sandbox.Head(clone));
    }

    [Fact]
    public async Task Check_ReportsACloneThatIsAhead()
    {
        _sandbox.CreateOrigin();
        string clone = _sandbox.Clone("clone");
        _sandbox.Commit(clone, "local.txt", "local");

        GitRepositoryStatus status = await Service.CheckAsync(clone);

        Assert.Equal(GitRepositoryState.Ahead, status.State);
        Assert.Equal(1, status.Ahead);
        Assert.False(status.CanUpdate);
    }

    [Fact]
    public async Task Inspect_ReportsADetachedHead()
    {
        _sandbox.CreateOrigin();
        string clone = _sandbox.Clone("clone");
        _sandbox.Git(clone, "switch", "--quiet", "--detach");

        GitRepositoryStatus status = await Service.InspectAsync(clone);

        Assert.Equal(GitRepositoryState.DetachedHead, status.State);
        Assert.Null(status.CurrentBranch);
        Assert.Equal("main", status.DefaultBranch);
    }

    [Fact]
    public async Task Check_ReportsARepositoryWithoutARemote()
    {
        string repository = Path.Combine(_sandbox.Root, "local");
        _sandbox.Git(_sandbox.Root, "init", "--quiet", repository);
        _sandbox.Commit(repository, "file.txt", "content");

        GitRepositoryStatus status = await Service.CheckAsync(repository);

        Assert.Equal(GitRepositoryState.NoUpstream, status.State);
        Assert.Null(status.Remote);
    }

    [Fact]
    public async Task DefaultBranch_FollowsTheRemoteHead()
    {
        _sandbox.CreateOrigin();
        string seed = Path.Combine(_sandbox.Root, "seed");
        _sandbox.Git(seed, "push", "--quiet", "origin", "main:develop");
        _sandbox.Git(_sandbox.Origin, "symbolic-ref", "HEAD", "refs/heads/develop");
        string clone = _sandbox.Clone("clone");

        GitRepositoryStatus status = await Service.CheckAsync(clone);

        Assert.Equal("develop", status.DefaultBranch);
        Assert.Equal("develop", status.CurrentBranch);
        Assert.Equal(GitRepositoryState.UpToDate, status.State);
    }

    [Fact]
    public async Task DefaultBranch_FallsBackToMasterWithoutARemoteHead()
    {
        _sandbox.CreateOrigin(branch: "master");
        string clone = _sandbox.Clone("clone");
        _sandbox.Git(clone, "remote", "set-head", "origin", "--delete");

        GitRepositoryStatus status = await Service.CheckAsync(clone);

        Assert.Equal("master", status.DefaultBranch);
        Assert.Equal("origin/master", status.TrackingRef);
        Assert.Equal(GitRepositoryState.UpToDate, status.State);
    }

    [Fact]
    public async Task DefaultBranch_CanBeChosenPerRepository()
    {
        _sandbox.CreateOrigin();
        string seed = Path.Combine(_sandbox.Root, "seed");
        _sandbox.Git(seed, "push", "--quiet", "origin", "main:develop");
        string clone = _sandbox.Clone("clone");
        _sandbox.Git(clone, "switch", "--quiet", "develop");

        GitRepositoryStatus remoteDefault = await Service.CheckAsync(clone);
        GitRepositoryStatus chosen = await Service.CheckAsync(clone, defaultBranch: "develop");

        Assert.Equal(GitRepositoryState.NotOnDefaultBranch, remoteDefault.State);
        Assert.Equal("develop", chosen.DefaultBranch);
        Assert.Equal("origin/develop", chosen.TrackingRef);
        Assert.Equal(GitRepositoryState.UpToDate, chosen.State);
    }

    [Fact]
    public async Task Check_ReportsAnUnreachableRemote()
    {
        _sandbox.CreateOrigin();
        string clone = _sandbox.Clone("clone");
        _sandbox.Git(clone, "remote", "set-url", "origin", Path.Combine(_sandbox.Root, "gone.git"));

        GitRepositoryStatus status = await Service.CheckAsync(clone);

        Assert.Equal(GitRepositoryState.Error, status.State);
        Assert.False(string.IsNullOrWhiteSpace(status.Error));
    }
}
