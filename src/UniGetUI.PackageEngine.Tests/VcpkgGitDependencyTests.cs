using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.Classes.Manager.Classes;
using UniGetUI.PackageEngine.Managers.VcpkgManager;

namespace UniGetUI.PackageEngine.Tests;

// The detection tests replace the process PATH, so nothing else may run alongside them.
[CollectionDefinition("Vcpkg git dependency tests", DisableParallelization = true)]
public sealed class VcpkgGitDependencyTestCollection
{
    public const string Name = "Vcpkg git dependency tests";
}

[Collection(VcpkgGitDependencyTestCollection.Name)]
public sealed class VcpkgGitDependencyTests : IDisposable
{
    private const string WingetManualCommand = "winget install --id Git.Git --exact --source winget";

    private readonly string _testRoot = Path.Combine(
        AppContext.BaseDirectory,
        nameof(VcpkgGitDependencyTests),
        Guid.NewGuid().ToString("N")
    );

    private readonly string? _originalPath = Environment.GetEnvironmentVariable(
        "PATH",
        EnvironmentVariableTarget.Process
    );

    public VcpkgGitDependencyTests()
    {
        Directory.CreateDirectory(_testRoot);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Settings.ResetSettings();
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _originalPath, EnvironmentVariableTarget.Process);
        Settings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    private static ManagerDependency GitDependency() =>
        Assert.Single(new Vcpkg().Dependencies, dep => dep.Name == "Git");

    private string PlantFakeGit()
    {
        string binDirectory = Path.Combine(_testRoot, "bin");
        Directory.CreateDirectory(binDirectory);
        // The platform-native name is spelled out here on purpose: the fixture must not be
        // derived from the code under test, or a wrong executable name would plant a matching file.
        string gitPath = Path.Combine(binDirectory, OperatingSystem.IsWindows() ? "git.exe" : "git");
        File.WriteAllText(gitPath, "");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(
                gitPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            );
        }

        return binDirectory;
    }

    private static Func<string, bool> OnPath(params string[] executables) =>
        name => executables.Contains(name);

    private static Func<string, bool> Existing(params string[] files) =>
        path => files.Contains(path);

    [Fact]
    public void ExecutableName_MatchesThePlatform()
    {
        Assert.Equal(
            OperatingSystem.IsWindows() ? "git.exe" : "git",
            VcpkgGitDependency.ExecutableName
        );
    }

    // On main this fails on Linux and macOS: the check asked for "git.exe", which never matches a
    // POSIX git. On Windows it passes before and after, since Windows was never affected.
    [Fact]
    public async Task IsInstalled_FindsThePlatformNativeGitOnPath()
    {
        string binDirectory = PlantFakeGit();
        Environment.SetEnvironmentVariable(
            "PATH",
            binDirectory + Path.PathSeparator + _originalPath,
            EnvironmentVariableTarget.Process
        );

        Assert.True(await GitDependency().IsInstalled());
    }

    [Fact]
    public async Task IsInstalled_ReportsMissingWhenGitIsNotOnPath()
    {
        // Windows also searches the user and machine PATH from the registry, where CI has git.
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string emptyDirectory = Path.Combine(_testRoot, "empty");
        Directory.CreateDirectory(emptyDirectory);
        Environment.SetEnvironmentVariable("PATH", emptyDirectory, EnvironmentVariableTarget.Process);

        Assert.False(await GitDependency().IsInstalled());
    }

    [Fact]
    public void Create_OnWindows_KeepsTheWingetInstallCommand()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ManagerDependency dep = GitDependency();

        Assert.Equal(WingetManualCommand, dep.FancyInstallCommand);
        Assert.Equal(
            (
                CoreData.PowerShell5,
                "-ExecutionPolicy Bypass -NoLogo -NoProfile -Command \"& {winget install --id Git.Git --exact "
                    + "--source winget --accept-source-agreements --accept-package-agreements --force}\""
            ),
            dep.GetInstallCommand()
        );
    }

    [Fact]
    public void Create_OffWindows_DoesNotOfferWinget()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        ManagerDependency dep = GitDependency();

        Assert.DoesNotContain("winget", dep.FancyInstallCommand);
        Assert.DoesNotContain("winget", dep.InstallArguments);
        Assert.NotEqual(CoreData.PowerShell5, dep.InstallFileName);
    }

    [Fact]
    public void GetInstallCommand_OnLinuxWithoutAPackageManager_ExplainsInsteadOfStartingNothing()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        string emptyDirectory = Path.Combine(_testRoot, "empty");
        Directory.CreateDirectory(emptyDirectory);
        Environment.SetEnvironmentVariable("PATH", emptyDirectory, EnvironmentVariableTarget.Process);

        ManagerDependency dep = GitDependency();

        var exception = Assert.Throws<InvalidOperationException>(() => dep.GetInstallCommand());
        Assert.Contains("Git", exception.Message);
        Assert.Contains("manually", dep.FancyInstallCommand);
    }

    [Fact]
    public void Resolve_OnMacOs_UsesHomebrewFromPath()
    {
        var command = VcpkgGitDependency.Resolve(
            isMacOS: true,
            OnPath("brew"),
            Existing(),
            elevatorPath: "",
            elevatorArgs: ""
        );

        Assert.Equal(
            new VcpkgGitDependency.InstallCommand("brew", "install git", "brew install git"),
            command
        );
    }

    [Fact]
    public void Resolve_OnMacOs_FindsHomebrewOutsideThePathOfAGuiProcess()
    {
        var command = VcpkgGitDependency.Resolve(
            isMacOS: true,
            OnPath(),
            Existing("/opt/homebrew/bin/brew"),
            elevatorPath: "",
            elevatorArgs: ""
        );

        Assert.Equal(
            new VcpkgGitDependency.InstallCommand(
                "/opt/homebrew/bin/brew",
                "install git",
                "/opt/homebrew/bin/brew install git"
            ),
            command
        );
    }

    [Fact]
    public void Resolve_OnMacOs_FallsBackToTheCommandLineTools()
    {
        var command = VcpkgGitDependency.Resolve(
            isMacOS: true,
            OnPath(),
            Existing(),
            elevatorPath: "",
            elevatorArgs: ""
        );

        Assert.Equal(
            new VcpkgGitDependency.InstallCommand(
                "xcode-select",
                "--install",
                "xcode-select --install"
            ),
            command
        );
    }

    [Theory]
    [InlineData("dnf", "/usr/bin/pkexec", "", "/usr/bin/pkexec", "dnf install -y git", "sudo dnf install -y git")]
    [InlineData("apt-get", "/usr/bin/sudo", "-A", "/usr/bin/sudo", "-A apt-get install -y git", "sudo apt-get install -y git")]
    [InlineData("zypper", "/usr/bin/pkexec", "", "/usr/bin/pkexec", "zypper --non-interactive install git", "sudo zypper --non-interactive install git")]
    [InlineData("pacman", "", "", "pacman", "-S --noconfirm --needed git", "sudo pacman -S --noconfirm --needed git")]
    public void Resolve_OnLinux_RunsTheDistributionPackageManagerThroughTheElevator(
        string packageManager,
        string elevatorPath,
        string elevatorArgs,
        string expectedFileName,
        string expectedArguments,
        string expectedManualCommand
    )
    {
        var command = VcpkgGitDependency.Resolve(
            isMacOS: false,
            OnPath(packageManager),
            Existing(),
            elevatorPath,
            elevatorArgs
        );

        Assert.Equal(
            new VcpkgGitDependency.InstallCommand(
                expectedFileName,
                expectedArguments,
                expectedManualCommand
            ),
            command
        );
    }

    [Fact]
    public void Resolve_OnLinux_PrefersAptGetWhenSeveralPackageManagersExist()
    {
        var command = VcpkgGitDependency.Resolve(
            isMacOS: false,
            OnPath("zypper", "dnf", "apt-get"),
            Existing(),
            elevatorPath: "",
            elevatorArgs: ""
        );

        Assert.Equal("apt-get", command?.FileName);
    }

    [Fact]
    public void Resolve_OnLinux_IgnoresHomebrewAndReturnsNullWithoutADistributionPackageManager()
    {
        var command = VcpkgGitDependency.Resolve(
            isMacOS: false,
            OnPath("brew"),
            Existing("/home/linuxbrew/.linuxbrew/bin/brew"),
            elevatorPath: "/usr/bin/pkexec",
            elevatorArgs: ""
        );

        Assert.Null(command);
    }
}
