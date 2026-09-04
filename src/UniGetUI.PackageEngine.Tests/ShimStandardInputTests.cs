#if WINDOWS
using System.Diagnostics;
using UniGetUI.Core.Tools;

namespace UniGetUI.PackageEngine.Tests;

/// <summary>
/// The npm and Scoop PowerShell shims pipe $input to the real program whenever standard input is
/// not a console, and enumerating $input blocks until that pipe is closed. Launching such a shim
/// with a redirected pipe left open therefore hangs forever instead of running the command, which
/// is what a package search through npm did once it was moved to the -File launch path.
/// <para>
/// These tests drive a stand-in shim carrying the same branch through the real powershell.exe, so
/// they reproduce the hang without needing npm or Scoop installed.
/// </para>
/// </summary>
public sealed class ShimStandardInputTests : IDisposable
{
    private readonly string _shimPath = Path.Combine(
        Path.GetTempPath(),
        $"unigetui_shim_{Guid.NewGuid():N}.ps1"
    );

    public ShimStandardInputTests()
    {
        File.WriteAllText(
            _shimPath,
            """
            if ($MyInvocation.ExpectingInput) { $input | Out-Null; "ran:$args" }
            else { "ran:$args" }
            """
        );
    }

    public void Dispose() => File.Delete(_shimPath);

    private static string PowerShellPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe"
        );

    private Process StartShim()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = PowerShellPath(),
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (
            string argument in new[]
            {
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                _shimPath,
                "search",
                "cowsay",
            }
        )
            startInfo.ArgumentList.Add(argument);

        return new Process { StartInfo = startInfo };
    }

    [Fact]
    public async Task AShimRunsWhenTheHelperClosesStandardInput()
    {
        using Process process = StartShim();

        CoreTools.StartAndCloseStandardInput(process);
        Task<string> output = process.StandardOutput.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await process.WaitForExitAsync(timeout.Token);

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("ran:search cowsay", await output);
    }

    [Fact]
    public void AShimHangsWhenStandardInputIsLeftOpen()
    {
        using Process process = StartShim();

        process.Start();
        try
        {
            Assert.False(
                process.WaitForExit(5000),
                "The shim completed with its standard input left open, so the helper is no longer "
                    + "what keeps the npm and Scoop launch paths alive."
            );
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void TheHelperLeavesAProcessWithoutARedirectedStandardInputAlone()
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = PowerShellPath(),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        foreach (string argument in new[] { "-NoProfile", "-Command", "exit 0" })
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };

        CoreTools.StartAndCloseStandardInput(process);

        Assert.True(process.WaitForExit(30000));
        Assert.Equal(0, process.ExitCode);
    }
}
#endif
