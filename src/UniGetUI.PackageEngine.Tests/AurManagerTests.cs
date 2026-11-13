using UniGetUI.PackageEngine.AgentBroker;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Managers.AurManager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Structs;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

namespace UniGetUI.PackageEngine.Tests;

public sealed class AurManagerTests
{
    [Theory]
    [InlineData("paru v2.0.4 - libalpm v15.0.0\n", "paru 2.0.4")]
    [InlineData("yay v12.4.2 - libalpm v15.0.0\n", "yay 12.4.2")]
    public void ReadsTheHelperVersion(string output, string expected)
    {
        Assert.Equal(expected, AurOutputParser.ParseHelperVersion(output));
    }

    [Fact]
    public void ParsesParuSearchOutput()
    {
        const string output =
            "aur/visual-studio-code-bin 1.95.3-1 [+1601 ~12.47] [installed: 1.95.2-1]\n" +
            "    Visual Studio Code (vscode): Editor for building and debugging modern web and cloud applications\n" +
            "aur/vscodium-bin 1.95.3.24321-1 [+523 ~5.10] [out-of-date: 2024-11-20] [orphaned]\n" +
            "    Free/Libre Open Source Software Binaries of VSCode\n";

        var result = AurOutputParser.ParseSearch(output);

        Assert.Equal(
            [new("visual-studio-code-bin", "1.95.3-1"), new("vscodium-bin", "1.95.3.24321-1")],
            result);
    }

    [Fact]
    public void ParsesYaySearchOutput()
    {
        const string output =
            "aur/google-chrome 131.0.6778.85-1 (+2153 9.87) [2d] (Installed)\r\n" +
            "    The popular web browser by Google (Stable Channel)\r\n" +
            "aur/google-chrome-beta 132.0.6834.32-1 (+356 0.42) [1mo] (Orphaned) (Out-of-date: 2024-11-20) \tThe popular web browser by Google (Beta Channel)\r\n";

        var result = AurOutputParser.ParseSearch(output);

        Assert.Equal(
            [new("google-chrome", "131.0.6778.85-1"), new("google-chrome-beta", "132.0.6834.32-1")],
            result);
    }

    [Fact]
    public void ParsesForeignInstalledPackages()
    {
        const string output = "paru 2.0.4-1\nvisual-studio-code-bin 1.95.2-1\n\nnot a package line\n";

        var result = AurOutputParser.ParseInstalled(output);

        Assert.Equal([new("paru", "2.0.4-1"), new("visual-studio-code-bin", "1.95.2-1")], result);
    }

    [Fact]
    public void ParsesUpdatesAndSkipsIgnoredAndDevelOnes()
    {
        const string output =
            "visual-studio-code-bin 1.95.2-1 -> 1.95.3-1\n" +
            "google-chrome 130.0.6723.116-1 -> 131.0.6778.85-1 [ignored]\n" +
            ":: Looking for AUR upgrades...\n" +
            "neovim-git 0.11.0.r123.g1a2b3c-1 -> latest-commit\n" +
            "spotify 1:1.2.48.405-1 -> 1:1.2.50.335-1 [3d]\n";

        var result = AurOutputParser.ParseUpdates(output);

        Assert.Equal(
            [
                new("visual-studio-code-bin", "1.95.2-1", "1.95.3-1"),
                new("spotify", "1:1.2.48.405-1", "1:1.2.50.335-1"),
            ],
            result);
    }

    [Fact]
    public void ParsesAurPackageDetails()
    {
        const string output =
            "Repository      : aur\n" +
            "Name            : visual-studio-code-bin\n" +
            "Version         : 1.95.3-1\n" +
            "Description     : Visual Studio Code (vscode)\n" +
            "Groups          : None\n" +
            "Licenses        : custom:commercial\n" +
            "URL             : https://code.visualstudio.com/\n" +
            "AUR URL         : https://aur.archlinux.org/packages/visual-studio-code-bin\n" +
            "Keywords        : vscode  editor\n" +
            "Depends On      : libxkbfile  gnupg  gtk3  libsecret  nss  gcc-libs\n" +
            "                  glibc>=2.28-4\n" +
            "Make Deps       : git  npm\n" +
            "Optional Deps   : glib2: Needed for move to trash functionality\n" +
            "Maintainer      : dcelasun\n" +
            "Last Modified   : Thu 14 Nov 2024 10:12:31 AM\n" +
            "\n" +
            "Repository      : aur\n" +
            "Name            : another-package\n";

        var details = new PackageDetails(new PackageBuilder().WithId("visual-studio-code-bin").Build());

        AurOutputParser.ParseDetails(output, details);

        Assert.Equal("Visual Studio Code (vscode)", details.Description);
        Assert.Equal("custom:commercial", details.License);
        Assert.Equal("dcelasun", details.Publisher);
        Assert.Equal("1.95.3-1", details.Version);
        Assert.Equal(new Uri("https://code.visualstudio.com/"), details.HomepageUrl);
        Assert.Equal(new Uri("https://aur.archlinux.org/packages/visual-studio-code-bin"), details.ManifestUrl);
        Assert.Equal(["vscode", "editor"], details.Tags);
        Assert.Equal(
            ["libxkbfile", "gnupg", "gtk3", "libsecret", "nss", "gcc-libs", "glibc", "glib2"],
            details.Dependencies.Select(d => d.Name));
        Assert.False(details.Dependencies.Single(d => d.Name == "glib2").Mandatory);
        Assert.True(details.Dependencies.Single(d => d.Name == "glibc").Mandatory);
    }

    [Fact]
    public void ParsesYayDetailsWithWrappedLists()
    {
        const string output =
            "Repository                    : aur\n" +
            "Name                          : visual-studio-code-bin\n" +
            "Description                   : Visual Studio Code (vscode)\n" +
            "URL                           : https://code.visualstudio.com/\n" +
            "Depends On                    : libxkbfile  gnupg  gtk3  libsecret  nss\n" +
            "                                gcc-libs  glibc\n" +
            "Optional Deps                 : glib2: Needed for move to trash functionality\n" +
            "                                libdbusmenu-glib: KDE global menu\n" +
            "AUR URL                       : https://aur.archlinux.org/packages/visual-studio-code-bin\n" +
            "Keywords                      : vscode  editor\n" +
            "Maintainer                    : dcelasun\n";

        var details = new PackageDetails(new PackageBuilder().WithId("visual-studio-code-bin").Build());

        AurOutputParser.ParseDetails(output, details);

        Assert.Equal("dcelasun", details.Publisher);
        Assert.Equal(new Uri("https://aur.archlinux.org/packages/visual-studio-code-bin"), details.ManifestUrl);
        Assert.Equal(
            ["libxkbfile", "gnupg", "gtk3", "libsecret", "nss", "gcc-libs", "glibc", "glib2", "libdbusmenu-glib"],
            details.Dependencies.Select(d => d.Name));
        Assert.False(details.Dependencies.Single(d => d.Name == "libdbusmenu-glib").Mandatory);
    }

    [Fact]
    public void AnUnidentifiedHelperOnlyGetsTheOptionsBothHelpersUnderstand()
    {
        var args = AurOperationArguments.Build(OperationType.Install, "yay-bin", AurHelperKind.Unknown, "/usr/bin/pkexec", "");

        Assert.Equal(["-S", "--aur", "--noconfirm", "--sudo=/usr/bin/pkexec", "yay-bin"], args);
    }

    [Theory]
    [InlineData(1, 0, "", 0)]
    [InlineData(1, 0, "error: failed to fetch https://aur.archlinux.org/rpc\n", 1)]
    [InlineData(1, 0, " -> Error during AUR search: Too many package results.\n", 1)]
    [InlineData(1, 0, "warning: Packages not in the AUR: my-local-pkg\n", 0)]
    [InlineData(127, 0, "", 127)]
    [InlineData(1, 3, "error: one package failed\n", 1)]
    [InlineData(0, 0, "", 0)]
    public void OnlyAnExitCodeOfOneWithNothingFoundAndNoErrorMeansAnEmptyResult(int exitCode, int count, string stderr, int expected)
    {
        Assert.Equal(expected, Aur.ListingExitCode(exitCode, count, stderr));
    }

    [Theory]
    [InlineData(true, "/usr/bin/yay", false, true, "yay")]
    [InlineData(true, "/usr/local/bin/aurhelper", false, true, "aurhelper")]
    [InlineData(true, "/usr/bin/paru", true, false, Aur.DefaultFriendlyName)]
    [InlineData(false, "", false, false, Aur.DefaultFriendlyName)]
    public void ResolvesTheExecutableAndRefusesToRunAsRoot(
        bool found, string path, bool runningAsRoot, bool expectedFound, string expectedName)
    {
        Assert.Equal((expectedFound, expectedName), Aur.ResolveExecutable(found, path, runningAsRoot));
    }

    [Theory]
    [InlineData("1.0-2", "1.0.1-1", -1)]
    [InlineData("1:1.2.48.405-1", "1:1.2.50.335-1", -1)]
    [InlineData("1:1.0-1", "2.0-1", 1)]
    public void ComparesVersionsTheWayPacmanDoes(string a, string b, int expected)
    {
        Assert.Equal(expected, new Aur().CompareVersions(a, b));
    }

    [Fact]
    public void FallsBackToThePackagePageWhenTheHelperGivesNoAurUrl()
    {
        var details = new PackageDetails(new PackageBuilder().WithId("yay-bin").Build());

        AurOutputParser.ParseDetails("Name            : yay-bin\n", details);

        Assert.Equal(new Uri("https://aur.archlinux.org/packages/yay-bin"), details.ManifestUrl);
    }

    [Theory]
    [InlineData("paru v2.0.4 - libalpm v15.0.0\n", "/usr/local/bin/aur", AurHelperKind.Paru)]
    [InlineData("yay v12.4.2 - libalpm v15.0.0\n", "/usr/local/bin/paru", AurHelperKind.Yay)]
    [InlineData("", "/usr/bin/yay", AurHelperKind.Yay)]
    [InlineData("", "/usr/bin/paru-git", AurHelperKind.Paru)]
    [InlineData("", "/usr/local/bin/aurhelper", AurHelperKind.Unknown)]
    [InlineData("error while loading shared libraries: libalpm.so.15\n", "/usr/local/bin/aurhelper", AurHelperKind.Unknown)]
    public void RecognizesTheHelperFromItsVersionThenItsFileName(string versionOutput, string path, AurHelperKind expected)
    {
        Assert.Equal(expected, AurOutputParser.DetectHelper(versionOutput, path));
    }

    [Fact]
    public void ParuInstallsSkipReviewAndUseTheConfiguredElevator()
    {
        var args = AurOperationArguments.Build(OperationType.Install, "yay-bin", AurHelperKind.Paru, "/usr/bin/sudo", "-A");

        Assert.Equal(
            ["-S", "--aur", "--skipreview", "--noconfirm", "--sudo=/usr/bin/sudo", "--sudoflags=-A", "yay-bin"],
            args);
    }

    [Fact]
    public void YayUpdatesAnswerEveryMenuWithNone()
    {
        var args = AurOperationArguments.Build(OperationType.Update, "paru-bin", AurHelperKind.Yay, "/usr/bin/pkexec", "");

        Assert.Equal(
            [
                "-S", "--aur", "--answerclean=None", "--answerdiff=None", "--answeredit=None",
                "--noconfirm", "--sudo=/usr/bin/pkexec", "--sudoloop=false", "paru-bin",
            ],
            args);
    }

    [Fact]
    public void ParuTurnsTheSudoLoopOffForAnElevatorThatIsNotSudo()
    {
        var args = AurOperationArguments.Build(OperationType.Install, "yay-bin", AurHelperKind.Paru, "/usr/bin/pkexec", "");

        Assert.Equal(
            ["-S", "--aur", "--skipreview", "--noconfirm", "--sudo=/usr/bin/pkexec", "--nosudoloop", "yay-bin"],
            args);
    }

    [Fact]
    public void UninstallsRemoveDependenciesAndLeaveTheElevatorUnsetWhenNoneWasFound()
    {
        var args = AurOperationArguments.Build(OperationType.Uninstall, "paru-bin", AurHelperKind.Paru, "", "");

        Assert.Equal(["-Rs", "--noconfirm", "paru-bin"], args);
    }

    [Fact]
    public void OperationsNeverRunElevatedWhateverTheOptionsSay()
    {
        var manager = new Aur();
        var package = new PackageBuilder()
            .WithManager(manager)
            .WithId("paru-bin")
            .WithOptions(new OverridenInstallationOptions(runAsAdministrator: true))
            .Build();
        var options = new Serializable.InstallOptions { RunAsAdministrator = true };

        Assert.False(BrokerRequestValidator.RequestsElevation(package, options));
        Assert.False(manager.Capabilities.CanRunAsAdmin);
    }
}
