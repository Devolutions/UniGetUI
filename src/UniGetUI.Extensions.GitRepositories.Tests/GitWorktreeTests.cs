namespace UniGetUI.Extensions.GitRepositories.Tests;

public sealed class GitWorktreeTests : IDisposable
{
    private readonly GitSandbox _sandbox = new();

    private GitRepositoryService Service => _sandbox.Service;

    public void Dispose() => _sandbox.Dispose();

    [Fact]
    public async Task ListWorktrees_ReportsWhatMakesEachSafeToRemove()
    {
        _sandbox.CreateOrigin();
        string clone = _sandbox.Clone("clone");
        string fresh = AddWorktree(clone, "fresh");
        string feature = AddWorktree(clone, "feature");
        _sandbox.Commit(feature, "feature.txt", "work");
        string dirty = AddWorktree(clone, "dirty");
        File.WriteAllText(Path.Combine(dirty, "untracked.txt"), "unsaved work");

        IReadOnlyList<GitWorktree> worktrees = await Service.ListWorktreesAsync(clone);

        Assert.Equal(4, worktrees.Count);
        Assert.Equal(3, await Service.CountLinkedWorktreesAsync(clone));
        Assert.True(worktrees[0].IsMain);
        Assert.Equal(clone, worktrees[0].Path);
        Assert.Equal("main", worktrees[0].Branch);

        GitWorktree freshWorktree = Assert.Single(worktrees, w => w.Path == fresh);
        Assert.Equal("fresh", freshWorktree.Branch);
        Assert.True(freshWorktree.IsMerged);
        Assert.False(freshWorktree.HasLocalChanges);
        Assert.NotNull(freshWorktree.LastCommitDate);

        GitWorktree featureWorktree = Assert.Single(worktrees, w => w.Path == feature);
        Assert.Equal(1, featureWorktree.UniqueCommits);
        Assert.False(featureWorktree.IsMerged);
        Assert.False(featureWorktree.HasLocalChanges);

        GitWorktree dirtyWorktree = Assert.Single(worktrees, w => w.Path == dirty);
        Assert.True(dirtyWorktree.HasLocalChanges);
    }

    [Fact]
    public async Task ListWorktrees_CanLeaveLocalChangesToBeCheckedOneByOne()
    {
        _sandbox.CreateOrigin();
        string clone = _sandbox.Clone("clone");
        string feature = AddWorktree(clone, "feature");
        _sandbox.Commit(feature, "feature.txt", "work");
        File.WriteAllText(Path.Combine(feature, "untracked.txt"), "unsaved work");
        // A detached worktree has no branch to read in the batch, so it is described on its own.
        string detached = Path.Combine(_sandbox.Root, "wt-detached");
        _sandbox.Git(clone, "worktree", "add", "--quiet", "--detach", detached, "feature");

        IReadOnlyList<GitWorktree> worktrees = await Service.ListWorktreesAsync(clone, checkLocalChanges: false);

        GitWorktree featureWorktree = Assert.Single(worktrees, w => w.Path == feature);
        Assert.Equal(1, featureWorktree.UniqueCommits);
        Assert.NotNull(featureWorktree.LastCommitDate);
        Assert.False(featureWorktree.HasLocalChanges);
        Assert.True(await Service.HasLocalChangesAsync(feature));

        GitWorktree detachedWorktree = Assert.Single(worktrees, w => w.Path == detached);
        Assert.Null(detachedWorktree.Branch);
        Assert.Equal(1, detachedWorktree.UniqueCommits);
        Assert.NotNull(detachedWorktree.LastCommitDate);
        Assert.False(await Service.HasLocalChangesAsync(detached));
        Assert.False(await Service.HasLocalChangesAsync(Path.Combine(_sandbox.Root, "gone")));
    }

    [Fact]
    public async Task ListWorktrees_ReportsABranchWhoseRemoteBranchWasDeleted()
    {
        _sandbox.CreateOrigin();
        string clone = _sandbox.Clone("clone");
        string feature = AddWorktree(clone, "feature");
        _sandbox.Commit(feature, "feature.txt", "work");
        _sandbox.Git(feature, "push", "--quiet", "-u", "origin", "feature");
        // As when a pull request is squash-merged and its branch deleted.
        _sandbox.Git(_sandbox.Origin, "branch", "-D", "feature");
        await Service.CheckAsync(clone);

        IReadOnlyList<GitWorktree> worktrees = await Service.ListWorktreesAsync(clone);

        GitWorktree featureWorktree = Assert.Single(worktrees, w => w.Path == feature);
        Assert.True(featureWorktree.UpstreamGone);
        Assert.False(featureWorktree.IsMerged);
    }

    [Fact]
    public async Task RemoveWorktree_RemovesAMergedWorktreeAndItsBranch()
    {
        _sandbox.CreateOrigin();
        string clone = _sandbox.Clone("clone");
        string fresh = AddWorktree(clone, "fresh");

        GitOperationResult result = await Service.RemoveWorktreeAsync(clone, fresh, deleteBranch: true);

        Assert.True(result.Success, result.Output);
        Assert.False(Directory.Exists(fresh));
        Assert.False(_sandbox.RefExists(clone, "refs/heads/fresh"));
        Assert.Single(await Service.ListWorktreesAsync(clone));
    }

    [Fact]
    public async Task RemoveWorktree_KeepsAChangedWorktreeUnlessForced()
    {
        _sandbox.CreateOrigin();
        string clone = _sandbox.Clone("clone");
        string dirty = AddWorktree(clone, "dirty");
        File.WriteAllText(Path.Combine(dirty, "untracked.txt"), "unsaved work");

        GitOperationResult refused = await Service.RemoveWorktreeAsync(clone, dirty);
        Assert.False(refused.Success);
        Assert.True(File.Exists(Path.Combine(dirty, "untracked.txt")));

        GitOperationResult forced = await Service.RemoveWorktreeAsync(clone, dirty, force: true);
        Assert.True(forced.Success, forced.Output);
        Assert.False(Directory.Exists(dirty));
        Assert.True(_sandbox.RefExists(clone, "refs/heads/dirty"));
    }

    [Fact]
    public async Task RemoveWorktree_KeepsAnUnmergedBranchUnlessForced()
    {
        _sandbox.CreateOrigin();
        string clone = _sandbox.Clone("clone");
        string feature = AddWorktree(clone, "feature");
        _sandbox.Commit(feature, "feature.txt", "work");

        GitOperationResult result = await Service.RemoveWorktreeAsync(clone, feature, deleteBranch: true);

        Assert.False(result.Success);
        Assert.False(Directory.Exists(feature));
        Assert.True(_sandbox.RefExists(clone, "refs/heads/feature"));
    }

    [Fact]
    public async Task RemoveWorktree_RefusesTheMainAndLockedWorktrees()
    {
        _sandbox.CreateOrigin();
        string clone = _sandbox.Clone("clone");
        string locked = AddWorktree(clone, "locked");
        _sandbox.Git(clone, "worktree", "lock", "--reason", "agent at work", locked);

        GitOperationResult main = await Service.RemoveWorktreeAsync(clone, clone, force: true);
        GitOperationResult lockedResult = await Service.RemoveWorktreeAsync(clone, locked, force: true);

        Assert.False(main.Success);
        Assert.False(lockedResult.Success);
        Assert.Contains("agent at work", lockedResult.Output);
        Assert.True(Directory.Exists(locked));
        GitWorktree lockedWorktree = Assert.Single(await Service.ListWorktreesAsync(clone), w => w.Path == locked);
        Assert.True(lockedWorktree.IsLocked);
        Assert.Equal("agent at work", lockedWorktree.LockReason);
    }

    [Fact]
    public async Task PruneWorktrees_ClearsWorktreesWhoseFolderWasDeleted()
    {
        _sandbox.CreateOrigin();
        string clone = _sandbox.Clone("clone");
        string stale = AddWorktree(clone, "stale");
        GitSandbox.DeleteFolder(stale);

        GitWorktree staleWorktree = Assert.Single(await Service.ListWorktreesAsync(clone), w => w.Path == stale);
        Assert.True(staleWorktree.IsPrunable);

        GitOperationResult result = await Service.PruneWorktreesAsync(clone);

        Assert.True(result.Success, result.Output);
        Assert.Single(await Service.ListWorktreesAsync(clone));
    }

    [Fact]
    public void ParseWorktreeList_ReadsEveryKindOfEntry()
    {
        string output = string.Join('\0',
            "worktree /repo", "HEAD 1111111111111111111111111111111111111111", "branch refs/heads/main", "",
            "worktree /repo/.claude/worktrees/agent", "HEAD 2222222222222222222222222222222222222222",
            "branch refs/heads/claude/agent", "locked agent at work", "",
            "worktree /tmp/detached", "HEAD 3333333333333333333333333333333333333333", "detached",
            "prunable gitdir file points to non-existent location", "") + '\0';

        IReadOnlyList<GitWorktree> worktrees = GitRepositoryService.ParseWorktreeList(output);

        Assert.Equal(3, worktrees.Count);
        Assert.True(worktrees[0].IsMain);
        Assert.Equal("main", worktrees[0].Branch);
        Assert.False(worktrees[1].IsMain);
        Assert.Equal("claude/agent", worktrees[1].Branch);
        Assert.True(worktrees[1].IsLocked);
        Assert.Equal("agent at work", worktrees[1].LockReason);
        Assert.Null(worktrees[2].Branch);
        Assert.Equal("3333333333333333333333333333333333333333", worktrees[2].Head);
        Assert.True(worktrees[2].IsPrunable);
    }

    private string AddWorktree(string clone, string branch)
    {
        string path = Path.Combine(_sandbox.Root, "wt-" + branch);
        _sandbox.Git(clone, "worktree", "add", "--quiet", "-b", branch, path);
        return path;
    }
}
