using UniGetUI.PackageEngine.Managers.CargoManager;

namespace UniGetUI.PackageEngine.Tests;

[Collection(nameof(OperationOrchestrationTestCollection))]
public sealed class CargoConfigurationTests
{
    [Fact]
    public void GetRootHonorsExplicitEnvironmentConfigurationAndHomePrecedence()
    {
        using var sandbox = new CargoTestSandbox();
        using var environment = sandbox.IsolateEnvironment();
        Assert.Equal(sandbox.PathFor("home"), CargoConfiguration.GetRoot());
        sandbox.WriteText("home/config.toml", "[install]\nroot = 'relative-install'");
        Assert.Equal(sandbox.PathFor("relative-install"), CargoConfiguration.GetRoot());
        using var installRoot = new CargoTestEnvironment(("CARGO_INSTALL_ROOT", sandbox.PathFor("env-root")));
        Assert.Equal(sandbox.PathFor("env-root"), CargoConfiguration.GetRoot());
        Assert.Equal(sandbox.PathFor("explicit-root"), CargoConfiguration.GetRoot(sandbox.PathFor("explicit-root")));
        Assert.Equal(Path.GetFullPath("relative-root", CargoConfiguration.WorkingDirectory),
            CargoConfiguration.GetRoot("relative-root"));
    }

    [Fact]
    public void LegacyConfigWinsAndTomlParserPreservesQuotedAndMultilineValues()
    {
        using var sandbox = new CargoTestSandbox();
        using var environment = sandbox.IsolateEnvironment();
        sandbox.WriteText("home/config.toml", "[install]\nroot = 'ignored'");
        sandbox.WriteText("home/config", """
            [install]
            root = 'legacy path'
            [metadata]
            "key.with.dots" = '''first
            second'''
            """);
        Assert.Equal(sandbox.PathFor("legacy path"), CargoConfiguration.GetRoot());
        var metadata = CargoConfiguration.Table(CargoConfiguration.ReadConfiguration(), "metadata")!;
        Assert.Equal("first\nsecond", CargoConfiguration.String(metadata, "key.with.dots")?.Replace("\r\n", "\n"));
        Assert.Null(CargoConfiguration.Table(metadata, "absent"));
        Assert.Null(CargoConfiguration.String(metadata, "absent"));
    }

    [Fact]
    public void HasBuildOverridesDetectsConfigAndEnvironmentWithoutInventingDefaults()
    {
        using var sandbox = new CargoTestSandbox();
        using var environment = sandbox.IsolateEnvironment();
        Assert.False(CargoConfiguration.HasBuildOverrides());
        sandbox.WriteText("home/config.toml", "[build]\ntarget = 'aarch64-unknown-linux-musl'");
        Assert.True(CargoConfiguration.HasBuildOverrides());
        sandbox.WriteText("home/config.toml", "[build]");
        Assert.False(CargoConfiguration.HasBuildOverrides());
        using var target = new CargoTestEnvironment(("CARGO_BUILD_TARGET", "x86_64-unknown-linux-gnu"));
        Assert.True(CargoConfiguration.HasBuildOverrides());
    }

    [Fact]
    public void InvalidTomlCannotSilentlyBecomeAnEmptyConfiguration()
    {
        Assert.ThrowsAny<Exception>(() => CargoConfiguration.Parse("[install\nroot = 'broken'"));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("registries")]
    [InlineData("registry")]
    [InlineData("target")]
    [InlineData("include")]
    public void CargoSourceRegistryAndTargetConfigurationConservativelyDisablesManagedCompilationBypass(string section)
    {
        using var sandbox = new CargoTestSandbox();
        using var environment = sandbox.IsolateEnvironment();
        sandbox.WriteText("home/config.toml", section == "include"
            ? "include = ['other.toml']" : $"[{section}]\nsetting = 'custom'");
        Assert.True(CargoConfiguration.HasBuildOverrides());
    }

    [Theory]
    [InlineData("wrong-table")]
    [InlineData("wrong-path-type")]
    [InlineData("empty-path")]
    public void InvalidInstallRootConfigurationDoesNotSilentlyUseTheDefault(string scenario)
    {
        using var sandbox = new CargoTestSandbox();
        using var environment = sandbox.IsolateEnvironment();
        sandbox.WriteText("home/config.toml", scenario switch
        {
            "wrong-table" => "install = 42",
            "wrong-path-type" => "[install]\nroot = 42",
            _ => "[install]\nroot = ''",
        });
        Assert.Throws<InvalidDataException>(() => CargoConfiguration.GetRoot());
    }
}
