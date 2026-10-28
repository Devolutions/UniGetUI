using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Views;
using UniGetUI.Avalonia.Views.DialogPages;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;
using UniGetUI.Extensions.GitRepositories;

namespace UniGetUI.Avalonia.ViewModels.Pages;

/// <summary>
/// The Git Repositories page: main clones kept on their default branch, checked against their remote,
/// fast-forwarded when that is all it takes, and their worktrees listed for cleanup. One instance lives
/// for the whole session, so automatic checks and the sidebar badge keep running while the page is closed.
/// </summary>
public partial class GitRepositoriesViewModel : ViewModelBase
{
    private const int MaxParallelChecks = 4;

    private readonly GitRepositoryService _git;
    private readonly DispatcherTimer _checkTimer = new();
    private CancellationTokenSource? _detailsLoad;
    private bool _initialized;

    public GitRepositoriesViewModel() : this(new GitRepositoryService()) { }

    internal GitRepositoriesViewModel(GitRepositoryService git)
    {
        _git = git;
        foreach (string path in GitRepositoryList.Load())
            Repositories.Add(new GitRepositoryItemViewModel(path, GitRepositoryList.GetBranch(path)));
        Repositories.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasRepositories));
            OnPropertyChanged(nameof(IsEmpty));
            UpdateBehindCount();
        };

        _checkIntervalIndex = Math.Max(0, Array.IndexOf(GitRepositoryList.CheckIntervals, GitRepositoryList.CheckIntervalMinutes));
        _autoUpdate = GitRepositoryList.AutoUpdate;
        _detailsViewIndex = GitRepositoryList.ShowBranches ? BranchesView : WorktreesView;
        _checkTimer.Tick += async (_, _) => await CheckAllAsync();
        ConfigureTimer();
    }

    public ObservableCollection<GitRepositoryItemViewModel> Repositories { get; } = [];
    public ObservableCollection<GitWorktreeItemViewModel> Worktrees { get; } = [];
    public ObservableCollection<GitBranchItemViewModel> Branches { get; } = [];

    public InfoBarViewModel Notice { get; } = new();

    public bool HasRepositories => Repositories.Count > 0;
    public bool IsEmpty => Repositories.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private GitRepositoryItemViewModel? _selectedRepository;

    public bool HasSelection => SelectedRepository is not null;

    public const int WorktreesView = 0;
    public const int BranchesView = 1;

    /// <summary>What the panel under the clones shows for the selected one: its worktrees or its branches.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowWorktrees), nameof(ShowBranches))]
    private int _detailsViewIndex;

    public bool ShowWorktrees => DetailsViewIndex != BranchesView;
    public bool ShowBranches => DetailsViewIndex == BranchesView;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isLoadingDetails;

    [ObservableProperty]
    private bool _isGitMissing;

    [ObservableProperty]
    private string _gitVersion = "";

    /// <summary>Clones a fast-forward would bring up to date, shown on the sidebar.</summary>
    [ObservableProperty]
    private int _behindCount;

    [ObservableProperty]
    private int _checkIntervalIndex;

    [ObservableProperty]
    private bool _autoUpdate;

    public string[] CheckIntervalOptions { get; } =
    [
        CoreTools.Translate("Only when asked"),
        CoreTools.Translate("Every 15 minutes"),
        CoreTools.Translate("Every 30 minutes"),
        CoreTools.Translate("Every hour"),
        CoreTools.Translate("Every 4 hours"),
    ];

    partial void OnCheckIntervalIndexChanged(int value)
    {
        GitRepositoryList.CheckIntervalMinutes = GitRepositoryList.CheckIntervals[Math.Clamp(value, 0, GitRepositoryList.CheckIntervals.Length - 1)];
        ConfigureTimer();
    }

    partial void OnAutoUpdateChanged(bool value) => GitRepositoryList.AutoUpdate = value;

    partial void OnSelectedRepositoryChanged(GitRepositoryItemViewModel? value) => _ = LoadDetailsAsync();

    partial void OnDetailsViewIndexChanged(int value)
    {
        GitRepositoryList.ShowBranches = value == BranchesView;
        _ = LoadDetailsAsync();
    }

    private void ConfigureTimer()
    {
        int minutes = GitRepositoryList.CheckIntervalMinutes;
        _checkTimer.Stop();
        if (minutes <= 0 || _stopped) return;
        _checkTimer.Interval = TimeSpan.FromMinutes(minutes);
        _checkTimer.Start();
    }

    private bool _stopped;

    /// <summary>Ends automatic checks and loading, as when Git Repositories is turned off.</summary>
    public void Stop()
    {
        _stopped = true;
        _checkTimer.Stop();
        _detailsLoad?.Cancel();
    }

    /// <summary>Picks up the check interval and auto-update choices made on its page under Settings &gt; Extensions.</summary>
    public void ReloadPreferences()
    {
        CheckIntervalIndex = Math.Max(0, Array.IndexOf(GitRepositoryList.CheckIntervals, GitRepositoryList.CheckIntervalMinutes));
        AutoUpdate = GitRepositoryList.AutoUpdate;
    }

    /// <summary>
    /// Finds git and reads every clone's state from what was last fetched, without contacting any remote;
    /// with automatic checks on, also fetches them. Runs once, at startup.
    /// </summary>
    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;

        string? version = await _git.GetGitVersionAsync();
        GitVersion = version ?? "";
        IsGitMissing = version is null;
        if (IsGitMissing)
        {
            Notice.Title = CoreTools.Translate("Git was not found");
            Notice.Message = CoreTools.Translate("Install Git and make sure it is on the PATH, then restart UniGetUI.");
            Notice.Severity = InfoBarSeverity.Error;
            Notice.IsClosable = false;
            Notice.IsOpen = true;
            return;
        }

        if (GitRepositoryList.CheckIntervalMinutes > 0)
            await CheckAllAsync();
        else
            await RunAsync(Repositories, InspectItemAsync);
    }

    // ─── Repositories ────────────────────────────────────────────────────────

    [RelayCommand]
    private async Task AddRepository(Visual? visual)
    {
        if (visual is null || TopLevel.GetTopLevel(visual) is not { } topLevel) return;
        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = CoreTools.Translate("Choose git clones, or folders that contain them"),
            AllowMultiple = true,
        });
        string[] paths = [.. folders.Select(folder => folder.TryGetLocalPath()).OfType<string>()];
        if (paths.Length > 0)
            await AddPathsAsync(paths);
    }

    /// <summary>
    /// Adds the clone each folder is in or, for a folder that is in none, every clone found under it:
    /// picking <c>D:\dev\RDM</c> adds RDM, and picking <c>D:\dev</c> adds all the clones in it.
    /// </summary>
    internal async Task AddPathsAsync(IReadOnlyList<string> paths)
    {
        // A single clone gets the precise notices: already listed, or a worktree replaced by its main clone.
        if (paths.Count == 1 && await _git.LocateAsync(paths[0]) is not null)
        {
            await AddPathAsync(paths[0], quiet: false);
            return;
        }

        int found = 0, added = 0;
        foreach (string path in paths)
        {
            List<string> clones = await _git.LocateAsync(path) is not null ? [path] : FindClones(path);
            found += clones.Count;
            foreach (string clone in clones)
            {
                if (await AddPathAsync(clone, quiet: true))
                    added++;
            }
        }

        if (found == 0)
            ShowNotice(paths.Count == 1
                    ? CoreTools.Translate("No git clones were found in {0}", paths[0])
                    : CoreTools.Translate("No git clones were found in the chosen folders"),
                CoreTools.Translate("Clones are looked for in the chosen folders and two levels of subfolders."),
                InfoBarSeverity.Informational);
        else if (added == 0)
            ShowNotice(CoreTools.Translate("Every git clone found is already in the list"), "", InfoBarSeverity.Informational);
        else
            ShowNotice(added == 1
                    ? CoreTools.Translate("1 git clone was added")
                    : CoreTools.Translate("{0} git clones were added", added),
                "", InfoBarSeverity.Success);
    }

    /// <summary>
    /// Main clones up to two levels below <paramref name="root"/>: folders holding a <c>.git</c> folder.
    /// A <c>.git</c> file marks a linked worktree, which is reached through its main clone instead.
    /// </summary>
    internal static List<string> FindClones(string root, int depth = 2)
    {
        var clones = new List<string>();
        if (Directory.Exists(Path.Combine(root, ".git")))
        {
            clones.Add(root);
            return clones;
        }
        if (depth == 0) return clones;
        try
        {
            foreach (string child in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
            {
                if ((File.GetAttributes(child) & (FileAttributes.Hidden | FileAttributes.ReparsePoint)) != 0
                    || Path.GetFileName(child).StartsWith('.'))
                    continue;
                clones.AddRange(FindClones(child, depth - 1));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"Could not look for git clones in {root}: {ex.Message}");
        }
        return clones;
    }

    /// <summary>Adds a clone to the list; a worktree is replaced by its main clone. Returns whether one was added.</summary>
    internal async Task<bool> AddPathAsync(string path, bool quiet)
    {
        GitRepositoryLocation? location = await _git.LocateAsync(path);
        if (location is null)
        {
            if (!quiet)
                ShowNotice(CoreTools.Translate("{0} is not a git repository", path), "", InfoBarSeverity.Error);
            return false;
        }

        string clone = location.MainClonePath;
        if (!GitRepositoryList.Add(clone))
        {
            if (!quiet)
                ShowNotice(CoreTools.Translate("{0} is already in the list", clone), "", InfoBarSeverity.Informational);
            return false;
        }

        var item = new GitRepositoryItemViewModel(clone, null);
        Repositories.Add(item);
        if (location.IsLinkedWorktree && !quiet)
            ShowNotice(
                CoreTools.Translate("Added the main clone of this worktree"),
                CoreTools.Translate("{0} is a worktree of {1}, so {1} was added instead.", location.Root, clone),
                InfoBarSeverity.Informational);
        SelectedRepository = item;
        await RunAsync([item], InspectItemAsync);
        return true;
    }

    [RelayCommand]
    private void RemoveFromList(GitRepositoryItemViewModel? item)
    {
        if (item is null) return;
        GitRepositoryList.Remove(item.Path);
        Repositories.Remove(item);
        if (ReferenceEquals(SelectedRepository, item))
            SelectedRepository = null;
    }

    [RelayCommand]
    private static void OpenFolder(GitRepositoryItemViewModel? item)
    {
        if (item is not null) CoreTools.Launch(item.Path);
    }

    [RelayCommand]
    private async Task ChooseBranch(GitRepositoryItemViewModel? item)
    {
        if (item is null || MainWindow.Instance is not { } owner) return;

        var input = new TextBox
        {
            Text = item.BranchOverride ?? "",
            PlaceholderText = item.Status?.DefaultBranch ?? "main",
        };
        var body = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = CoreTools.Translate(
                        "UniGetUI keeps this clone current on the branch below. Leave it empty to follow the remote's default branch."),
                    TextWrapping = TextWrapping.Wrap,
                },
                input,
            },
        };
        var dialog = new ImmersiveConfirmationDialog(
            CoreTools.Translate("Branch to keep current"), body, CoreTools.Translate("Save"), CoreTools.Translate("Cancel"));
        await dialog.ShowDialog(owner);
        if (dialog.Result is not true) return;

        string? branch = string.IsNullOrWhiteSpace(input.Text) ? null : input.Text.Trim();
        GitRepositoryList.SetBranch(item.Path, branch);
        item.BranchOverride = branch;
        await RunAsync([item], InspectItemAsync);
    }

    // ─── Check and update ────────────────────────────────────────────────────

    [RelayCommand]
    private Task CheckAll() => CheckAllAsync();

    internal Task CheckAllAsync() => IsGitMissing ? Task.CompletedTask : RunAsync(Repositories, CheckItemAsync);

    [RelayCommand]
    private Task Check(GitRepositoryItemViewModel? item)
        => item is null ? Task.CompletedTask : RunAsync([item], CheckItemAsync);

    [RelayCommand]
    private async Task UpdateAll()
    {
        if (IsGitMissing) return;
        var results = new List<GitUpdateResult>();
        await RunAsync(Repositories, async item =>
        {
            if (await UpdateItemAsync(item, fetch: true) is { } result)
                lock (results) results.Add(result);
        });
        ReportUpdates(results);
    }

    [RelayCommand]
    private async Task Update(GitRepositoryItemViewModel? item)
    {
        if (item is null) return;
        var results = new List<GitUpdateResult>();
        await RunAsync([item], async target =>
        {
            if (await UpdateItemAsync(target, fetch: false) is { } result)
                results.Add(result);
        });
        ReportUpdates(results);
    }

    private void ReportUpdates(IReadOnlyList<GitUpdateResult> results)
    {
        int updated = results.Count(result => result.Outcome == GitUpdateOutcome.Updated);
        int failed = results.Count(result => result.Outcome == GitUpdateOutcome.Failed);
        int skipped = results.Count(result => result.Outcome == GitUpdateOutcome.Refused);
        if (failed > 0)
            ShowNotice(
                CoreTools.Translate("{0} repositories could not be updated", failed),
                string.Join(Environment.NewLine, results
                    .Where(result => result.Outcome == GitUpdateOutcome.Failed)
                    .Select(result => $"{Path.GetFileName(result.Status.Path)}: {FirstLine(result.Output)}")),
                InfoBarSeverity.Error);
        else if (updated > 0 || skipped > 0)
            ShowNotice(
                CoreTools.Translate("{0} repositories updated", updated),
                skipped > 0 ? CoreTools.Translate("{0} were left alone: see their status for why.", skipped) : "",
                skipped > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success);
        else
            ShowNotice(CoreTools.Translate("Every repository is up to date"), "", InfoBarSeverity.Success);
    }

    private static string FirstLine(string text)
        => text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";

    private async Task InspectItemAsync(GitRepositoryItemViewModel item)
    {
        item.Status = await _git.InspectAsync(item.Path, item.BranchOverride);
        await CountWorktreesAsync(item);
    }

    private async Task CheckItemAsync(GitRepositoryItemViewModel item)
    {
        item.Status = await _git.CheckAsync(item.Path, item.BranchOverride);
        item.LastChecked = DateTime.Now;
        await CountWorktreesAsync(item);
        if (AutoUpdate && item.Status.CanUpdate)
            await UpdateItemAsync(item, fetch: false);
    }

    private async Task<GitUpdateResult?> UpdateItemAsync(GitRepositoryItemViewModel item, bool fetch)
    {
        GitUpdateResult result = await _git.UpdateAsync(item.Path, fetch, item.BranchOverride);
        item.Status = result.Status;
        item.LastChecked = DateTime.Now;
        return result;
    }

    private async Task CountWorktreesAsync(GitRepositoryItemViewModel item)
    {
        try
        {
            item.WorktreeCount = await _git.CountLinkedWorktreesAsync(item.Path);
        }
        catch (GitException)
        {
            item.WorktreeCount = null;
        }
    }

    /// <summary>Runs an action on several clones, a few at a time, each marked busy while it runs.</summary>
    private async Task RunAsync(IEnumerable<GitRepositoryItemViewModel> items,
        Func<GitRepositoryItemViewModel, Task> action)
    {
        List<GitRepositoryItemViewModel> targets = [.. items.Where(item => !item.IsBusy)];
        if (targets.Count == 0) return;

        using var gate = new SemaphoreSlim(MaxParallelChecks);
        IsBusy = true;
        try
        {
            await Task.WhenAll(targets.Select(async item =>
            {
                await gate.WaitAsync();
                item.IsBusy = true;
                try
                {
                    await action(item);
                }
                catch (Exception ex)
                {
                    Logger.Error($"Git repository {item.Path} failed");
                    Logger.Error(ex);
                    item.Status = new GitRepositoryStatus(item.Path, GitRepositoryState.Error, Error: ex.Message);
                }
                finally
                {
                    item.IsBusy = false;
                    gate.Release();
                }
            }));
        }
        finally
        {
            IsBusy = Repositories.Any(item => item.IsBusy);
            UpdateBehindCount();
        }

        if (SelectedRepository is { } selected && targets.Contains(selected))
            await LoadDetailsAsync();
    }

    private void UpdateBehindCount() => BehindCount = Repositories.Count(item => item.Status?.CanUpdate == true);

    // ─── Worktrees and branches of the selected clone ───────────────────────

    [RelayCommand]
    private Task RefreshDetails() => LoadDetailsAsync();

    /// <summary>Loads what the panel under the clones shows, dropping whatever an earlier load was doing.</summary>
    internal async Task LoadDetailsAsync()
    {
        _detailsLoad?.Cancel();
        var load = _detailsLoad = new CancellationTokenSource();
        Worktrees.Clear();
        Branches.Clear();
        if (SelectedRepository is not { } repository || IsGitMissing) return;

        IsLoadingDetails = true;
        try
        {
            if (ShowBranches) await LoadBranchesAsync(repository, load.Token);
            else await LoadWorktreesAsync(repository, load.Token);
        }
        catch (OperationCanceledException) { }
        catch (GitException ex)
        {
            Logger.Warn(ex.Message);
            ShowNotice(ShowBranches
                    ? CoreTools.Translate("Could not list the branches of {0}", repository.Name)
                    : CoreTools.Translate("Could not list the worktrees of {0}", repository.Name),
                ex.Message, InfoBarSeverity.Error);
        }
        finally
        {
            if (ReferenceEquals(_detailsLoad, load))
                IsLoadingDetails = false;
        }
    }

    private async Task LoadBranchesAsync(GitRepositoryItemViewModel repository, CancellationToken cancellationToken)
    {
        IReadOnlyList<GitBranch> branches = await Task.Run(() =>
            _git.ListBranchesAsync(repository.Path, repository.BranchOverride, cancellationToken), cancellationToken);
        if (cancellationToken.IsCancellationRequested) return;
        foreach (GitBranch branch in branches)
            Branches.Add(new GitBranchItemViewModel(branch));
        OnPropertyChanged(nameof(HasMergedBranches));
    }

    private async Task LoadWorktreesAsync(GitRepositoryItemViewModel repository, CancellationToken cancellationToken)
    {
        // git status is slow on large working trees, so the list shows first and each one's changes follow.
        // Git runs off the UI thread: starting a process blocks for a moment, and there are dozens of them.
        IReadOnlyList<GitWorktree> worktrees = await Task.Run(() => _git.ListWorktreesAsync(
            repository.Path, repository.BranchOverride, checkLocalChanges: false, cancellationToken), cancellationToken);
        if (cancellationToken.IsCancellationRequested) return;
        GitWorktreeItemViewModel[] items = [.. worktrees.Select(worktree =>
            new GitWorktreeItemViewModel(worktree, changesChecked: false))];
        foreach (GitWorktreeItemViewModel item in items)
            Worktrees.Add(item);
        repository.WorktreeCount = worktrees.Count(worktree => !worktree.IsMain);

        // Running every git status at once only makes them compete for the disk.
        using var gate = new SemaphoreSlim(8);
        await Task.WhenAll(items.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken);
            try { await CheckLocalChangesAsync(item, cancellationToken); }
            finally { gate.Release(); }
        }));
    }

    private async Task CheckLocalChangesAsync(GitWorktreeItemViewModel item, CancellationToken cancellationToken)
    {
        bool changes;
        try
        {
            changes = await Task.Run(() => _git.HasLocalChangesAsync(item.Path, cancellationToken), cancellationToken);
        }
        catch (GitException ex)
        {
            // Unreadable is treated as changed, so removing it still warns that files may be lost.
            Logger.Warn(ex.Message);
            changes = true;
        }
        if (!cancellationToken.IsCancellationRequested)
            item.LocalChanges = changes;
    }

    [RelayCommand]
    private async Task PruneWorktrees()
    {
        if (SelectedRepository is not { } repository) return;
        GitOperationResult result = await _git.PruneWorktreesAsync(repository.Path);
        if (!result.Success)
            ShowNotice(CoreTools.Translate("Could not prune the worktrees of {0}", repository.Name), result.Output, InfoBarSeverity.Error);
        await LoadDetailsAsync();
    }

    [RelayCommand]
    private static void OpenWorktreeFolder(GitWorktreeItemViewModel? worktree)
    {
        if (worktree is { CanOpen: true }) CoreTools.Launch(worktree.Path);
    }

    [RelayCommand]
    private async Task RemoveWorktree(GitWorktreeItemViewModel? worktree)
    {
        if (worktree is not { CanRemove: true } || SelectedRepository is not { } repository
            || MainWindow.Instance is not { } owner)
            return;
        // The warning below must not depend on whether its git status had finished.
        if (worktree.IsCheckingChanges)
            await CheckLocalChangesAsync(worktree, CancellationToken.None);

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock
        {
            Text = CoreTools.Translate("The folder {0} will be deleted.", worktree.Path),
            TextWrapping = TextWrapping.Wrap,
        });
        if (worktree.HasLocalChanges)
            body.Children.Add(Warning(CoreTools.Translate(
                "It has changed or untracked files. They will be lost.")));
        if (worktree.HasUniqueCommits && !worktree.UpstreamGone)
            body.Children.Add(Warning(CoreTools.Translate(
                "Its branch has commits that are not on the default branch. Keep the branch to keep them.")));

        CheckBox? deleteBranch = null;
        if (worktree.Worktree.Branch is { } branch)
        {
            deleteBranch = new CheckBox
            {
                Content = CoreTools.Translate("Also delete the branch {0}", branch),
                IsChecked = worktree.IsMerged || worktree.UpstreamGone,
            };
            body.Children.Add(deleteBranch);
        }

        var dialog = new ImmersiveConfirmationDialog(
            CoreTools.Translate("Remove this worktree?"), body, CoreTools.Translate("Remove"), CoreTools.Translate("Cancel"))
        {
            FocusPrimaryButton = worktree.IsSafeToRemove,
        };
        await dialog.ShowDialog(owner);
        if (dialog.Result is not true) return;

        bool removeBranch = deleteBranch?.IsChecked == true;
        // The dialog spelled out what is lost, so confirming it allows git to discard it.
        bool force = worktree.HasLocalChanges || (removeBranch && !worktree.IsMerged);
        GitOperationResult result = await _git.RemoveWorktreeAsync(
            repository.Path, worktree.Path, force, removeBranch, repository.BranchOverride);
        if (!result.Success)
            ShowNotice(CoreTools.Translate("Could not remove the worktree {0}", worktree.Name), result.Output, InfoBarSeverity.Error);
        await LoadDetailsAsync();
    }

    [RelayCommand]
    private static void OpenBranchWorktree(GitBranchItemViewModel? branch)
    {
        if (branch?.WorktreePath is { } path && Directory.Exists(path)) CoreTools.Launch(path);
    }

    public bool HasMergedBranches => Branches.Any(branch => branch.IsSafeToDelete);

    [RelayCommand]
    private async Task DeleteBranch(GitBranchItemViewModel? branch)
    {
        if (branch is not { CanDelete: true } || SelectedRepository is not { } repository
            || MainWindow.Instance is not { } owner)
            return;

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock
        {
            Text = CoreTools.Translate("The branch {0} will be deleted from {1}.", branch.Name, repository.Name),
            TextWrapping = TextWrapping.Wrap,
        });
        if (branch.HasUniqueCommits && !branch.UpstreamGone)
            body.Children.Add(Warning(CoreTools.Translate(
                "It has commits that are not on the default branch. They will be lost unless they were pushed.")));
        if (branch.Branch.AheadOfUpstream > 0)
            body.Children.Add(Warning(CoreTools.Translate(
                "It has commits that were never pushed to {0}.", branch.Branch.Upstream ?? "")));

        var dialog = new ImmersiveConfirmationDialog(
            CoreTools.Translate("Delete this branch?"), body, CoreTools.Translate("Delete"), CoreTools.Translate("Cancel"))
        {
            FocusPrimaryButton = branch.IsSafeToDelete,
        };
        await dialog.ShowDialog(owner);
        if (dialog.Result is not true) return;

        // The dialog spelled out what is lost, so confirming it allows git to discard it.
        GitOperationResult result = await _git.DeleteBranchAsync(
            repository.Path, branch.Name, force: true, repository.BranchOverride);
        if (!result.Success)
            ShowNotice(CoreTools.Translate("Could not delete the branch {0}", branch.Name), result.Output, InfoBarSeverity.Error);
        await LoadDetailsAsync();
    }

    /// <summary>Deletes every branch whose commits are all on the default branch, as agents leave behind.</summary>
    [RelayCommand]
    private async Task DeleteMergedBranches()
    {
        GitBranchItemViewModel[] merged = [.. Branches.Where(branch => branch.IsSafeToDelete)];
        if (merged.Length == 0 || SelectedRepository is not { } repository || MainWindow.Instance is not { } owner)
            return;

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock
        {
            Text = CoreTools.Translate(
                "Everything committed on these branches is already on the default branch, so nothing is lost:"),
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new TextBlock
        {
            Text = string.Join(Environment.NewLine, merged.Select(branch => branch.Name)),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
        });
        var dialog = new ImmersiveConfirmationDialog(
            CoreTools.Translate("Delete {0} merged branches?", merged.Length), body,
            CoreTools.Translate("Delete"), CoreTools.Translate("Cancel"))
        {
            FocusPrimaryButton = true,
        };
        await dialog.ShowDialog(owner);
        if (dialog.Result is not true) return;

        GitOperationResult result = await _git.DeleteBranchesAsync(
            repository.Path, [.. merged.Select(branch => branch.Name)], force: false, repository.BranchOverride);
        if (!result.Success)
            ShowNotice(CoreTools.Translate("Some branches could not be deleted"), result.Output, InfoBarSeverity.Warning);
        await LoadDetailsAsync();
    }

    private static TextBlock Warning(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontWeight = FontWeight.SemiBold,
    };

    private void ShowNotice(string title, string message, InfoBarSeverity severity)
    {
        if (IsGitMissing) return;
        Notice.Title = title;
        Notice.Message = message;
        Notice.Severity = severity;
        Notice.IsClosable = true;
        Notice.IsOpen = true;
    }
}
