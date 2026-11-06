using System.Diagnostics;

namespace UniGetUI.Extensions.Xcode.Tests;

/// <summary>Runs a shell script in place of xcodes, so these only run where there is a shell.</summary>
public sealed class XcodesRunnerTests
{
    private static bool HasShell => !OperatingSystem.IsWindows();

    [Fact]
    public async Task RunAsync_ReturnsTheExitCodeAndEachPipe()
    {
        if (!HasShell)
            return;
        using var script = new FakeXcodesScript("echo out-text\necho err-text >&2\nexit 3\n");

        XcodesResult result = await new XcodesRunner(script.Path).RunAsync("list");

        Assert.Equal(3, result.ExitCode);
        Assert.False(result.Success);
        Assert.Equal("out-text", result.StandardOutput.Trim());
        Assert.Equal("err-text", result.StandardError.Trim());
        Assert.Equal("out-text\nerr-text", result.Output);
    }

    [Fact]
    public async Task RunAsync_PassesEachArgumentAsOneAndAsksForNoColour()
    {
        if (!HasShell)
            return;
        using var script = new FakeXcodesScript("for argument in \"$@\"; do echo \"[$argument]\"; done\n");

        XcodesResult result = await new XcodesRunner(script.Path).RunAsync("uninstall", "26.1 Beta 2");

        Assert.True(result.Success);
        Assert.Equal(["[uninstall]", "[26.1 Beta 2]", "[--no-color]"], result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries));
    }

    [Fact]
    public async Task RunAsync_SetsNoColorAndTheCallersEnvironment()
    {
        if (!HasShell)
            return;
        using var script = new FakeXcodesScript("echo \"NO_COLOR=$NO_COLOR\"\necho \"EXTRA=$XCODES_TEST_EXTRA\"\necho \"GONE=${XCODES_TEST_GONE-unset}\"\n");
        string? previous = Environment.GetEnvironmentVariable("XCODES_TEST_GONE");
        Environment.SetEnvironmentVariable("XCODES_TEST_GONE", "present");
        try
        {
            var environment = new Dictionary<string, string?>
            {
                ["XCODES_TEST_EXTRA"] = "yes",
                ["XCODES_TEST_GONE"] = null,
            };

            XcodesResult result = await new XcodesRunner(script.Path, environment).RunAsync("list");

            Assert.Equal(["NO_COLOR=1", "EXTRA=yes", "GONE=unset"], result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
        finally
        {
            Environment.SetEnvironmentVariable("XCODES_TEST_GONE", previous);
        }
    }

    [Fact]
    public async Task RunAsync_ClosesStandardInputSoNothingWaitsForAnAnswer()
    {
        if (!HasShell)
            return;
        using var script = new FakeXcodesScript("if read -r line; then echo \"read:[$line]\"; else echo \"end of input\"; fi\n");

        XcodesResult result = await new XcodesRunner(script.Path).RunAsync("list");

        Assert.Equal("end of input", result.StandardOutput.Trim());
    }

    [Fact]
    public async Task RunAsync_RunsFromTheTemporaryFolder()
    {
        if (!HasShell)
            return;
        // Folders such as /var are links on macOS, so the folder is recognised by a file in it, not by its name.
        string marker = $"xcodes-tests-{Guid.NewGuid():N}";
        string markerPath = Path.Combine(Path.GetTempPath(), marker);
        File.WriteAllText(markerPath, "");
        try
        {
            using var script = new FakeXcodesScript($"if [ -e \"{marker}\" ]; then echo temporary; else echo elsewhere; fi\n");

            XcodesResult result = await new XcodesRunner(script.Path).RunAsync("list");

            Assert.Equal("temporary", result.StandardOutput.Trim());
        }
        finally
        {
            File.Delete(markerPath);
        }
    }

    [Fact]
    public async Task RunAsync_StopsACommandThatOutlivesItsTimeLimit()
    {
        if (!HasShell)
            return;
        using var script = new FakeXcodesScript("sleep 30\n");
        var clock = Stopwatch.StartNew();

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(
            () => new XcodesRunner(script.Path).RunAsync(["list"], TimeSpan.FromMilliseconds(400), CancellationToken.None));

        Assert.Contains("did not finish", error.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"It took {clock.Elapsed}.");
    }

    [Fact]
    public async Task RunAsync_StopsWhenCancelled()
    {
        if (!HasShell)
            return;
        using var script = new FakeXcodesScript("sleep 30\n");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        var clock = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => new XcodesRunner(script.Path).RunAsync(["list"], null, cancellation.Token));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(20), $"It took {clock.Elapsed}.");
    }

    [Fact]
    public async Task RunAsync_ReportsAnExecutableThatCannotBeStarted()
    {
        if (!HasShell)
            return;
        string missing = Path.Combine(Path.GetTempPath(), "xcodes-tests-missing", "xcodes");

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => new XcodesRunner(missing).RunAsync("list"));

        Assert.Contains("Could not start", error.Message);
    }

    [Fact]
    public async Task RunAsync_FindsXcodesItselfWhenNoneWasGiven()
    {
        // Without a file named xcodes on the PATH or in the usual folders there is nothing to run.
        if (XcodesLocator.Find() is not null)
            return;

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => new XcodesRunner().RunAsync("list"));

        Assert.Equal("Could not find xcodes.", error.Message);
    }
}

public sealed class XcodesResultTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(-1, false)]
    public void Success_MeansExitCodeZero(int exitCode, bool success)
        => Assert.Equal(success, new XcodesResult(exitCode, "", "").Success);

    [Fact]
    public void Output_JoinsWhatEachPipeHeld()
    {
        Assert.Equal("out\nerr", new XcodesResult(1, "out\n", "\nerr\n").Output);
        Assert.Equal("out", new XcodesResult(0, "out\n", "").Output);
        Assert.Equal("err", new XcodesResult(1, "  ", "err").Output);
        Assert.Equal("", new XcodesResult(0, "", "").Output);
    }
}
