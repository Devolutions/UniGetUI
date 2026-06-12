using System.Runtime.InteropServices;
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
    public void ClassicModeDefaultsToDisabled()
    {
        Assert.False(ModernAppLauncher.IsClassicModeEnabled());

        Settings.Set(Settings.K.UseClassicMode, true);

        Assert.True(ModernAppLauncher.IsClassicModeEnabled());
    }

    [Fact]
    public void BetaTestersAlwaysUseModernUI()
    {
        Settings.Set(Settings.K.UseClassicMode, true);

        Assert.True(ModernAppLauncher.IsClassicModeEnabled());

        Settings.Set(Settings.K.EnableUniGetUIBeta, true);

        Assert.False(ModernAppLauncher.IsClassicModeEnabled());
    }

    [Fact]
    public void LegacyDisableClassicModeSettingDoesNotEnableClassicMode()
    {
        Settings.Set(Settings.K.DisableClassicMode, true);

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
    public void ResolveModernExecutablePath_PrefersCurrentArchitectureDevelopmentBuildOutput()
    {
        string baseDirectory = Path.Combine(
            _testRoot,
            "UniGetUI",
            "bin",
            "x64",
            "Debug",
            "net10.0-windows10.0.26100.0",
            "win-x64"
        );
        Directory.CreateDirectory(baseDirectory);

        string currentRuntimeIdentifier = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "win-x64",
            Architecture.Arm64 => "win-arm64",
            Architecture.X86 => "win-x86",
            Architecture.Arm => "win-arm",
            _ => "win-x64",
        };
        string otherRuntimeIdentifier = currentRuntimeIdentifier == "win-x64"
            ? "win-arm64"
            : "win-x64";

        string expected = CreateAvaloniaDevelopmentExecutable(currentRuntimeIdentifier);
        string other = CreateAvaloniaDevelopmentExecutable(otherRuntimeIdentifier);

        File.SetLastWriteTimeUtc(expected, DateTime.UtcNow.AddMinutes(-5));
        File.SetLastWriteTimeUtc(other, DateTime.UtcNow);

        Assert.Equal(expected, ModernAppLauncher.ResolveModernExecutablePath(baseDirectory));
    }

    private string CreateAvaloniaDevelopmentExecutable(string runtimeIdentifier)
    {
        string path = Path.Combine(
            _testRoot,
            "UniGetUI.Avalonia",
            "bin",
            "Debug",
            "net10.0-windows10.0.26100.0",
            runtimeIdentifier,
            ModernAppLauncher.ModernAppExecutableName
        );
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        return path;
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
