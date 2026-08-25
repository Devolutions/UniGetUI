#if WINDOWS
using System.Diagnostics;
using UniGetUI.PackageEngine.Managers.ScoopManager;
using UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

namespace UniGetUI.PackageEngine.Tests;

public sealed class ScoopProcessTests
{
    private const int StdErrLines = 2000;
    private const int StdErrLineLength = 80;
    private const int PipeBufferBytes = 4096;
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public void ReadLinesReturnsStdOutWhenTheProcessFloodsStdErr()
    {
        using Process p = StartNoisyProcess();
        TestProcessTaskLogger logger = new();

        var task = Task.Run(() => ScoopProcess.ReadLines(p, logger));
        AssertCompleted(task, p);

        Assert.Equal(["pkg-a 1.0.0 main", "pkg-b 2.0.0 extras"], task.Result);
        Assert.Equal(["pkg-a 1.0.0 main", "pkg-b 2.0.0 extras"], logger.StdOut);
        Assert.True(
            logger.StdErr.Sum(entry => entry.Length) > PipeBufferBytes,
            "The process did not write more than a pipe buffer to standard error"
        );
        Assert.Equal(0, logger.ReturnCode);
    }

    [Fact]
    public void ReadToEndReturnsStdOutWhenTheProcessFloodsStdErr()
    {
        using Process p = StartNoisyProcess();
        TestProcessTaskLogger logger = new();

        var task = Task.Run(() => ScoopProcess.ReadToEnd(p, logger));
        AssertCompleted(task, p);

        Assert.Contains("pkg-a 1.0.0 main", task.Result);
        Assert.Contains("pkg-b 2.0.0 extras", task.Result);
        Assert.Equal(0, logger.ReturnCode);
    }

    private static void AssertCompleted(Task task, Process p)
    {
        if (task.Wait(Patience))
            return;

        try
        {
            p.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
        }

        Assert.Fail(
            $"Reading the output of the process did not finish after {Patience.TotalSeconds} seconds"
        );
    }

    private static Process StartNoisyProcess()
    {
        Process p = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments =
                    "-NoProfile -ExecutionPolicy Bypass -Command \"$e = [Console]::Error; "
                    + $"1..{StdErrLines} | ForEach-Object {{ $e.WriteLine('x' * {StdErrLineLength}) }}; "
                    + "'pkg-a 1.0.0 main'; 'pkg-b 2.0.0 extras'\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
            },
        };

        p.Start();
        return p;
    }
}
#endif
