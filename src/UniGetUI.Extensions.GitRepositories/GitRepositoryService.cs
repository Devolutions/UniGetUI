using System.Globalization;
using UniGetUI.Core.Logging;

namespace UniGetUI.Extensions.GitRepositories;

/// <summary>
/// Keeps main clones current and their worktrees tidy. A main clone is only ever fast-forwarded to its
/// remote default branch, and only while it is checked out on that branch with no changed tracked files:
/// anything that would need a merge, a rebase or touching local work is reported and left alone.
/// </summary>
public sealed class GitRepositoryService(GitRunner? runner = null)
{
    public static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(5);

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private readonly GitRunner _git = runner ?? new GitRunner();

    /// <summary>The installed git's version line, or null when git cannot be run.</summary>
    public async Task<string?> GetGitVersionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            GitResult result = await _git.RunAsync(Path.GetTempPath(), ["--version"], null, cancellationToken);
            return result.Success ? result.StandardOutput.Trim() : null;
        }
        catch (GitException ex)
        {
            Logger.Warn(ex.Message);
            return null;
        }
    }

    /// <summary>Finds the working tree a folder belongs to, or null when it is not inside one.</summary>
    public async Task<GitRepositoryLocation?> LocateAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(path))
            return null;

        GitResult paths = await RunAsync(path, ["rev-parse", "--path-format=absolute",
            "--show-toplevel", "--git-dir", "--git-common-dir"], cancellationToken);
        string[] lines = SplitLines(paths.StandardOutput);
        if (!paths.Success || lines.Length < 3)
            return null;

        string root = NormalizePath(lines[0]);
        bool linked = !SamePath(lines[1], lines[2]);
        if (!linked)
            return new GitRepositoryLocation(root, root, false);

        // The main working tree is always listed first.
        IReadOnlyList<GitWorktree> worktrees = await ReadWorktreesAsync(root, cancellationToken);
        string mainClone = worktrees.Count > 0 ? worktrees[0].Path : root;
        return new GitRepositoryLocation(root, mainClone, true);
    }

    /// <summary>Reads a clone's state against what was last fetched, without contacting the remote.</summary>
    /// <param name="path">The main clone, or any folder in it or in one of its worktrees.</param>
    /// <param name="defaultBranch">The branch to keep current, instead of the remote's default one.</param>
    public async Task<GitRepositoryStatus> InspectAsync(string path, string? defaultBranch = null,
        CancellationToken cancellationToken = default)
    {
        GitRepositoryLocation? location = await LocateAsync(path, cancellationToken);
        if (location is null)
            return new GitRepositoryStatus(path, GitRepositoryState.Error, Error: "Not a git working tree.");
        string root = location.MainClonePath;

        GitResult head = await RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken);
        string? branch = head.Success ? head.StandardOutput.Trim() : null;
        (string? remote, defaultBranch) = await ResolveDefaultBranchAsync(root, branch, defaultBranch, cancellationToken);
        string? tracking = remote is not null && defaultBranch is not null ? $"{remote}/{defaultBranch}" : null;
        var status = new GitRepositoryStatus(root, GitRepositoryState.UpToDate, branch, defaultBranch, remote, tracking);

        if (branch is null)
            return status with { State = GitRepositoryState.DetachedHead };
        if (tracking is null)
            return status with { State = GitRepositoryState.NoUpstream };
        if (!string.Equals(branch, defaultBranch, StringComparison.Ordinal))
            return status with { State = GitRepositoryState.NotOnDefaultBranch };

        GitResult changes = await RunAsync(root,
            ["--no-optional-locks", "status", "--porcelain", "--untracked-files=no"], cancellationToken);
        if (!changes.Success)
            return status with { State = GitRepositoryState.Error, Error = changes.Output };
        bool dirty = changes.StandardOutput.Trim().Length > 0;

        GitResult counts = await RunAsync(root,
            ["rev-list", "--left-right", "--count", $"HEAD...refs/remotes/{tracking}"], cancellationToken);
        string[] fields = counts.StandardOutput.Split((char[])['\t', ' '],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!counts.Success || fields.Length != 2
            || !int.TryParse(fields[0], CultureInfo.InvariantCulture, out int ahead)
            || !int.TryParse(fields[1], CultureInfo.InvariantCulture, out int behind))
            return status with { State = GitRepositoryState.Error, Error = counts.Output, HasLocalChanges = dirty };

        GitRepositoryState state = (dirty, ahead, behind) switch
        {
            (true, _, _) => GitRepositoryState.HasLocalChanges,
            (_, > 0, > 0) => GitRepositoryState.Diverged,
            (_, > 0, _) => GitRepositoryState.Ahead,
            (_, _, > 0) => GitRepositoryState.Behind,
            _ => GitRepositoryState.UpToDate,
        };
        return status with { State = state, Ahead = ahead, Behind = behind, HasLocalChanges = dirty };
    }

    /// <summary>Fetches the clone's remote, pruning deleted branches, then reads its state.</summary>
    public async Task<GitRepositoryStatus> CheckAsync(string path, string? defaultBranch = null,
        CancellationToken cancellationToken = default)
    {
        GitRepositoryStatus status = await InspectAsync(path, defaultBranch, cancellationToken);
        if (status.Remote is null)
            return status;

        GitResult fetch;
        try
        {
            fetch = await _git.RunAsync(status.Path, ["fetch", "--prune", status.Remote], FetchTimeout, cancellationToken);
        }
        catch (GitException ex)
        {
            Logger.Warn($"Could not fetch {status.Path}: {ex.Message}");
            return status with { State = GitRepositoryState.Error, Error = ex.Message };
        }
        if (!fetch.Success)
        {
            Logger.Warn($"Could not fetch {status.Path}: {fetch.Output}");
            return status with { State = GitRepositoryState.Error, Error = fetch.Output };
        }
        return await InspectAsync(status.Path, defaultBranch, cancellationToken);
    }

    /// <summary>
    /// Fast-forwards a clone to its remote default branch, if that is all it takes. A clone that is on
    /// another branch, has changed tracked files, or has commits of its own is refused, never merged.
    /// </summary>
    /// <param name="fetch">Whether to fetch first; pass false right after <see cref="CheckAsync"/>.</param>
    public async Task<GitUpdateResult> UpdateAsync(string path, bool fetch = true, string? defaultBranch = null,
        CancellationToken cancellationToken = default)
    {
        GitRepositoryStatus status = fetch
            ? await CheckAsync(path, defaultBranch, cancellationToken)
            : await InspectAsync(path, defaultBranch, cancellationToken);
        switch (status.State)
        {
            case GitRepositoryState.UpToDate:
                return new GitUpdateResult(GitUpdateOutcome.AlreadyUpToDate, status, "");
            case GitRepositoryState.Error:
                return new GitUpdateResult(GitUpdateOutcome.Failed, status, status.Error ?? "");
            case not GitRepositoryState.Behind:
                return new GitUpdateResult(GitUpdateOutcome.Refused, status, "");
        }

        GitResult merge = await RunAsync(status.Path,
            ["merge", "--ff-only", "--no-edit", $"refs/remotes/{status.TrackingRef}"], cancellationToken);
        GitRepositoryStatus after = await InspectAsync(status.Path, defaultBranch, cancellationToken);
        if (!merge.Success)
        {
            Logger.Warn($"Could not fast-forward {status.Path} to {status.TrackingRef}: {merge.Output}");
            return new GitUpdateResult(GitUpdateOutcome.Failed, after, merge.Output);
        }
        Logger.Info($"Fast-forwarded {status.Path} by {status.Behind} commit(s) to {status.TrackingRef}");
        return new GitUpdateResult(GitUpdateOutcome.Updated, after, merge.Output);
    }

    /// <summary>Lists a clone's worktrees, the main one first, with what makes each safe to remove.</summary>
    /// <param name="checkLocalChanges">Run <c>git status</c> in every worktree, by far the slowest part on large
    /// working trees. When false, <see cref="GitWorktree.HasLocalChanges"/> is left false, for
    /// <see cref="HasLocalChangesAsync"/> to answer one worktree at a time.</param>
    public async Task<IReadOnlyList<GitWorktree>> ListWorktreesAsync(string path, string? defaultBranch = null,
        bool checkLocalChanges = true, CancellationToken cancellationToken = default)
    {
        GitRepositoryLocation location = await LocateAsync(path, cancellationToken)
            ?? throw new GitException($"{path} is not a git working tree.");
        string root = location.MainClonePath;
        IReadOnlyList<GitWorktree> worktrees = await ReadWorktreesAsync(root, cancellationToken);

        // The main worktree comes first, with the branch its HEAD is on.
        (string? remote, defaultBranch) = await ResolveDefaultBranchAsync(
            root, worktrees.FirstOrDefault()?.Branch, defaultBranch, cancellationToken);
        string? tracking = remote is not null && defaultBranch is not null
            ? $"refs/remotes/{remote}/{defaultBranch}"
            : null;

        string[] names = [.. worktrees.Select(worktree => worktree.Branch).OfType<string>().Distinct(StringComparer.Ordinal)];
        Dictionary<string, GitBranch> branches = names.Length == 0
            ? []
            : (await ReadBranchesAsync(root, tracking, defaultBranch, names, cancellationToken))
                .ToDictionary(branch => branch.Name, StringComparer.Ordinal);
        GitWorktree[] described = await Task.WhenAll(worktrees.Select(worktree =>
            DescribeWorktreeAsync(root, worktree, tracking, branches, cancellationToken)));
        if (!checkLocalChanges)
            return described;
        return await Task.WhenAll(described.Select(async worktree =>
            worktree with { HasLocalChanges = await HasLocalChangesAsync(worktree.Path, cancellationToken) }));
    }

    /// <summary>Whether removing a worktree would lose files: changed or untracked ones. A folder that is
    /// already gone has none, and one git cannot read is assumed to have some.</summary>
    public async Task<bool> HasLocalChangesAsync(string worktreePath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(worktreePath))
            return false;
        // Untracked files count too: removing the worktree would delete them.
        GitResult changes = await RunAsync(worktreePath,
            ["--no-optional-locks", "status", "--porcelain"], cancellationToken);
        return !changes.Success || changes.StandardOutput.Trim().Length > 0;
    }

    /// <summary>Lists a clone's local branches, the default one first and then the most recently committed.</summary>
    public async Task<IReadOnlyList<GitBranch>> ListBranchesAsync(string path, string? defaultBranch = null,
        CancellationToken cancellationToken = default)
    {
        GitRepositoryLocation location = await LocateAsync(path, cancellationToken)
            ?? throw new GitException($"{path} is not a git working tree.");
        string root = location.MainClonePath;
        GitResult head = await RunAsync(root, ["symbolic-ref", "--quiet", "--short", "HEAD"], cancellationToken);
        (string? remote, defaultBranch) = await ResolveDefaultBranchAsync(
            root, head.Success ? head.StandardOutput.Trim() : null, defaultBranch, cancellationToken);
        string? tracking = remote is not null && defaultBranch is not null
            ? $"refs/remotes/{remote}/{defaultBranch}"
            : null;

        IReadOnlyList<GitBranch> branches = await ReadBranchesAsync(root, tracking, defaultBranch, null, cancellationToken);
        return [.. branches
            .OrderByDescending(branch => branch.IsDefault)
            .ThenByDescending(branch => branch.LastCommitDate ?? DateTimeOffset.MinValue)
            .ThenBy(branch => branch.Name, StringComparer.Ordinal)];
    }

    /// <summary>
    /// Deletes a local branch. One that is checked out in a worktree, or is the default branch, never is;
    /// one with commits found nowhere else only when <paramref name="force"/> is set.
    /// </summary>
    public Task<GitOperationResult> DeleteBranchAsync(string repositoryPath, string branch, bool force = false,
        string? defaultBranch = null, CancellationToken cancellationToken = default)
        => DeleteBranchesAsync(repositoryPath, [branch], force, defaultBranch, cancellationToken);

    /// <summary>Deletes several local branches, on the same terms as <see cref="DeleteBranchAsync"/>; it only
    /// succeeds when every one of them is deleted.</summary>
    public async Task<GitOperationResult> DeleteBranchesAsync(string repositoryPath, IReadOnlyCollection<string> branches,
        bool force = false, string? defaultBranch = null, CancellationToken cancellationToken = default)
    {
        GitRepositoryLocation location = await LocateAsync(repositoryPath, cancellationToken)
            ?? throw new GitException($"{repositoryPath} is not a git working tree.");
        string root = location.MainClonePath;
        Dictionary<string, GitBranch> existing = (await ListBranchesAsync(root, defaultBranch, cancellationToken))
            .ToDictionary(branch => branch.Name, StringComparer.Ordinal);

        var messages = new List<string>();
        var forced = new List<string>();
        var checkedDeletions = new List<string>();
        foreach (string name in branches.Distinct(StringComparer.Ordinal))
        {
            if (!existing.TryGetValue(name, out GitBranch? branch))
                messages.Add($"{root} has no branch named {name}.");
            else if (branch.IsDefault)
                messages.Add($"{name} is the default branch and cannot be deleted.");
            else if (branch.WorktreePath is not null)
                messages.Add($"{name} is checked out in {branch.WorktreePath}. Remove that worktree first.");
            // git's own -d checks a branch against its upstream, so a merged branch whose upstream was
            // deleted would be refused: one that is entirely on the default branch goes with -D.
            else if (force || branch.IsMerged)
                forced.Add(name);
            else
                checkedDeletions.Add(name);
        }
        bool success = messages.Count == 0;

        foreach ((string flag, List<string> names) in new[] { ("-D", forced), ("-d", checkedDeletions) })
        {
            if (names.Count == 0) continue;
            GitResult deletion = await RunAsync(root, ["branch", flag, "--", .. names], cancellationToken);
            if (deletion.Output.Length > 0) messages.Add(deletion.Output);
            if (deletion.Success)
            {
                Logger.Info($"Deleted branches {string.Join(", ", names)} of {root}");
            }
            else
            {
                success = false;
                Logger.Warn($"Could not delete every branch of {string.Join(", ", names)} in {root}: {deletion.Output}");
            }
        }
        return new GitOperationResult(success, string.Join(Environment.NewLine, messages));
    }

    /// <summary>Counts a clone's linked worktrees, without describing each one.</summary>
    public async Task<int> CountLinkedWorktreesAsync(string path, CancellationToken cancellationToken = default)
    {
        GitRepositoryLocation location = await LocateAsync(path, cancellationToken)
            ?? throw new GitException($"{path} is not a git working tree.");
        IReadOnlyList<GitWorktree> worktrees = await ReadWorktreesAsync(location.MainClonePath, cancellationToken);
        return worktrees.Count(worktree => !worktree.IsMain);
    }

    /// <summary>
    /// Removes a linked worktree. One with changed or untracked files is only removed when
    /// <paramref name="force"/> is set; the main worktree and locked ones never are.
    /// </summary>
    /// <param name="deleteBranch">Also delete its branch: forcibly when everything on it is on the remote
    /// default branch or <paramref name="force"/> is set, otherwise only if git deems it merged.</param>
    public async Task<GitOperationResult> RemoveWorktreeAsync(string repositoryPath, string worktreePath,
        bool force = false, bool deleteBranch = false, string? defaultBranch = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<GitWorktree> worktrees = await ListWorktreesAsync(
            repositoryPath, defaultBranch, checkLocalChanges: false, cancellationToken);
        GitWorktree? worktree = worktrees.FirstOrDefault(candidate => SamePath(candidate.Path, worktreePath));
        if (worktree is null)
            return new GitOperationResult(false, $"{worktreePath} is not a worktree of {repositoryPath}.");
        if (worktree.IsMain)
            return new GitOperationResult(false, $"{worktree.Path} is the main worktree and cannot be removed.");
        if (worktree.IsLocked)
            return new GitOperationResult(false, string.IsNullOrEmpty(worktree.LockReason)
                ? $"{worktree.Path} is locked."
                : $"{worktree.Path} is locked: {worktree.LockReason}");

        string root = worktrees[0].Path;
        // A worktree whose folder is already gone only has its administrative entry left to clear.
        string[] command = worktree.IsPrunable || !Directory.Exists(worktree.Path)
            ? ["worktree", "prune"]
            : force
                ? ["worktree", "remove", "--force", worktree.Path]
                : ["worktree", "remove", worktree.Path];
        GitResult removal = await RunAsync(root, command, cancellationToken);
        if (!removal.Success)
        {
            Logger.Warn($"Could not remove worktree {worktree.Path}: {removal.Output}");
            return new GitOperationResult(false, removal.Output);
        }
        Logger.Info($"Removed worktree {worktree.Path} of {root}");

        if (!deleteBranch || worktree.Branch is null)
            return new GitOperationResult(true, removal.Output);

        GitResult branch = await RunAsync(root,
            ["branch", worktree.IsMerged || force ? "-D" : "-d", worktree.Branch], cancellationToken);
        string output = string.Join(Environment.NewLine,
            new[] { removal.Output, branch.Output }.Where(text => text.Length > 0));
        if (!branch.Success)
        {
            Logger.Warn($"Removed worktree {worktree.Path}, but kept branch {worktree.Branch}: {branch.Output}");
            return new GitOperationResult(false, output);
        }
        Logger.Info($"Deleted branch {worktree.Branch} of {root}");
        return new GitOperationResult(true, output);
    }

    /// <summary>Clears the entries of worktrees whose folders were deleted without git.</summary>
    public async Task<GitOperationResult> PruneWorktreesAsync(string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        GitRepositoryLocation location = await LocateAsync(repositoryPath, cancellationToken)
            ?? throw new GitException($"{repositoryPath} is not a git working tree.");
        GitResult prune = await RunAsync(location.MainClonePath, ["worktree", "prune", "--verbose"], cancellationToken);
        if (prune.Success)
            Logger.Info($"Pruned worktrees of {location.MainClonePath}");
        else
            Logger.Warn($"Could not prune worktrees of {location.MainClonePath}: {prune.Output}");
        return new GitOperationResult(prune.Success, prune.Output);
    }

    /// <summary>
    /// Picks the remote the current branch tracks (else origin, else the only remote) and its default
    /// branch: <paramref name="preferredBranch"/> when given, else the one its HEAD points to, else main,
    /// else master.
    /// </summary>
    private async Task<(string? Remote, string? DefaultBranch)> ResolveDefaultBranchAsync(string root,
        string? currentBranch, string? preferredBranch, CancellationToken cancellationToken)
    {
        string[] remotes = SplitLines((await RunAsync(root, ["remote"], cancellationToken)).StandardOutput);
        string? remote = null;
        if (currentBranch is not null)
        {
            GitResult configured = await RunAsync(root,
                ["config", "--get", $"branch.{currentBranch}.remote"], cancellationToken);
            string name = configured.StandardOutput.Trim();
            if (configured.Success && remotes.Contains(name, StringComparer.Ordinal))
                remote = name;
        }
        remote ??= remotes.Contains("origin", StringComparer.Ordinal) ? "origin"
            : remotes.Length == 1 ? remotes[0]
            : null;
        if (remote is null)
            return (null, null);
        if (!string.IsNullOrWhiteSpace(preferredBranch))
            return (remote, preferredBranch.Trim());

        GitResult remoteHead = await RunAsync(root,
            ["symbolic-ref", "--quiet", "--short", $"refs/remotes/{remote}/HEAD"], cancellationToken);
        string prefix = remote + "/";
        string target = remoteHead.StandardOutput.Trim();
        if (remoteHead.Success && target.StartsWith(prefix, StringComparison.Ordinal) && target.Length > prefix.Length)
            return (remote, target[prefix.Length..]);

        foreach (string candidate in (string[])["main", "master"])
        {
            GitResult exists = await RunAsync(root,
                ["rev-parse", "--verify", "--quiet", $"refs/remotes/{remote}/{candidate}"], cancellationToken);
            if (exists.Success)
                return (remote, candidate);
        }
        return (remote, null);
    }

    private async Task<IReadOnlyList<GitWorktree>> ReadWorktreesAsync(string root, CancellationToken cancellationToken)
    {
        GitResult list = await RunAsync(root, ["worktree", "list", "--porcelain", "-z"], cancellationToken);
        if (!list.Success)
            throw new GitException($"Could not list the worktrees of {root}: {list.Output}");
        return ParseWorktreeList(list.StandardOutput);
    }

    /// <summary>
    /// Reads local branches in one git call: their upstream and how far they are from it, their last commit,
    /// the worktree they are checked out in, and how many of their commits <paramref name="tracking"/> lacks.
    /// Asking branch by branch starts several processes each, which takes seconds on a clone with dozens of
    /// agent worktrees.
    /// </summary>
    /// <param name="names">The branches to read; every local branch when null.</param>
    private async Task<IReadOnlyList<GitBranch>> ReadBranchesAsync(string root, string? tracking, string? defaultBranch,
        IReadOnlyCollection<string>? names, CancellationToken cancellationToken)
    {
        string[] patterns = names is null ? ["refs/heads"] : [.. names.Select(branch => "refs/heads/" + branch)];
        // The subject goes last: it is the only field that can itself hold a tab.
        static string Format(string uniqueCommits) => "--format=%(refname)\t%(upstream:short)\t%(upstream:track)"
            + $"\t%(committerdate:iso-strict)\t%(worktreepath)\t{uniqueCommits}\t%(contents:subject)";
        // %(ahead-behind) needs git 2.41; older versions are asked once more without it.
        GitResult? refs = tracking is null
            ? null
            : await RunAsync(root, ["for-each-ref", Format($"%(ahead-behind:{tracking})"), .. patterns], cancellationToken);
        bool counted = refs?.Success == true;
        if (!counted)
            refs = await RunAsync(root, ["for-each-ref", Format(""), .. patterns], cancellationToken);
        if (!refs!.Success)
            throw new GitException($"Could not list the branches of {root}: {refs.Output}");

        var wanted = names?.ToHashSet(StringComparer.Ordinal);
        var branches = new List<GitBranch>();
        foreach (string[] fields in SplitLines(refs.StandardOutput).Select(line => line.Split('\t')))
        {
            if (fields.Length < 7 || !fields[0].StartsWith("refs/heads/", StringComparison.Ordinal))
                continue;
            string name = fields[0]["refs/heads/".Length..];
            if (wanted is not null && !wanted.Contains(name))
                continue;

            (bool gone, int ahead, int behind) = ParseUpstreamTrack(fields[2]);
            DateTimeOffset? date = DateTimeOffset.TryParse(fields[3], CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTimeOffset committed) ? committed : null;
            int? unique = counted
                && int.TryParse(fields[5].Split(' ')[0], CultureInfo.InvariantCulture, out int count) ? count : null;
            branches.Add(new GitBranch(
                name,
                Upstream: fields[1].Length > 0 ? fields[1] : null,
                UpstreamGone: gone,
                AheadOfUpstream: ahead,
                BehindUpstream: behind,
                UniqueCommits: unique,
                LastCommitDate: date,
                Subject: string.Join('\t', fields[6..]),
                WorktreePath: fields[4].Length > 0 ? NormalizePath(fields[4]) : null,
                IsDefault: name == defaultBranch));
        }
        return branches;
    }

    /// <summary>Reads <c>%(upstream:track)</c>: "[gone]", "[ahead 1, behind 2]", or nothing when in sync.</summary>
    internal static (bool Gone, int Ahead, int Behind) ParseUpstreamTrack(string track)
    {
        if (track == "[gone]")
            return (true, 0, 0);
        int ahead = 0, behind = 0;
        foreach (string part in track.Trim('[', ']').Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            string[] words = part.Split(' ');
            if (words.Length != 2 || !int.TryParse(words[1], CultureInfo.InvariantCulture, out int count))
                continue;
            if (words[0] == "ahead") ahead = count;
            else if (words[0] == "behind") behind = count;
        }
        return (false, ahead, behind);
    }

    private async Task<GitWorktree> DescribeWorktreeAsync(string root, GitWorktree worktree, string? tracking,
        IReadOnlyDictionary<string, GitBranch> branches, CancellationToken cancellationToken)
    {
        if (worktree.Branch is not null && branches.TryGetValue(worktree.Branch, out GitBranch? branch))
        {
            worktree = worktree with
            {
                UpstreamGone = branch.UpstreamGone,
                LastCommitDate = branch.LastCommitDate,
                UniqueCommits = branch.UniqueCommits,
            };
            if (branch.UniqueCommits is not null || tracking is null)
                return worktree;
        }
        if (worktree.Head is null)
            return worktree;

        // A detached worktree, or any worktree with git older than 2.41, is asked about on its own.
        Task<GitResult> dateLookup = RunAsync(root, ["log", "-1", "--format=%cI", worktree.Head], cancellationToken);
        Task<GitResult>? uniqueLookup = tracking is null
            ? null
            : RunAsync(root, ["rev-list", "--count", $"{tracking}..{worktree.Head}"], cancellationToken);

        GitResult date = await dateLookup;
        if (date.Success && DateTimeOffset.TryParse(date.StandardOutput.Trim(), CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateTimeOffset committed))
            worktree = worktree with { LastCommitDate = committed };
        if (uniqueLookup is not null && await uniqueLookup is { Success: true } unique
            && int.TryParse(unique.StandardOutput.Trim(), CultureInfo.InvariantCulture, out int count))
            worktree = worktree with { UniqueCommits = count };
        return worktree;
    }

    /// <summary>Parses <c>git worktree list --porcelain -z</c>: NUL-terminated fields, a blank field between entries.</summary>
    internal static IReadOnlyList<GitWorktree> ParseWorktreeList(string output)
    {
        var worktrees = new List<GitWorktree>();
        GitWorktree? current = null;
        foreach (string field in output.Split('\0'))
        {
            if (field.Length == 0)
            {
                if (current is not null) worktrees.Add(current);
                current = null;
                continue;
            }

            int space = field.IndexOf(' ');
            string key = space < 0 ? field : field[..space];
            string value = space < 0 ? "" : field[(space + 1)..];
            if (key == "worktree")
            {
                if (current is not null) worktrees.Add(current);
                current = new GitWorktree(NormalizePath(value), null, null, worktrees.Count == 0, false, null, false);
                continue;
            }
            if (current is null)
                continue;

            current = key switch
            {
                "HEAD" => current with { Head = value },
                "branch" => current with
                {
                    Branch = value.StartsWith("refs/heads/", StringComparison.Ordinal) ? value["refs/heads/".Length..] : value,
                },
                "locked" => current with { IsLocked = true, LockReason = value.Length > 0 ? value : null },
                "prunable" => current with { IsPrunable = true },
                _ => current,
            };
        }
        if (current is not null) worktrees.Add(current);
        return worktrees;
    }

    private Task<GitResult> RunAsync(string workingDirectory, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
        => _git.RunAsync(workingDirectory, arguments, null, cancellationToken);

    private static string[] SplitLines(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>A full path without a trailing separator, as paths are compared here.</summary>
    public static string NormalizePath(string path)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>Whether two paths name the same folder, ignoring case where the file system does.</summary>
    public static bool SamePath(string a, string b)
        => string.Equals(NormalizePath(a), NormalizePath(b), PathComparison);
}
