using UniGetUI.PackageEngine.Managers.CargoManager;

namespace UniGetUI.PackageEngine.Tests;

public sealed class CargoBinDirectoryTests
{
    private static Func<string, string?> Env(params (string Name, string Value)[] values) =>
        name => values.FirstOrDefault(entry => entry.Name == name).Value;

    [Fact]
    public void GetCargoBinDirectories_FallsBackToTheDefaultCargoHome()
    {
        var directories = Cargo.GetCargoBinDirectories(Env(), Path.Join("C:", "Users", "tester"));

        Assert.Equal([Path.Join("C:", "Users", "tester", ".cargo", "bin")], directories);
    }

    [Fact]
    public void GetCargoBinDirectories_ReplacesTheDefaultWhenCargoHomeIsSet()
    {
        var directories = Cargo.GetCargoBinDirectories(
            Env(("CARGO_HOME", Path.Join("D:", "scoop", "persist", "rustup", ".cargo"))),
            Path.Join("C:", "Users", "tester")
        );

        Assert.Equal(
            [Path.Join("D:", "scoop", "persist", "rustup", ".cargo", "bin")],
            directories
        );
    }

    [Fact]
    public void GetCargoBinDirectories_IgnoresCargoInstallRoot()
    {
        var directories = Cargo.GetCargoBinDirectories(
            Env(("CARGO_INSTALL_ROOT", Path.Join("D:", "tools", "cargo-bins"))),
            Path.Join("C:", "Users", "tester")
        );

        Assert.Equal([Path.Join("C:", "Users", "tester", ".cargo", "bin")], directories);
    }

    [Fact]
    public void GetCargoBinDirectories_IgnoresBlankValues()
    {
        var directories = Cargo.GetCargoBinDirectories(Env(("CARGO_HOME", "   ")), "");

        Assert.Empty(directories);
    }

    [Fact]
    public void IsCargoBinaryPresent_FindsBinariesUnderCargoHomeWhenNotOnPath()
    {
        string binaryName = OperatingSystem.IsWindows()
            ? "cargo-unigetui-detection-probe.exe"
            : "cargo-unigetui-detection-probe";
        string cargoHome = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        string binDirectory = Path.Join(cargoHome, "bin");
        string binaryPath = Path.Join(binDirectory, binaryName);
        string? previous = Environment.GetEnvironmentVariable("CARGO_HOME");

        try
        {
            Assert.False(Cargo.IsCargoBinaryPresent(binaryName));

            Directory.CreateDirectory(binDirectory);
            File.WriteAllText(binaryPath, "");
            Environment.SetEnvironmentVariable("CARGO_HOME", cargoHome);

            if (!OperatingSystem.IsWindows())
            {
                Assert.False(Cargo.IsCargoBinaryPresent(binaryName));
                File.SetUnixFileMode(
                    binaryPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                );
            }

            Assert.True(Cargo.IsCargoBinaryPresent(binaryName));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CARGO_HOME", previous);
            if (Directory.Exists(cargoHome))
                Directory.Delete(cargoHome, true);
        }
    }
}
