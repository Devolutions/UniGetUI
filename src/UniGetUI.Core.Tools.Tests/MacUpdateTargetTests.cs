using System.Diagnostics;

namespace UniGetUI.Core.Tools.Tests;

public class MacUpdateTargetTests
{
    private const string TranslocatedBundle =
        "/private/var/folders/76/pyfy9ch97zg773pw3v188r980000gn/T/AppTranslocation/A19F29BA-B27D-440A-821C-E438AF47F812/d/UniGetUI.app";

    private const string TranslocatedExecutable = TranslocatedBundle + "/Contents/MacOS/UniGetUI";

    private static MacUpdateTarget Resolve(
        string? executablePath,
        string? translocatedOriginal,
        string[] existingDirectories,
        string[] writableDirectories) =>
        MacUpdateTargetResolver.Resolve(
            executablePath,
            _ => translocatedOriginal,
            existingDirectories.Contains,
            writableDirectories.Contains);

    [Fact]
    public void Resolve_UpdatesTheOriginalBundleWhenRunningFromATranslocatedCopy()
    {
        MacUpdateTarget target = Resolve(
            TranslocatedExecutable,
            "/Applications/UniGetUI.app",
            [TranslocatedBundle, "/Applications/UniGetUI.app"],
            ["/Applications", "/Applications/UniGetUI.app"]);

        Assert.Equal(MacUpdateTargetStatus.Ready, target.Status);
        Assert.Equal("/Applications/UniGetUI.app", target.BundlePath);
        Assert.Equal(TranslocatedBundle, target.RunningBundlePath);
    }

    [Fact]
    public void Resolve_UpdatesTheOriginalBundleOutsideApplicationsWhenTheSystemReportsIt()
    {
        MacUpdateTarget target = Resolve(
            TranslocatedExecutable,
            "/Users/me/Apps/UniGetUI.app",
            [TranslocatedBundle, "/Users/me/Apps/UniGetUI.app", "/Applications/UniGetUI.app"],
            ["/Users/me/Apps", "/Users/me/Apps/UniGetUI.app"]);

        Assert.Equal(MacUpdateTargetStatus.Ready, target.Status);
        Assert.Equal("/Users/me/Apps/UniGetUI.app", target.BundlePath);
    }

    [Fact]
    public void Resolve_FallsBackToApplicationsWhenTheTranslocatedOriginalCannotBeResolved()
    {
        MacUpdateTarget target = Resolve(
            TranslocatedExecutable,
            null,
            [TranslocatedBundle, "/Applications/UniGetUI.app"],
            ["/Applications", "/Applications/UniGetUI.app"]);

        Assert.Equal(MacUpdateTargetStatus.Ready, target.Status);
        Assert.Equal("/Applications/UniGetUI.app", target.BundlePath);
    }

    [Fact]
    public void Resolve_RefusesToUpdateATranslocatedCopyWithNoOriginalToReplace()
    {
        MacUpdateTarget target = Resolve(
            TranslocatedExecutable,
            null,
            [TranslocatedBundle],
            [TranslocatedBundle]);

        Assert.Equal(MacUpdateTargetStatus.TranslocatedUnresolved, target.Status);
    }

    [Fact]
    public void Resolve_ReportsARootOwnedBundleAsNotWritable()
    {
        MacUpdateTarget target = Resolve(
            "/Applications/UniGetUI.app/Contents/MacOS/UniGetUI",
            null,
            ["/Applications/UniGetUI.app"],
            ["/Applications"]);

        Assert.Equal(MacUpdateTargetStatus.NotWritable, target.Status);
        Assert.Equal("/Applications/UniGetUI.app", target.BundlePath);
    }

    [Fact]
    public void Resolve_ReportsAnUnwritableParentFolderAsNotWritable()
    {
        MacUpdateTarget target = Resolve(
            "/Applications/UniGetUI.app/Contents/MacOS/UniGetUI",
            null,
            ["/Applications/UniGetUI.app"],
            ["/Applications/UniGetUI.app"]);

        Assert.Equal(MacUpdateTargetStatus.NotWritable, target.Status);
    }

    [Fact]
    public void Resolve_IsReadyForAWritableInstalledBundle()
    {
        MacUpdateTarget target = Resolve(
            "/Applications/UniGetUI.app/Contents/MacOS/UniGetUI",
            null,
            ["/Applications/UniGetUI.app"],
            ["/Applications", "/Applications/UniGetUI.app"]);

        Assert.Equal(MacUpdateTargetStatus.Ready, target.Status);
        Assert.Equal("/Applications/UniGetUI.app", target.BundlePath);
    }

    [Fact]
    public void Resolve_FallsBackToApplicationsWhenNotRunningFromABundle()
    {
        MacUpdateTarget target = Resolve(
            "/usr/local/bin/unigetui",
            null,
            [],
            []);

        Assert.Equal(MacUpdateTargetStatus.BundleNotFound, target.Status);
        Assert.Equal(MacUpdateTargetResolver.DefaultInstallPath, target.BundlePath);
        Assert.Null(target.RunningBundlePath);
    }

    [Theory]
    [InlineData("/Applications/UniGetUI.app/Contents/MacOS/UniGetUI", "/Applications/UniGetUI.app")]
    [InlineData(TranslocatedExecutable, TranslocatedBundle)]
    [InlineData("/usr/local/bin/unigetui", null)]
    [InlineData(null, null)]
    public void FindEnclosingAppBundle_ReturnsTheInnermostBundle(string? executable, string? expected)
    {
        Assert.Equal(expected, MacUpdateTargetResolver.FindEnclosingAppBundle(executable));
    }

    [Fact]
    public void TryGetSwapFailure_ReadsTheBundleTheHelperCouldNotReplace()
    {
        string log =
            "Target version: 2026.1.0\n"
            + "[INFO ] Replacing /Applications/UniGetUI.app with the freshly-extracted bundle and relaunching.\n"
            + "\n"
            + MacUpdateTargetResolver.SwapFailedMarker + " /Applications/UniGetUI.app\n";

        Assert.True(MacUpdateTargetResolver.TryGetSwapFailure(log, out string bundle));
        Assert.Equal("/Applications/UniGetUI.app", bundle);
    }

    [Fact]
    public void TryGetSwapFailure_IgnoresALogWithoutTheMarker()
    {
        Assert.False(MacUpdateTargetResolver.TryGetSwapFailure("Target version: 2026.1.0\n", out _));
    }

    [MacOSFact]
    public void ElevatedSwapScript_ReplacesTheBundleAndDeletesTheOldOneOnceTheAppHasExited()
    {
        string root = Directory.CreateTempSubdirectory("unigetui-swap-").FullName;
        try
        {
            (string target, string newApp) = CreateBundles(root);

            Assert.Equal(0, RunElevatedSwapScript(target, newApp, ExitedProcessId()));

            Assert.Equal("new", File.ReadAllText(Path.Join(target, "version")));
            Assert.False(Directory.Exists(newApp));
            Assert.True(SpinWait.SpinUntil(() => !Directory.Exists(target + ".old"), TimeSpan.FromSeconds(5)));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [MacOSFact]
    public void ElevatedSwapScript_KeepsTheOldBundleWhileTheAppIsStillRunning()
    {
        string root = Directory.CreateTempSubdirectory("unigetui-swap-").FullName;
        try
        {
            (string target, string newApp) = CreateBundles(root);

            Assert.Equal(0, RunElevatedSwapScript(target, newApp, Environment.ProcessId));

            Assert.Equal("new", File.ReadAllText(Path.Join(target, "version")));
            Assert.Equal("old", File.ReadAllText(Path.Join(target + ".old", "version")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [MacOSFact]
    public void ElevatedSwapScript_LeavesTheBundleAloneWhenTheNewOneIsMissing()
    {
        string root = Directory.CreateTempSubdirectory("unigetui-swap-").FullName;
        try
        {
            (string target, string newApp) = CreateBundles(root);
            Directory.Delete(newApp, recursive: true);

            Assert.NotEqual(0, RunElevatedSwapScript(target, newApp, ExitedProcessId()));

            Assert.Equal("old", File.ReadAllText(Path.Join(target, "version")));
            Assert.False(Directory.Exists(target + ".old"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static (string Target, string NewApp) CreateBundles(string root)
    {
        string target = Path.Join(root, "Applications", "UniGetUI.app");
        string newApp = Path.Join(root, "staging", "UniGetUI.app");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(newApp);
        File.WriteAllText(Path.Join(target, "version"), "old");
        File.WriteAllText(Path.Join(newApp, "version"), "new");
        return (target, newApp);
    }

    private static int RunElevatedSwapScript(string target, string newApp, int pid)
    {
        var startInfo = new ProcessStartInfo("/bin/sh") { UseShellExecute = false };
        foreach (string arg in new[] { "-c", MacUpdateTargetResolver.ElevatedSwapScript, "sh", target, newApp, pid.ToString() })
        {
            startInfo.ArgumentList.Add(arg);
        }

        using Process process = Process.Start(startInfo)!;
        process.WaitForExit();
        return process.ExitCode;
    }

    private static int ExitedProcessId()
    {
        using Process process = Process.Start("/usr/bin/true");
        process.WaitForExit();
        return process.Id;
    }
}
