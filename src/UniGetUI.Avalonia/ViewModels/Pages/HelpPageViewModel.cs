using CommunityToolkit.Mvvm.Input;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.ViewModels.Pages;

public partial class HelpPageViewModel : ViewModels.ViewModelBase
{
    public const string HelpBaseUrl = "https://marticliment.com/unigetui/help/";

    // Kept in sync from the WebView's NavigationCompleted event via code-behind
    public string CurrentUrl { get; set; } = HelpBaseUrl;

    [RelayCommand]
    private void OpenInBrowser() => CoreTools.Launch(CurrentUrl);

    public string GetInitialUrl(string uriAttachment) =>
        string.IsNullOrEmpty(uriAttachment) ? HelpBaseUrl : HelpBaseUrl + uriAttachment;
}
