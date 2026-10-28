using Avalonia.Controls;
using UniGetUI.Avalonia.Views;
using UniGetUI.Avalonia.Views.Pages.SettingsPages;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.Interface.Enums;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// A built-in, optional part of UniGetUI that goes beyond package management, such as keeping git clones
/// current. Each is off until turned on from Settings &gt; Extensions, and turns on and off without a restart.
/// </summary>
internal sealed class AppExtension
{
    /// <summary>Stable identifier, also the page name the CLI and IPC navigate to.</summary>
    public required string Id { get; init; }

    /// <summary>The English name, translated where it is shown.</summary>
    public required string Name { get; init; }

    /// <summary>One English sentence on what it does, translated where it is shown.</summary>
    public required string Description { get; init; }

    public required IconType Icon { get; init; }
    public required Settings.K EnabledSetting { get; init; }
    public required PageType Page { get; init; }

    /// <summary>Its page under Settings &gt; Extensions, and how to make one without reflection.</summary>
    public required Type SettingsPage { get; init; }
    public required Func<UserControl> CreateSettingsPage { get; init; }

    /// <summary>Starts or stops what it runs in the app, once its setting has changed.</summary>
    public required Action<bool> ApplyEnabled { get; init; }

    /// <summary>A few words on how it is doing while on, such as "2 behind"; empty when there is nothing to say.</summary>
    public Func<string> Status { get; init; } = () => "";

    public bool IsEnabled => Settings.Get(EnabledSetting);

    public void SetEnabled(bool enabled)
    {
        Settings.Set(EnabledSetting, enabled);
        ApplyEnabled(enabled);
    }
}

internal static class AppExtensions
{
    public static readonly AppExtension GitRepositories = new()
    {
        Id = "git-repositories",
        Name = "Git Repositories",
        Description = "Keep your main git clones up to date and clean up leftover worktrees and branches",
        Icon = IconType.GitBranch,
        EnabledSetting = Settings.K.EnableGitRepositories,
        Page = PageType.GitRepositories,
        SettingsPage = typeof(GitRepositoriesSettings),
        CreateSettingsPage = () => new GitRepositoriesSettings(),
        ApplyEnabled = enabled => MainWindow.Instance?.SetGitRepositoriesEnabled(enabled),
        Status = () => MainWindow.Instance?.GitRepositories is not { } git
            ? ""
            : git.BehindCount > 0
                ? CoreTools.Translate("{0} behind", git.BehindCount)
                : git.Repositories.Count switch
                {
                    0 => CoreTools.Translate("No clones yet"),
                    1 => CoreTools.Translate("1 clone"),
                    int count => CoreTools.Translate("{0} clones", count),
                },
    };

    /// <summary>Every extension, in the order Settings lists them.</summary>
    public static IReadOnlyList<AppExtension> All { get; } = [GitRepositories];
}
