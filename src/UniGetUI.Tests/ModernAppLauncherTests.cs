using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;

namespace UniGetUI.Tests;

public sealed class ModernAppLauncherTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        nameof(ModernAppLauncherTests),
        Guid.NewGuid().ToString("N")
    );

    public ModernAppLauncherTests()
    {
        Directory.CreateDirectory(_testRoot);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Settings.ResetSettings();
    }

    public void Dispose()
    {
        Settings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    [Fact]
    public void ClassicModeDefaultsToEnabled()
    {
        Assert.True(ModernAppLauncher.IsClassicModeEnabled());

        Settings.Set(Settings.K.DisableClassicMode, true);

        Assert.False(ModernAppLauncher.IsClassicModeEnabled());
    }

    [Fact]
    public void BetaTestersDefaultToModernUI()
    {
        Assert.True(ModernAppLauncher.IsClassicModeEnabled());

        Settings.Set(Settings.K.EnableUniGetUIBeta, true);

        Assert.False(ModernAppLauncher.IsClassicModeEnabled());
    }

    [Fact]
    public void ResolveModernExecutablePath_PrefersRootExecutable()
    {
        string baseDirectory = Path.Combine(_testRoot, "Launcher");
        Directory.CreateDirectory(baseDirectory);

        string expected = Path.Combine(baseDirectory, ModernAppLauncher.ModernAppExecutableName);
        File.WriteAllText(expected, "");

        string avaloniaDirectory = Path.Combine(baseDirectory, ModernAppLauncher.ModernAppDirectoryName);
        Directory.CreateDirectory(avaloniaDirectory);
        File.WriteAllText(Path.Combine(avaloniaDirectory, ModernAppLauncher.ModernAppExecutableName), "");

        Assert.Equal(expected, ModernAppLauncher.ResolveModernExecutablePath(baseDirectory));
    }

    [Fact]
    public void ResolveModernExecutablePath_FallsBackToAvaloniaSubdirectory()
    {
        string baseDirectory = Path.Combine(_testRoot, "Launcher");
        string avaloniaDirectory = Path.Combine(baseDirectory, ModernAppLauncher.ModernAppDirectoryName);
        Directory.CreateDirectory(avaloniaDirectory);

        string expected = Path.Combine(
            avaloniaDirectory,
            ModernAppLauncher.ModernAppExecutableName
        );
        File.WriteAllText(expected, "");

        Assert.Equal(expected, ModernAppLauncher.ResolveModernExecutablePath(baseDirectory));
    }

    [Fact]
    public void ResolveModernExecutablePath_FindsDevelopmentBuildOutput()
    {
        string baseDirectory = Path.Combine(
            _testRoot,
            "UniGetUI",
            "bin",
            "x64",
            "Debug",
            "net10.0-windows10.0.26100.0"
        );
        Directory.CreateDirectory(baseDirectory);

        string expected = Path.Combine(
            _testRoot,
            "UniGetUI.Avalonia",
            "bin",
            "x64",
            "Debug",
            "net10.0-windows10.0.26100.0",
            ModernAppLauncher.ModernAppExecutableName
        );
        Directory.CreateDirectory(Path.GetDirectoryName(expected)!);
        File.WriteAllText(expected, "");

        Assert.Equal(expected, ModernAppLauncher.ResolveModernExecutablePath(baseDirectory));
    }

    [Fact]
    public void CreateStartInfo_PreservesArguments()
    {
        string executable = Path.Combine(_testRoot, ModernAppLauncher.ModernAppExecutableName);
        string[] args = ["--daemon", "--set-setting-value", "FreshValue", "value with spaces"];

        var startInfo = ModernAppLauncher.CreateStartInfo(executable, args);

        Assert.Equal(executable, startInfo.FileName);
        Assert.Equal(_testRoot, startInfo.WorkingDirectory);
        Assert.Equal(args, startInfo.ArgumentList);
    }
}
