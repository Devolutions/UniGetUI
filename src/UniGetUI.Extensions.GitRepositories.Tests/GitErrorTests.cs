namespace UniGetUI.Extensions.GitRepositories.Tests;

public sealed class GitErrorTests
{
    [Fact]
    public void Summary_PutsTheDiagnosticAheadOfHundredsOfPrunedBranches()
    {
        string progress = "From https://example.com/repo\n"
            + string.Concat(Enumerable.Repeat(" - [deleted] (none) -> origin/old-branch\n", 300));
        const string error = "error: cannot lock ref 'refs/remotes/origin/main': is at abc but expected def";

        Assert.Equal(error, GitError.Summarize(progress + error + "\nhint: try again\n"));
    }

    [Fact]
    public void Summary_BoundsLongLinesAndFallsBackToTheLastLine()
    {
        Assert.Equal("last line", GitError.Summarize("first line\r\nlast line\r\n"));
        Assert.Equal("", GitError.Summarize(""));
        Assert.Equal(new string('x', 497) + "...", GitError.Summarize(new string('x', 501)));
        Assert.Equal(new string('x', 500), GitError.Summarize(new string('x', 500)));
        Assert.Equal("remote: fatal: access denied", GitError.Summarize("progress\nremote: fatal: access denied\nhint: sign in"));
    }

    [Theory]
    [InlineData("fatal: Authentication failed")]
    [InlineData("fatal: Permission denied (publickey).")]
    [InlineData("fatal: repository not found")]
    [InlineData("error: cannot lock ref 'refs/remotes/origin/a/b': 'refs/remotes/origin/a' exists; cannot create ref")]
    [InlineData("error: cannot lock ref 'refs/remotes/origin/main': unable to resolve reference: reference broken")]
    [InlineData("fatal: bad object refs/remotes/origin/main")]
    public void PersistentFailures_AreNotClassifiedAsTransient(string error)
        => Assert.False(GitError.IsTransientFetchFailure(error));
}
