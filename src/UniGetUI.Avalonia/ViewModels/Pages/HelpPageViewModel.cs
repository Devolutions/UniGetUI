using CommunityToolkit.Mvvm.Input;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.ViewModels.Pages;

public partial class HelpPageViewModel : ViewModels.ViewModelBase
{
    private const string HelpBaseUrl = "https://marticliment.com/unigetui/help/";
    private string _currentUrl = HelpBaseUrl;

    [RelayCommand]
    private void OpenInBrowser() => CoreTools.Launch(_currentUrl);

    public void NavigateTo(string uriAttachment)
    {
        _currentUrl = string.IsNullOrEmpty(uriAttachment)
            ? HelpBaseUrl
            : HelpBaseUrl + uriAttachment;
    }
}
