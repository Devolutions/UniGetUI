namespace UniGetUI.Extensions.GitRepositories;

/// <summary>Where a folder sits in git: the root of its working tree, and the main clone it belongs to.</summary>
/// <param name="Root">The top of the working tree that contains the folder.</param>
/// <param name="MainClonePath">The main working tree; equals <paramref name="Root"/> unless it is a linked worktree.</param>
/// <param name="IsLinkedWorktree">Whether the folder belongs to a worktree made by <c>git worktree add</c>.</param>
public sealed record GitRepositoryLocation(string Root, string MainClonePath, bool IsLinkedWorktree);

/// <summary>What a main clone needs before it can be updated, most blocking first.</summary>
public enum GitRepositoryState
{
    /// <summary>On its default branch, clean, and level with the remote.</summary>
    UpToDate,

    /// <summary>On its default branch, clean, and only missing remote commits: an update fast-forwards it.</summary>
    Behind,

    /// <summary>Has commits the remote does not; never updated, since that would need a merge.</summary>
    Ahead,

    /// <summary>Has commits the remote does not, and misses some it has; never updated.</summary>
    Diverged,

    /// <summary>Tracked files have been changed; never updated, the clone is meant to stay clean.</summary>
    HasLocalChanges,

    /// <summary>Checked out on another branch than its default one.</summary>
    NotOnDefaultBranch,

    /// <summary>No branch is checked out.</summary>
    DetachedHead,

    /// <summary>There is no remote branch to compare with.</summary>
    NoUpstream,

    /// <summary>Git failed; <see cref="GitRepositoryStatus.Error"/> says why.</summary>
    Error,
}

/// <param name="Path">The main clone's root.</param>
/// <param name="CurrentBranch">The checked-out branch, or null on a detached HEAD.</param>
/// <param name="DefaultBranch">The remote's default branch: its HEAD, else main, else master.</param>
/// <param name="Remote">The remote that branch comes from.</param>
/// <param name="TrackingRef">The remote branch compared against and fast-forwarded to, such as origin/main.</param>
public sealed record GitRepositoryStatus(
    string Path,
    GitRepositoryState State,
    string? CurrentBranch = null,
    string? DefaultBranch = null,
    string? Remote = null,
    string? TrackingRef = null,
    int Ahead = 0,
    int Behind = 0,
    bool HasLocalChanges = false,
    string? Error = null)
{
    public bool CanUpdate => State == GitRepositoryState.Behind;
}

public enum GitUpdateOutcome
{
    /// <summary>The clone was fast-forwarded.</summary>
    Updated,

    /// <summary>There was nothing to update.</summary>
    AlreadyUpToDate,

    /// <summary>The clone was left alone because updating it was not safe; the status says why.</summary>
    Refused,

    /// <summary>Git failed; the output says why.</summary>
    Failed,
}

public sealed record GitUpdateResult(GitUpdateOutcome Outcome, GitRepositoryStatus Status, string Output);

/// <summary>A working tree of a repository, as <c>git worktree list</c> reports it, with what makes it safe to remove.</summary>
/// <param name="Path">The worktree's folder.</param>
/// <param name="Head">The commit it has checked out, or null for a bare entry.</param>
/// <param name="Branch">The checked-out branch's short name, or null when detached.</param>
/// <param name="IsMain">Whether it is the main working tree, which cannot be removed.</param>
/// <param name="IsLocked">Whether it was locked with <c>git worktree lock</c>.</param>
/// <param name="IsPrunable">Whether its folder is gone and only its administrative entry is left.</param>
/// <param name="HasLocalChanges">Whether it has changed or untracked files, which a removal would delete.</param>
/// <param name="UniqueCommits">Commits on it that are not on the remote default branch; null when unknown.</param>
/// <param name="UpstreamGone">Whether its branch tracked a remote branch that has since been deleted, as after a merged pull request.</param>
/// <param name="LastCommitDate">When its checked-out commit was made.</param>
public sealed record GitWorktree(
    string Path,
    string? Head,
    string? Branch,
    bool IsMain,
    bool IsLocked,
    string? LockReason,
    bool IsPrunable,
    bool HasLocalChanges = false,
    int? UniqueCommits = null,
    bool UpstreamGone = false,
    DateTimeOffset? LastCommitDate = null)
{
    /// <summary>Everything committed on it is already on the remote default branch.</summary>
    public bool IsMerged => UniqueCommits == 0;
}

/// <summary>A local branch of a clone.</summary>
/// <param name="Name">Its short name, such as <c>feature/login</c>.</param>
/// <param name="Upstream">The remote branch it tracks, such as <c>origin/feature/login</c>; null when none.</param>
/// <param name="UpstreamGone">Whether the remote branch it tracked has since been deleted, as after a merged pull request.</param>
/// <param name="AheadOfUpstream">Commits not pushed to <paramref name="Upstream"/>.</param>
/// <param name="BehindUpstream">Commits on <paramref name="Upstream"/> not pulled into it.</param>
/// <param name="UniqueCommits">Commits on it that are not on the remote default branch; null when unknown.</param>
/// <param name="WorktreePath">The worktree it is checked out in, the main clone included; null when none.</param>
/// <param name="IsDefault">Whether it is the branch the clone is kept current on.</param>
public sealed record GitBranch(
    string Name,
    string? Upstream = null,
    bool UpstreamGone = false,
    int AheadOfUpstream = 0,
    int BehindUpstream = 0,
    int? UniqueCommits = null,
    DateTimeOffset? LastCommitDate = null,
    string Subject = "",
    string? WorktreePath = null,
    bool IsDefault = false)
{
    /// <summary>Everything committed on it is already on the remote default branch.</summary>
    public bool IsMerged => UniqueCommits == 0;
}

public sealed record GitOperationResult(bool Success, string Output);
