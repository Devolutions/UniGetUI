using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Tools;
using UniGetUI.Extensions.GitRepositories;

namespace UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;

/// <summary>The Git Repositories page under Settings &gt; Extensions.</summary>
public partial class GitRepositoriesSettingsViewModel : ViewModelBase
{
    /// <summary>The automatic check choices, as labels and the values stored for them.</summary>
    public static readonly (string Label, string Value)[] CheckIntervalItems =
    [
        ("Only when asked", ""),
        ("Every 15 minutes", "15"),
        ("Every 30 minutes", "30"),
        ("Every hour", "60"),
        ("Every 4 hours", "240"),
    ];

    public GitRepositoriesSettingsViewModel() => _ = FindGitAsync();

    [ObservableProperty]
    private bool _isEnabled = AppExtensions.GitRepositories.IsEnabled;

    [ObservableProperty]
    private string _gitStatus = CoreTools.Translate("Looking for Git...");

    [ObservableProperty]
    private bool _isGitFound;

    private async Task FindGitAsync()
    {
        string? version = await new GitRepositoryService().GetGitVersionAsync();
        IsGitFound = version is not null;
        GitStatus = version is not null
            ? CoreTools.Translate("Found {0}", version)
            : CoreTools.Translate("Git was not found. Install it from Discover Packages, then come back to this page.");
    }

    [RelayCommand]
    private void EnabledChanged()
    {
        // The card has saved the setting; this starts or stops what runs in the app.
        IsEnabled = AppExtensions.GitRepositories.IsEnabled;
        AppExtensions.GitRepositories.ApplyEnabled(IsEnabled);
    }

    [RelayCommand]
    private static void PreferencesChanged() => MainWindow.Instance?.GitRepositories?.ReloadPreferences();

    [RelayCommand]
    private static void Open() => MainWindow.Instance?.Navigate(PageType.GitRepositories);
}
