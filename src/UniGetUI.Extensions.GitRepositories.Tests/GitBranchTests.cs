namespace UniGetUI.Extensions.GitRepositories.Tests;

public sealed class GitBranchTests : IDisposable
{
    private readonly GitSandbox _sandbox = new();

    private GitRepositoryService Service => _sandbox.Service;

    public void Dispose() => _sandbox.Dispose();

    [Fact]
    public async Task ListBranches_ReportsWhereEachBranchStands()
    {
        _sandbox.CreateOrigin();
        string clone = _sandbox.Clone("clone");
        _sandbox.Git(clone, "branch", "merged");
        // Pushed, then one more commit that is not.
        _sandbox.Git(clone, "switch", "--quiet", "-c", "pushed");
        _sandbox.Commit(clone, "pushed.txt", "one");
        _sandbox.Git(clone, "push", "--quiet", "-u", "origin", "pushed");
        _sandbox.Commit(clone, "pushed.txt", "two");
        // As when a pull request is squash-merged and its branch deleted.
        _sandbox.Git(clone, "switch", "--quiet", "-c", "squashed", "main");
        _sandbox.Commit(clone, "squashed.txt", "work");
        _sandbox.Git(clone, "push", "--quiet", "-u", "origin", "squashed");
        _sandbox.Git(_sandbox.Origin, "branch", "-D", "squashed");
        _sandbox.Git(clone, "switch", "--quiet", "main");
        await Service.CheckAsync(clone);
        string worktree = Path.Combine(_sandbox.Root, "wt-agent");
        _sandbox.Git(clone, "worktree", "add", "--quiet", "-b", "agent", worktree);

        IReadOnlyList<GitBranch> branches = await Service.ListBranchesAsync(clone);

        Assert.Equal("main", branches[0].Name);
        Assert.True(branches[0].IsDefault);
        Assert.Equal(clone, branches[0].WorktreePath);
        Assert.Equal("origin/main", branches[0].Upstream);

        GitBranch merged = Assert.Single(branches, b => b.Name == "merged");
        Assert.True(merged.IsMerged);
        Assert.Null(merged.Upstream);
        Assert.Null(merged.WorktreePath);
        Assert.NotNull(merged.LastCommitDate);
        Assert.Equal("Change README.md", merged.Subject);

        GitBranch pushed = Assert.Single(branches, b => b.Name == "pushed");
        Assert.Equal(2, pushed.UniqueCommits);
        Assert.Equal(1, pushed.AheadOfUpstream);
        Assert.Equal(0, pushed.BehindUpstream);
        Assert.False(pushed.UpstreamGone);

        GitBranch squashed = Assert.Single(branches, b => b.Name == "squashed");
        Assert.True(squashed.UpstreamGone);
        Assert.Equal(1, squashed.UniqueCommits);

        Assert.Equal(worktree, Assert.Single(branches, b => b.Name == "agent").WorktreePath);
    }

    [Fact]
    public async Task DeleteBranch_KeepsWhatIsInUseOrUnmergedUnlessForced()
    {
        _sandbox.CreateOrigin();
        string clone = _sandbox.Clone("clone");
        _sandbox.Git(clone, "branch", "merged");
        _sandbox.Git(clone, "switch", "--quiet", "-c", "work");
        _sandbox.Commit(clone, "work.txt", "work");
        _sandbox.Git(clone, "switch", "--quiet", "main");
        string worktree = Path.Combine(_sandbox.Root, "wt-agent");
        _sandbox.Git(clone, "worktree", "add", "--quiet", "-b", "agent", worktree);

        Assert.False((await Service.DeleteBranchAsync(clone, "main")).Success);
        Assert.False((await Service.DeleteBranchAsync(clone, "agent", force: true)).Success);
        Assert.True(_sandbox.RefExists(clone, "refs/heads/agent"));

        GitOperationResult merged = await Service.DeleteBranchAsync(clone, "merged");
        Assert.True(merged.Success, merged.Output);
        Assert.False(_sandbox.RefExists(clone, "refs/heads/merged"));

        Assert.False((await Service.DeleteBranchAsync(clone, "work")).Success);
        Assert.True(_sandbox.RefExists(clone, "refs/heads/work"));
        GitOperationResult forced = await Service.DeleteBranchAsync(clone, "work", force: true);
        Assert.True(forced.Success, forced.Output);
        Assert.False(_sandbox.RefExists(clone, "refs/heads/work"));
    }

    [Fact]
    public async Task DeleteBranches_DeletesWhatItCanAndReportsTheRest()
    {
        _sandbox.CreateOrigin();
        string clone = _sandbox.Clone("clone");
        _sandbox.Git(clone, "branch", "first");
        _sandbox.Git(clone, "branch", "second");

        GitOperationResult result = await Service.DeleteBranchesAsync(clone, ["first", "second", "main", "missing"]);

        Assert.False(result.Success);
        Assert.Contains("main", result.Output);
        Assert.Contains("missing", result.Output);
        Assert.False(_sandbox.RefExists(clone, "refs/heads/first"));
        Assert.False(_sandbox.RefExists(clone, "refs/heads/second"));
        Assert.True(_sandbox.RefExists(clone, "refs/heads/main"));
    }

    [Theory]
    [InlineData("", false, 0, 0)]
    [InlineData("[gone]", true, 0, 0)]
    [InlineData("[ahead 3]", false, 3, 0)]
    [InlineData("[behind 2]", false, 0, 2)]
    [InlineData("[ahead 1, behind 12]", false, 1, 12)]
    public void UpstreamTrackIsParsed(string track, bool gone, int ahead, int behind)
        => Assert.Equal((gone, ahead, behind), GitRepositoryService.ParseUpstreamTrack(track));
}
