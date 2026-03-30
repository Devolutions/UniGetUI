using CommunityToolkit.Mvvm.Input;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.ViewModels.Pages;

public partial class ReleaseNotesPageViewModel : ViewModels.ViewModelBase
{
    public string ReleaseNotesUrl { get; } = CoreData.GetGitHubReleasePageUrl();

    // Kept in sync from the WebView's NavigationCompleted event via code-behind
    public string CurrentUrl { get; set; } = CoreData.GetGitHubReleasePageUrl();

    [RelayCommand]
    private void OpenInBrowser() => CoreTools.Launch(CurrentUrl);
}
