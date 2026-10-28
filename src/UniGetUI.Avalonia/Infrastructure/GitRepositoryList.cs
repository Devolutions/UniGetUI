using System.Globalization;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Extensions.GitRepositories;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>The main clones the Git Repositories page keeps current, and how it checks them.</summary>
internal static class GitRepositoryList
{
    private static string ListName => Settings.ResolveKey(Settings.K.GitRepositories);

    public static IReadOnlyList<string> Load() => Settings.GetList<string>(ListName) ?? [];

    /// <summary>Adds a clone unless it is already listed; returns whether it was added.</summary>
    public static bool Add(string path)
    {
        List<string> paths = [.. Load()];
        if (paths.Any(existing => GitRepositoryService.SamePath(existing, path)))
            return false;
        paths.Add(path);
        Settings.SetList(ListName, paths);
        return true;
    }

    public static void Remove(string path)
    {
        List<string> paths = [.. Load().Where(existing => !GitRepositoryService.SamePath(existing, path))];
        Settings.SetList(ListName, paths);
        SetBranch(path, null);
    }

    /// <summary>The branch to keep current for a clone, when it is not the remote's default one.</summary>
    public static string? GetBranch(string path)
    {
        string? branch = Settings.GetDictionaryItem<string, string>(Settings.K.GitRepositoryBranches, path);
        return string.IsNullOrWhiteSpace(branch) ? null : branch;
    }

    public static void SetBranch(string path, string? branch)
    {
        if (string.IsNullOrWhiteSpace(branch))
            Settings.RemoveDictionaryKey<string, string>(Settings.K.GitRepositoryBranches, path);
        else
            Settings.SetDictionaryItem(Settings.K.GitRepositoryBranches, path, branch.Trim());
    }

    /// <summary>The choices offered for automatic checks, in minutes; zero means only on request.</summary>
    public static readonly int[] CheckIntervals = [0, 15, 30, 60, 240];

    public static int CheckIntervalMinutes
    {
        get => int.TryParse(Settings.GetValue(Settings.K.GitRepositoriesCheckInterval), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out int minutes) && CheckIntervals.Contains(minutes) ? minutes : 0;
        set => Settings.SetValue(Settings.K.GitRepositoriesCheckInterval,
            value > 0 ? value.ToString(CultureInfo.InvariantCulture) : "");
    }

    /// <summary>Whether a check fast-forwards the clones it finds behind, instead of only reporting them.</summary>
    public static bool AutoUpdate
    {
        get => Settings.Get(Settings.K.GitRepositoriesAutoUpdate);
        set => Settings.Set(Settings.K.GitRepositoriesAutoUpdate, value);
    }

    /// <summary>Whether the panel under the clones shows their branches rather than their worktrees.</summary>
    public static bool ShowBranches
    {
        get => Settings.GetValue(Settings.K.GitRepositoriesDetailsView) == "branches";
        set => Settings.SetValue(Settings.K.GitRepositoriesDetailsView, value ? "branches" : "");
    }

    public const double DefaultDetailsHeight = 280;
    public const double MinimumDetailsHeight = 120;

    /// <summary>The height the panel under the clones was last dragged to.</summary>
    public static double DetailsHeight
    {
        get => double.TryParse(Settings.GetValue(Settings.K.GitRepositoriesDetailsHeight), NumberStyles.Float,
            CultureInfo.InvariantCulture, out double height) && height >= MinimumDetailsHeight ? height : DefaultDetailsHeight;
        set => Settings.SetValue(Settings.K.GitRepositoriesDetailsHeight,
            Math.Round(Math.Max(value, MinimumDetailsHeight)).ToString(CultureInfo.InvariantCulture));
    }
}
