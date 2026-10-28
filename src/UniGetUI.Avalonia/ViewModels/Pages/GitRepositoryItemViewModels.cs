using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using UniGetUI.Core.Tools;
using UniGetUI.Extensions.GitRepositories;

namespace UniGetUI.Avalonia.ViewModels.Pages;

/// <summary>How a clone's state is shown: the colour of its badge.</summary>
public enum GitBadgeKind { Unknown, Ok, Update, Warning, Error }

/// <summary>Turns engine states into the words and badge colours the page shows.</summary>
internal static class GitStatusText
{
    /// <summary>"3 commits not on the default branch"; empty when there are none, or the count is unknown.</summary>
    public static string UniqueCommits(int? count) => count switch
    {
        null or 0 => "",
        1 => CoreTools.Translate("1 commit not on the default branch"),
        _ => CoreTools.Translate("{0} commits not on the default branch", count),
    };

    public static string LastCommit(DateTimeOffset? date) => date is { } committed
        ? CoreTools.Translate("Last commit {0}", committed.LocalDateTime.ToString("g", CultureInfo.CurrentCulture))
        : "";

    public static string Describe(GitRepositoryStatus? status) => status?.State switch
    {
        null => CoreTools.Translate("Not checked yet"),
        GitRepositoryState.UpToDate => CoreTools.Translate("Up to date"),
        GitRepositoryState.Behind => status.Behind == 1
            ? CoreTools.Translate("1 commit behind")
            : CoreTools.Translate("{0} commits behind", status.Behind),
        GitRepositoryState.Ahead => status.Ahead == 1
            ? CoreTools.Translate("1 local commit not on the remote")
            : CoreTools.Translate("{0} local commits not on the remote", status.Ahead),
        GitRepositoryState.Diverged => CoreTools.Translate("Diverged from {0}", status.TrackingRef ?? ""),
        GitRepositoryState.HasLocalChanges => CoreTools.Translate("Has local changes"),
        GitRepositoryState.NotOnDefaultBranch => CoreTools.Translate("On {0} instead of {1}",
            status.CurrentBranch ?? "", status.DefaultBranch ?? ""),
        GitRepositoryState.DetachedHead => CoreTools.Translate("No branch checked out"),
        GitRepositoryState.NoUpstream => CoreTools.Translate("No remote branch to follow"),
        _ => CoreTools.Translate("Could not be checked"),
    };

    public static GitBadgeKind Kind(GitRepositoryStatus? status) => status?.State switch
    {
        null => GitBadgeKind.Unknown,
        GitRepositoryState.UpToDate => GitBadgeKind.Ok,
        GitRepositoryState.Behind => GitBadgeKind.Update,
        GitRepositoryState.Error => GitBadgeKind.Error,
        _ => GitBadgeKind.Warning,
    };

    /// <summary>Why a clone is left alone, for the tooltip of its badge.</summary>
    public static string Explain(GitRepositoryStatus? status) => status?.State switch
    {
        null => CoreTools.Translate("Check this repository to compare it with its remote."),
        GitRepositoryState.UpToDate => CoreTools.Translate("Level with {0}.", status.TrackingRef ?? ""),
        GitRepositoryState.Behind => CoreTools.Translate("Updating fast-forwards it to {0}.", status.TrackingRef ?? ""),
        GitRepositoryState.Ahead or GitRepositoryState.Diverged => CoreTools.Translate(
            "It has commits that are not on {0}, so it is not updated: that would need a merge.", status.TrackingRef ?? ""),
        GitRepositoryState.HasLocalChanges => CoreTools.Translate(
            "Tracked files have been changed, so it is not updated. Commit, stash or discard them first."),
        GitRepositoryState.NotOnDefaultBranch => CoreTools.Translate(
            "Only a clone checked out on {0} is updated.", status.DefaultBranch ?? ""),
        GitRepositoryState.DetachedHead => CoreTools.Translate(
            "Only a clone checked out on {0} is updated.", status.DefaultBranch ?? ""),
        GitRepositoryState.NoUpstream => CoreTools.Translate(
            "It has no remote, or its remote has no main or master branch. You can choose the branch to follow."),
        _ => status.Error ?? "",
    };
}

public partial class GitRepositoryItemViewModel : ViewModelBase
{
    public GitRepositoryItemViewModel(string path, string? branchOverride)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(GitRepositoryService.NormalizePath(path));
        if (Name.Length == 0) Name = path;
        _branchOverride = branchOverride;
    }

    public string Path { get; }
    public string Name { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateText), nameof(StateDetail), nameof(BranchText), nameof(CanUpdate),
        nameof(IsOk), nameof(IsUpdate), nameof(IsWarning), nameof(IsError), nameof(IsUnknown))]
    private GitRepositoryStatus? _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpdate), nameof(IsIdle))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LastCheckedText), nameof(DetailText))]
    private DateTime? _lastChecked;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BranchText))]
    private string? _branchOverride;

    /// <summary>Its linked worktrees, once listed.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorktreeCountText), nameof(DetailText))]
    private int? _worktreeCount;

    public string StateText => GitStatusText.Describe(Status);
    public string StateDetail => GitStatusText.Explain(Status);
    public bool IsOk => GitStatusText.Kind(Status) == GitBadgeKind.Ok;
    public bool IsUpdate => GitStatusText.Kind(Status) == GitBadgeKind.Update;
    public bool IsWarning => GitStatusText.Kind(Status) == GitBadgeKind.Warning;
    public bool IsError => GitStatusText.Kind(Status) == GitBadgeKind.Error;
    public bool IsUnknown => GitStatusText.Kind(Status) == GitBadgeKind.Unknown;
    public bool CanUpdate => Status?.CanUpdate == true && !IsBusy;
    public bool IsIdle => !IsBusy;

    public string BranchText
    {
        get
        {
            string current = Status is null ? "" : Status.CurrentBranch ?? CoreTools.Translate("detached");
            string? target = BranchOverride ?? Status?.DefaultBranch;
            if (current.Length == 0) return target ?? "";
            return target is null || target == current ? current : $"{current} → {target}";
        }
    }

    public string LastCheckedText => LastChecked is { } checkedAt
        ? CoreTools.Translate("Checked at {0}", checkedAt.ToString("t", CultureInfo.CurrentCulture))
        : "";

    public string WorktreeCountText => WorktreeCount switch
    {
        null or 0 => "",
        1 => CoreTools.Translate("1 worktree"),
        int count => CoreTools.Translate("{0} worktrees", count),
    };

    /// <summary>The line under the branch: its worktrees and when it was last checked, whichever are known.</summary>
    public string DetailText => string.Join(" · ", new[] { WorktreeCountText, LastCheckedText }.Where(text => text.Length > 0));
}

public sealed partial class GitWorktreeItemViewModel : ObservableObject
{
    /// <param name="changesChecked">Whether <see cref="GitWorktree.HasLocalChanges"/> was read; when not,
    /// <see cref="LocalChanges"/> stays unknown until it is set.</param>
    public GitWorktreeItemViewModel(GitWorktree worktree, bool changesChecked = true)
    {
        Worktree = worktree;
        _localChanges = changesChecked ? worktree.HasLocalChanges : null;
        Name = System.IO.Path.GetFileName(worktree.Path) is { Length: > 0 } name ? name : worktree.Path;
        BranchText = worktree.Branch
            ?? (worktree.Head is { Length: >= 7 } head
                ? CoreTools.Translate("detached at {0}", head[..7])
                : CoreTools.Translate("detached"));
        LastCommitText = GitStatusText.LastCommit(worktree.LastCommitDate);
        UniqueCommitsText = GitStatusText.UniqueCommits(worktree.UniqueCommits);
        LockText = worktree.IsLocked
            ? string.IsNullOrEmpty(worktree.LockReason)
                ? CoreTools.Translate("Locked")
                : CoreTools.Translate("Locked: {0}", worktree.LockReason)
            : "";
    }

    public GitWorktree Worktree { get; }
    public string Path => Worktree.Path;
    public string Name { get; }
    public string BranchText { get; }
    public string LastCommitText { get; }
    public string UniqueCommitsText { get; }
    public string LockText { get; }

    public bool IsMain => Worktree.IsMain;
    public bool IsLocked => Worktree.IsLocked;
    public bool IsPrunable => Worktree.IsPrunable;
    /// <summary>Whether it has changed or untracked files; null while git status is still running.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLocalChanges), nameof(IsCheckingChanges), nameof(IsSafeToRemove))]
    private bool? _localChanges;

    public bool HasLocalChanges => LocalChanges == true;
    public bool IsCheckingChanges => LocalChanges is null;
    public bool IsMerged => Worktree.IsMerged && !Worktree.IsMain;
    public bool UpstreamGone => Worktree.UpstreamGone;
    public bool HasUniqueCommits => UniqueCommitsText.Length > 0 && !Worktree.IsMain;
    public bool CanRemove => !Worktree.IsMain && !Worktree.IsLocked;
    public bool CanOpen => !Worktree.IsPrunable;

    /// <summary>Nothing would be lost by removing it: no unsaved files, and everything committed is on the remote.</summary>
    public bool IsSafeToRemove => CanRemove && LocalChanges == false && (Worktree.IsMerged || Worktree.IsPrunable);
}

public sealed class GitBranchItemViewModel
{
    public GitBranchItemViewModel(GitBranch branch)
    {
        Branch = branch;
        LastCommitText = GitStatusText.LastCommit(branch.LastCommitDate);
        UniqueCommitsText = branch.IsDefault ? "" : GitStatusText.UniqueCommits(branch.UniqueCommits);
        CheckedOutText = branch.WorktreePath is { } path
            ? CoreTools.Translate("Checked out in {0}", System.IO.Path.GetFileName(path) is { Length: > 0 } folder ? folder : path)
            : "";
        UpstreamText = branch.UpstreamGone || branch.Upstream is null
            ? ""
            : (branch.AheadOfUpstream, branch.BehindUpstream) switch
            {
                (0, 0) => CoreTools.Translate("In sync with {0}", branch.Upstream),
                (int ahead, 0) => CoreTools.Translate("{0} to push to {1}", ahead, branch.Upstream),
                (0, int behind) => CoreTools.Translate("{0} to pull from {1}", behind, branch.Upstream),
                (int ahead, int behind) => CoreTools.Translate("{0} to push and {1} to pull from {2}", ahead, behind, branch.Upstream),
            };
    }

    public GitBranch Branch { get; }
    public string Name => Branch.Name;
    public string Subject => Branch.Subject;
    public string? WorktreePath => Branch.WorktreePath;
    public string LastCommitText { get; }
    public string UniqueCommitsText { get; }
    public string CheckedOutText { get; }
    public string UpstreamText { get; }

    public bool IsDefault => Branch.IsDefault;
    public bool IsCheckedOut => Branch.WorktreePath is not null;
    public bool IsMerged => Branch.IsMerged && !Branch.IsDefault;
    public bool UpstreamGone => Branch.UpstreamGone;
    public bool IsLocalOnly => Branch.Upstream is null;
    public bool HasUniqueCommits => UniqueCommitsText.Length > 0;
    public bool HasUpstreamText => UpstreamText.Length > 0;
    public bool IsOutOfSync => Branch.AheadOfUpstream > 0 || Branch.BehindUpstream > 0;

    /// <summary>git refuses to delete the branch a worktree has checked out, and the default one is kept.</summary>
    public bool CanDelete => !Branch.IsDefault && Branch.WorktreePath is null;

    /// <summary>Nothing would be lost by deleting it: everything committed on it is on the default branch.</summary>
    public bool IsSafeToDelete => CanDelete && Branch.IsMerged;
}
