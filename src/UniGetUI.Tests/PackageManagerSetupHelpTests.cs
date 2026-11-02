using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.PackageEngine.ManagerClasses.Manager;

namespace UniGetUI.Tests;

public class PackageManagerSetupHelpTests
{
    [Fact]
    public void AReadyManagerShowsNoSetupHelp()
    {
        Assert.Equal((false, false), PackageManagerViewModel.GetSetupHelpVisibility(
            loading: false, ready: true, enabled: true, statusFound: true, hasCandidates: true, hasInstallCommand: true));
    }

    [Fact]
    public void NothingIsShownWhileTheManagerReloads()
    {
        Assert.Equal((false, false), PackageManagerViewModel.GetSetupHelpVisibility(
            loading: true, ready: false, enabled: true, statusFound: false, hasCandidates: false, hasInstallCommand: true));
    }

    [Fact]
    public void AnEnabledManagerThatIsNotFoundShowsTheGuideAndTheCommand()
    {
        Assert.Equal((true, true), PackageManagerViewModel.GetSetupHelpVisibility(
            loading: false, ready: false, enabled: true, statusFound: false, hasCandidates: false, hasInstallCommand: true));
    }

    [Fact]
    public void AManagerThatIsFoundButCannotStartShowsTheGuideButNoInstallCommand()
    {
        Assert.Equal((true, false), PackageManagerViewModel.GetSetupHelpVisibility(
            loading: false, ready: false, enabled: true, statusFound: true, hasCandidates: true, hasInstallCommand: true));
    }

    [Fact]
    public void ADisabledManagerWithNoExecutableShowsTheGuideAndTheCommand()
    {
        Assert.Equal((true, true), PackageManagerViewModel.GetSetupHelpVisibility(
            loading: false, ready: false, enabled: false, statusFound: false, hasCandidates: false, hasInstallCommand: true));
    }

    [Fact]
    public void ADisabledManagerWithAnExecutableStillShowsTheGuide()
    {
        Assert.Equal((true, false), PackageManagerViewModel.GetSetupHelpVisibility(
            loading: false, ready: false, enabled: false, statusFound: false, hasCandidates: true, hasInstallCommand: true));
    }

    [Fact]
    public void AManagerWithoutAOneLineInstallShowsOnlyTheGuide()
    {
        Assert.Equal((true, false), PackageManagerViewModel.GetSetupHelpVisibility(
            loading: false, ready: false, enabled: true, statusFound: false, hasCandidates: false, hasInstallCommand: false));
    }

    [Fact]
    public void TheGuideLinkPointsAtTheManagersSection()
    {
        Assert.Equal(
            "https://github.com/Devolutions/UniGetUI/blob/main/docs/PACKAGE_MANAGERS.md#cargo",
            PackageManagerViewModel.GetSetupGuideUrl(new ManagerProperties { SetupGuide = "PACKAGE_MANAGERS.md#cargo" }));
    }

    [Fact]
    public void AManagerWithoutASectionLinksToTheTopOfTheGuide()
    {
        Assert.Equal(
            "https://github.com/Devolutions/UniGetUI/blob/main/docs/PACKAGE_MANAGERS.md",
            PackageManagerViewModel.GetSetupGuideUrl(new ManagerProperties()));
    }
}
