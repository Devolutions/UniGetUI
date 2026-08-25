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
    public void GetCargoBinDirectories_HonorsCargoHome()
    {
        var directories = Cargo.GetCargoBinDirectories(
            Env(("CARGO_HOME", Path.Join("D:", "scoop", "persist", "rustup", ".cargo"))),
            Path.Join("C:", "Users", "tester")
        );

        Assert.Equal(
            [
                Path.Join("D:", "scoop", "persist", "rustup", ".cargo", "bin"),
                Path.Join("C:", "Users", "tester", ".cargo", "bin"),
            ],
            directories
        );
    }

    [Fact]
    public void GetCargoBinDirectories_PrefersCargoInstallRoot()
    {
        var directories = Cargo.GetCargoBinDirectories(
            Env(
                ("CARGO_INSTALL_ROOT", Path.Join("D:", "tools", "cargo-bins")),
                ("CARGO_HOME", Path.Join("D:", "cargo-home"))
            ),
            Path.Join("C:", "Users", "tester")
        );

        Assert.Equal(
            [
                Path.Join("D:", "tools", "cargo-bins", "bin"),
                Path.Join("D:", "cargo-home", "bin"),
                Path.Join("C:", "Users", "tester", ".cargo", "bin"),
            ],
            directories
        );
    }

    [Fact]
    public void IsCargoBinaryPresent_FindsBinariesUnderCargoHomeWhenNotOnPath()
    {
        string binaryName = OperatingSystem.IsWindows()
            ? "cargo-unigetui-detection-probe.exe"
            : "cargo-unigetui-detection-probe";
        string cargoHome = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
        string binDirectory = Path.Join(cargoHome, "bin");
        string? previous = Environment.GetEnvironmentVariable("CARGO_HOME");

        try
        {
            Assert.False(Cargo.IsCargoBinaryPresent(binaryName));

            Directory.CreateDirectory(binDirectory);
            File.WriteAllText(Path.Join(binDirectory, binaryName), "");
            Environment.SetEnvironmentVariable("CARGO_HOME", cargoHome);

            Assert.True(Cargo.IsCargoBinaryPresent(binaryName));
        }
        finally
        {
            Environment.SetEnvironmentVariable("CARGO_HOME", previous);
            if (Directory.Exists(cargoHome))
                Directory.Delete(cargoHome, true);
        }
    }

    [Fact]
    public void GetCargoBinDirectories_IgnoresBlankValues()
    {
        var directories = Cargo.GetCargoBinDirectories(
            Env(("CARGO_INSTALL_ROOT", ""), ("CARGO_HOME", "   ")),
            ""
        );

        Assert.Empty(directories);
    }
}
