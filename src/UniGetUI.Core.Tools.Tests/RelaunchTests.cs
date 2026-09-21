using System.Diagnostics;
using UniGetUI.Core.Tools;

namespace UniGetUI.Core.Tools.Tests;

public class RelaunchTests
{
    [Fact]
    public void CreateRelaunchStartInfo_OnWindows_WaitsAndStartsThroughPowerShell()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        ProcessStartInfo startInfo = CoreTools.CreateRelaunchStartInfo(
            4242,
            @"C:\Users\O'Brien\UniGetUI\UniGetUI.exe"
        );

        Assert.Equal("powershell.exe", startInfo.FileName);
        Assert.Equal(
            "-NoProfile -WindowStyle Hidden -Command \"Wait-Process -Id 4242; "
                + @"Start-Process -FilePath 'C:\Users\O''Brien\UniGetUI\UniGetUI.exe'""",
            startInfo.Arguments
        );
        Assert.Empty(startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
    }

    [Fact]
    public void CreateRelaunchStartInfo_OffWindows_WaitsForThePidInAShellHelper()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        ProcessStartInfo startInfo = CoreTools.CreateRelaunchStartInfo(4242, "/opt/unigetui/UniGetUI");

        Assert.Equal("/bin/sh", startInfo.FileName);
        Assert.Equal("", startInfo.Arguments);
        Assert.Equal(6, startInfo.ArgumentList.Count);
        Assert.Equal("-c", startInfo.ArgumentList[0]);
        Assert.Contains("kill -0 \"$pid\"", startInfo.ArgumentList[1]);
        Assert.Contains("\"$exe\" >/dev/null 2>&1 &", startInfo.ArgumentList[1]);
        Assert.Contains("/usr/bin/open -na \"$bundle\"", startInfo.ArgumentList[1]);
        Assert.Equal("sh", startInfo.ArgumentList[2]);
        Assert.Equal("4242", startInfo.ArgumentList[3]);
        Assert.Equal("/opt/unigetui/UniGetUI", startInfo.ArgumentList[4]);
        Assert.Equal("", startInfo.ArgumentList[5]);
        Assert.False(startInfo.UseShellExecute);
    }

    [Fact]
    public void CreateRelaunchStartInfo_OnMacOs_PassesTheBundleToTheHelper()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        ProcessStartInfo startInfo = CoreTools.CreateRelaunchStartInfo(
            4242,
            "/Applications/UniGetUI.app/Contents/MacOS/UniGetUI"
        );

        Assert.Equal("/Applications/UniGetUI.app", startInfo.ArgumentList[5]);
    }

    [Theory]
    [InlineData("/Applications/UniGetUI.app/Contents/MacOS/UniGetUI", "UniGetUI.app")]
    [InlineData("/Users/me/My Apps/UniGetUI.app/Contents/MacOS/UniGetUI", "UniGetUI.app")]
    public void FindAppBundle_ReturnsTheBundleThatContainsTheExecutable(string executable, string bundleName)
    {
        string? bundle = CoreTools.FindAppBundle(executable);

        Assert.NotNull(bundle);
        Assert.Equal(bundleName, Path.GetFileName(bundle));
        Assert.EndsWith(bundleName, bundle);
    }

    [Theory]
    [InlineData("/opt/unigetui/UniGetUI")]
    [InlineData("/home/me/UniGetUI.app.bak/bin/UniGetUI")]
    [InlineData("UniGetUI")]
    public void FindAppBundle_ReturnsNullOutsideABundle(string executable)
    {
        Assert.Null(CoreTools.FindAppBundle(executable));
    }

    // The real helper: a sleeping child stands in for the exiting UniGetUI, and the "executable" is
    // a script that leaves a marker. The marker must not appear while the child is alive.
    [Fact]
    public async Task RelaunchHelper_StartsTheExecutableOnlyAfterTheProcessExits()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), "unigetui-relaunch-" + Path.GetRandomFileName());
        Directory.CreateDirectory(directory);
        string marker = Path.Combine(directory, "relaunched");
        string target = Path.Combine(directory, "target.sh");
        File.WriteAllText(target, $"#!/bin/sh\ntouch \"{marker}\"\n");
        File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var standIn = Process.Start(new ProcessStartInfo("sleep", "1.5") { UseShellExecute = false })!;
        try
        {
            using var helper = Process.Start(CoreTools.CreateRelaunchStartInfo(standIn.Id, target));
            Assert.NotNull(helper);

            await Task.Delay(500);
            Assert.False(standIn.HasExited);
            Assert.False(File.Exists(marker), "the helper must not start the executable while the process is alive");

            await standIn.WaitForExitAsync();
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!File.Exists(marker) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(100);
            }

            Assert.True(File.Exists(marker), "the helper did not start the executable after the process exited");
            await helper.WaitForExitAsync();
            Assert.Equal(0, helper.ExitCode);
        }
        finally
        {
            if (!standIn.HasExited)
            {
                standIn.Kill();
            }

            Directory.Delete(directory, recursive: true);
        }
    }
}
