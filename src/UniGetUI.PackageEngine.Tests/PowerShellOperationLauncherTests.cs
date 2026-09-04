#if WINDOWS
using System.Diagnostics;
using UniGetUI.Core.Tools;

namespace UniGetUI.PackageEngine.Tests;

/// <summary>
/// Drives the checked-in operation launcher through the real powershell.exe using the same
/// ArgumentList mechanism the app uses, so the -File launch path is verified end to end rather
/// than only in theory.
/// </summary>
public sealed class PowerShellOperationLauncherTests
{
    private static string LauncherPath()
    {
        string fromOutput = Path.Combine(
            AppContext.BaseDirectory,
            "Assets",
            "Utilities",
            "unigetui_ps_operation.ps1"
        );
        if (File.Exists(fromOutput))
            return fromOutput;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "SharedAssets",
                "Assets",
                "Utilities",
                "unigetui_ps_operation.ps1"
            );
            if (File.Exists(candidate))
                return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException("The operation launcher script was not found.");
    }

    private static string PowerShellPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe"
        );

    private sealed record Result(int ExitCode, string StdOut, string StdErr);

    private static Result Run(params string[] operationParameters)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = PowerShellPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
        };

        foreach (
            string argument in new[]
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                LauncherPath(),
            }
        )
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (string argument in operationParameters)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        process.StandardInput.Close();

        // Drained while waiting rather than before it: reading to the end first blocks until the
        // child closes the stream, which would make the timeout unreachable, and leaving the other
        // pipe unread would deadlock a child that fills it.
        Task<string> stdOut = process.StandardOutput.ReadToEndAsync();
        Task<string> stdErr = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(60_000))
        {
            process.Kill(true);
            Assert.Fail("The launcher did not exit in time.");
        }

        Assert.True(Task.WhenAll(stdOut, stdErr).Wait(30_000), "The launcher output was not read.");
        return new Result(process.ExitCode, stdOut.Result, stdErr.Result);
    }

    [Fact]
    public void TheLauncherIsDeployedNextToTheApplication()
    {
        Assert.True(File.Exists(LauncherPath()));
    }

    [Fact]
    public void NamedParametersBindThroughTheSplat()
    {
        var result = Run("plain", "Get-ChildItem", "-Path", @"C:\Windows", "-Filter", "explorer.exe", "-Name");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("explorer.exe", result.StdOut, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(result.StdErr.Trim());
    }

    // powershell.exe splits "-Confirm:$false" into "-Confirm" and the literal text "$false"
    // before the launcher runs, and splatting cannot bind that text to a switch. The launcher
    // converts the pair back into a real boolean; without that, the operation silently fails
    // with "a positional parameter cannot be found".
    [Fact]
    public void TheColonSwitchSyntaxTheHelpersEmitIsAccepted()
    {
        string target = Path.Combine(
            Path.GetTempPath(),
            $"unigetui_confirm_{Guid.NewGuid():N}.txt"
        );

        try
        {
            var result = Run(
                "plain",
                "New-Item",
                "-Path",
                target,
                "-ItemType",
                "File",
                "-Force",
                "-Confirm:$false"
            );

            Assert.Equal(0, result.ExitCode);
            Assert.DoesNotContain("positional parameter", result.StdErr);
            Assert.True(File.Exists(target), "The cmdlet did not run with -Confirm:$false bound.");
        }
        finally
        {
            File.Delete(target);
        }
    }

    [Fact]
    public void APlainSwitchWithoutAValueStillBinds()
    {
        string target = Path.Combine(Path.GetTempPath(), $"unigetui_force_{Guid.NewGuid():N}.txt");

        try
        {
            var result = Run("plain", "New-Item", "-Path", target, "-ItemType", "File", "-Force");

            Assert.Equal(0, result.ExitCode);
            Assert.True(File.Exists(target));
        }
        finally
        {
            File.Delete(target);
        }
    }

    [Fact]
    public void AStatementSeparatorInAnArgumentIsTreatedAsData()
    {
        string marker = Path.Combine(
            Path.GetTempPath(),
            $"unigetui_launcher_{Guid.NewGuid():N}.txt"
        );

        var result = Run(
            "plain",
            "Write-Output",
            $"1.2.3; New-Item -Path '{marker}' -ItemType File"
        );

        Assert.False(File.Exists(marker), "The injected statement executed.");
        Assert.Contains("1.2.3; New-Item", result.StdOut);
    }

    [Fact]
    public void ASubexpressionInAnArgumentIsTreatedAsData()
    {
        var result = Run("plain", "Write-Output", "1.2.3$(Get-Date)");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("1.2.3$(Get-Date)", result.StdOut);
    }

    [Fact]
    public void ControlValuesCannotBeSmuggledByParameterName()
    {
        var result = Run("plain", "Write-Output", "-mode", "tls12", "-command", "Get-Date");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            ["-mode", "tls12", "-command", "Get-Date"],
            result.StdOut.Split(
                Environment.NewLine,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
            )
        );
    }

    [Fact]
    public void ANonTerminatingErrorBoundToTheErrorVariableExitsNonZero()
    {
        var result = Run(
            "plain",
            "Get-Item",
            "-Path",
            @"C:\unigetui-does-not-exist-9f3a",
            "-ErrorVariable",
            "UniGetUIOperationError"
        );

        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public void ASuccessfulOperationExitsZero()
    {
        var result = Run(
            "plain",
            "Get-Item",
            "-Path",
            @"C:\Windows",
            "-ErrorVariable",
            "UniGetUIOperationError"
        );

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public void Tls12ModeStillRunsTheCommand()
    {
        var result = Run("tls12", "Write-Output", "launcher-ok");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("launcher-ok", result.StdOut);
    }

    [Fact]
    public void AMissingCommandIsRejected()
    {
        var result = Run("plain");

        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void TheLauncherProbeAcceptsTheRealLauncher()
    {
        Assert.True(CoreTools.PowerShellLauncherWorks(PowerShellPath(), LauncherPath()));
    }

    [Fact]
    public void TheLauncherProbeRejectsAMissingLauncher()
    {
        string missing = Path.Combine(
            Path.GetTempPath(),
            $"unigetui_absent_{Guid.NewGuid():N}.ps1"
        );

        Assert.False(CoreTools.PowerShellLauncherWorks(PowerShellPath(), missing));
    }

    [Fact]
    public void TheLauncherProbeRejectsALauncherThatDoesNotBehave()
    {
        string bogus = Path.Combine(Path.GetTempPath(), $"unigetui_bogus_{Guid.NewGuid():N}.ps1");
        File.WriteAllText(bogus, "exit 0" + Environment.NewLine);

        try
        {
            Assert.False(CoreTools.PowerShellLauncherWorks(PowerShellPath(), bogus));
        }
        finally
        {
            File.Delete(bogus);
        }
    }

    // Running under -Command failed the process on a terminating error. -File leaves the script
    // running, so without an explicit catch a failed operation would be reported as a success.
    [Fact]
    public void ATerminatingErrorExitsNonZero()
    {
        var result = Run("plain", "Get-Item", "-NoSuchParameter", "x");

        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public void AParameterSuppliedTwiceExitsNonZero()
    {
        var result = Run(
            "plain",
            "Get-ChildItem",
            "-Path",
            @"C:\Windows",
            "-Force",
            "-Force:$false"
        );

        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public void AnUnknownCommandExitsNonZero()
    {
        var result = Run("plain", "Unigetui-No-Such-Command-9f3a");

        Assert.Equal(1, result.ExitCode);
    }

    // _getOperationResult decides whether to retry without -Scope by matching on the error id and
    // the parameter name. Both have to survive the launcher, or the #5110 retry stops happening.
    [Fact]
    public void TheScopeRetryMarkersSurviveTheLauncher()
    {
        var result = Run("plain", "Get-Item", "-Path", @"C:\Windows", "-Scope", "x");

        string output = result.StdOut + result.StdErr;
        Assert.Contains("NamedParameterNotFound", output);
        Assert.Contains("Scope", output);
        Assert.Equal(1, result.ExitCode);
    }

    [Fact]
    public void TheElevationRetryMarkerSurvivesTheLauncher()
    {
        var result = Run(
            "plain",
            "Write-Error",
            "AdminPrivilegesAreRequired for this operation"
        );

        Assert.Contains("AdminPrivilegesAreRequired", result.StdOut + result.StdErr);
    }
}
#endif
