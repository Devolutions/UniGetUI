#if WINDOWS
using System.Diagnostics;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.PackageEngine.Managers.NpmManager;
using UniGetUI.PackageEngine.Managers.PowerShell7Manager;
using UniGetUI.PackageEngine.Managers.PowerShellManager;
using UniGetUI.PackageEngine.Managers.ScoopManager;

namespace UniGetUI.PackageEngine.Tests;

/// <summary>
/// Checks the launch wiring on the real managers rather than on a test double, so that a wrong
/// launcher path or a missing override shows up here instead of when a user runs an install.
/// </summary>
public sealed class OperationCallArgsWiringTests : IDisposable
{
    private readonly string _testRoot;

    public OperationCallArgsWiringTests()
    {
        _testRoot = Path.Combine(
            AppContext.BaseDirectory,
            nameof(OperationCallArgsWiringTests),
            Guid.NewGuid().ToString("N")
        );
        string secureSettingsRoot = Path.Combine(_testRoot, "SecureSettings");

        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        SecureSettings.TEST_SecureSettingsRootOverride = secureSettingsRoot;

        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Directory.CreateDirectory(secureSettingsRoot);

        Settings.ResetSettings();
        Settings.SetDictionary(Settings.K.DisabledManagers, new Dictionary<string, bool>());
        Settings.SetDictionary(Settings.K.ManagerPaths, new Dictionary<string, string>());
    }

    public void Dispose()
    {
        CoreData.TEST_DataDirectoryOverride = null;
        SecureSettings.TEST_SecureSettingsRootOverride = null;
        Settings.ResetSettings();
        try
        {
            if (Directory.Exists(_testRoot))
                Directory.Delete(_testRoot, true);
        }
        catch (IOException) { }
    }

    [Fact]
    public void TheLauncherPathResolvesToAFileThatExists()
    {
        Assert.True(
            File.Exists(CoreData.PowerShellOperationLauncher),
            $"The launcher was not found at {CoreData.PowerShellOperationLauncher}."
        );
    }

    [Theory]
    [InlineData("tls12")]
    [InlineData("plain")]
    public void ThePowerShellManagersLaunchTheCheckedInLauncherWithFile(string mode)
    {
        var manager = mode == "tls12" ? new PowerShell() : (object)new PowerShell7();
        var initialized = manager as UniGetUI.PackageEngine.ManagerClasses.Manager.PackageManager;
        initialized!.Initialize();

        // powershell.exe always exists on Windows, so a not-found PowerShell 5 manager means the
        // wiring itself is broken rather than the environment lacking the tool.
        if (mode == "tls12")
            Assert.True(initialized.Status.Found, "The Windows PowerShell manager was not found.");
        else if (!initialized.Status.Found)
            return;

        var vector = initialized.Status.OperationCallArgs;

        Assert.NotEmpty(vector);
        Assert.DoesNotContain("-Command", vector);
        Assert.Contains("-File", vector);
        Assert.Equal(CoreData.PowerShellOperationLauncher, vector[vector.ToList().IndexOf("-File") + 1]);
        Assert.Equal(mode, vector[^1]);
        Assert.True(File.Exists(vector[vector.ToList().IndexOf("-File") + 1]));
    }

    [Fact]
    public void ScoopLaunchesItsOwnScriptWithFileWhenPresent()
    {
        var manager = new Scoop();
        manager.Initialize();

        if (!manager.Status.Found)
            return;

        var vector = manager.Status.OperationCallArgs;

        Assert.NotEmpty(vector);
        Assert.DoesNotContain("-Command", vector);
        Assert.Contains("-File", vector);
        string script = vector[vector.ToList().IndexOf("-File") + 1];
        Assert.EndsWith(".ps1", script, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(script));
    }

    [Fact]
    public void NpmEitherLaunchesItsOwnScriptOrKeepsTheLegacyForm()
    {
        var manager = new Npm();
        manager.Initialize();

        if (!manager.Status.Found)
            return;

        var vector = manager.Status.OperationCallArgs;

        if (vector.Count is 0)
        {
            Assert.False(string.IsNullOrWhiteSpace(manager.Status.ExecutableCallArgs));
            return;
        }

        Assert.DoesNotContain("-Command", vector);
        Assert.Contains("-File", vector);
        string script = vector[vector.ToList().IndexOf("-File") + 1];
        Assert.EndsWith(".ps1", script, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(script));
    }

    [Fact]
    public void NpmResolvesThePowerShellEntryPointBesideTheShim()
    {
        string directory = Path.Combine(_testRoot, "node");
        Directory.CreateDirectory(directory);
        string shim = Path.Combine(directory, "npm.cmd");
        File.WriteAllText(shim, "");

        Assert.Null(Npm.ResolvePowerShellEntryPoint(shim));

        string script = Path.Combine(directory, "npm.ps1");
        File.WriteAllText(script, "");

        Assert.Equal(script, Npm.ResolvePowerShellEntryPoint(shim));
    }

    // .NET refuses to start a process when both Arguments and ArgumentList carry a value, so the
    // vector path has to clear the string form. This starts a real process to prove it does.
    [Fact]
    public void AProcessStartsWhenTheArgumentVectorReplacesTheArgumentString()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = CoreData.PowerShell5,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            Arguments = "lol",
        };

        startInfo.Arguments = string.Empty;
        startInfo.ArgumentList.Clear();
        foreach (string argument in new[] { "-NoProfile", "-Command", "Write-Output vector-ok" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        string output = process.StandardOutput.ReadToEnd();
        Assert.True(process.WaitForExit(30_000));

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("vector-ok", output);
    }
}
#endif
