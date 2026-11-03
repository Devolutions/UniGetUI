using UniGetUI.Avalonia.ViewModels.Pages.SettingsPages;
using UniGetUI.PackageEngine.ManagerClasses.Manager;

namespace UniGetUI.Tests;

public class PackageManagerSetupHelpTests
{
    [Fact]
    public void AReadyManagerShowsNoSetupHelp()
    {
        Assert.Equal((false, false), PackageManagerViewModel.GetSetupHelpVisibility(
            loading: false, ready: true, enabled: true, statusFound: true, foundWhileDisabled: true, hasInstallCommand: true));
    }

    [Fact]
    public void NothingIsShownWhileTheManagerReloads()
    {
        Assert.Equal((false, false), PackageManagerViewModel.GetSetupHelpVisibility(
            loading: true, ready: false, enabled: true, statusFound: false, foundWhileDisabled: false, hasInstallCommand: true));
    }

    [Fact]
    public void AnEnabledManagerThatIsNotFoundShowsTheGuideAndTheCommand()
    {
        Assert.Equal((true, true), PackageManagerViewModel.GetSetupHelpVisibility(
            loading: false, ready: false, enabled: true, statusFound: false, foundWhileDisabled: false, hasInstallCommand: true));
    }

    [Fact]
    public void AManagerThatIsFoundButCannotStartShowsTheGuideButNoInstallCommand()
    {
        Assert.Equal((true, false), PackageManagerViewModel.GetSetupHelpVisibility(
            loading: false, ready: false, enabled: true, statusFound: true, foundWhileDisabled: true, hasInstallCommand: true));
    }

    [Fact]
    public void ADisabledManagerWithNoExecutableShowsTheGuideAndTheCommand()
    {
        Assert.Equal((true, true), PackageManagerViewModel.GetSetupHelpVisibility(
            loading: false, ready: false, enabled: false, statusFound: false, foundWhileDisabled: false, hasInstallCommand: true));
    }

    [Fact]
    public void ADisabledManagerWhoseExecutableIsPresentShowsTheGuideButNoInstallCommand()
    {
        Assert.Equal((true, false), PackageManagerViewModel.GetSetupHelpVisibility(
            loading: false, ready: false, enabled: false, statusFound: false, foundWhileDisabled: true, hasInstallCommand: true));
    }

    [Fact]
    public void AManagerWithoutAOneLineInstallShowsOnlyTheGuide()
    {
        Assert.Equal((true, false), PackageManagerViewModel.GetSetupHelpVisibility(
            loading: false, ready: false, enabled: true, statusFound: false, foundWhileDisabled: false, hasInstallCommand: false));
    }

    [Theory]
    [InlineData("winget install --exact --id Rustlang.Rustup", "winget")]
    [InlineData("brew install node", "brew")]
    [InlineData("/bin/bash -c \"$(curl -fsSL https://example.test/install.sh)\"", "/bin/bash")]
    public void ACommandIsOfferedOnlyWhenItsProgramIsFound(string command, string program)
    {
        List<string> asked = [];
        Assert.Equal(command, PackageManagerViewModel.RunnableCommand(command, p => { asked.Add(p); return true; }));
        Assert.Equal([program], asked);
        Assert.Null(PackageManagerViewModel.RunnableCommand(command, _ => false));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoCommandIsOfferedWhenTheManagerDeclaresNone(string? command)
    {
        Assert.Null(PackageManagerViewModel.RunnableCommand(command, _ => true));
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
