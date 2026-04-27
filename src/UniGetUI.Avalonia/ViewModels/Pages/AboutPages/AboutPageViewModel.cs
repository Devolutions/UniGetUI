using CommunityToolkit.Mvvm.Input;
using UniGetUI.Core.Data;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.ViewModels.Pages.AboutPages;

public partial class AboutPageViewModel : ViewModelBase
{
    public string VersionText { get; } =
        CoreTools.Translate("You have installed UniGetUI Version {0}", CoreData.VersionName);

    public string DisclaimerTitle { get; } = CoreTools.Translate("Disclaimer");

    public string DisclaimerMessage { get; } = CoreTools.Translate(
        "UniGetUI is developed by Devolutions and is not affiliated with any of the compatible package managers.");

    [RelayCommand]
    private static void OpenHomepage() =>
        CoreTools.Launch("https://devolutions.net/unigetui");

    [RelayCommand]
    private static void OpenIssues() =>
        CoreTools.Launch("https://github.com/Devolutions/UniGetUI/issues/new/choose");

    [RelayCommand]
    private static void OpenRepository() =>
        CoreTools.Launch("https://github.com/Devolutions/UniGetUI/");
}
