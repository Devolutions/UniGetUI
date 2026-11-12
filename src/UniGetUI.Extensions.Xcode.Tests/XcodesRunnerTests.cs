using System.ComponentModel;
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
    public async Task RunAsync_RedactsFailureOutputAndStandardError()
    {
        if (!HasShell)
            return;
        using var script = new FakeXcodesScript("""
            echo "Set-Cookie: session=private" >&2
            echo "Request failed https://example.test?token=private" >&2
            exit 1
            """);
        XcodesResult result = await new XcodesRunner(script.Path).RunAsync("update");

        Assert.Contains("Set-Cookie: [redacted]", result.StandardError);
        Assert.Contains("[URL redacted]", result.StandardError);
        Assert.DoesNotContain("session=private", result.Output);
        Assert.DoesNotContain("token=private", result.Output);
    }

    [Fact]
    public async Task RunAsync_DoesNotRequireTheCallersSynchronizationContext()
    {
        if (!HasShell)
            return;
        using var script = new FakeXcodesScript("sleep 0.2\nprintf '26.4 (17E192)\\n'\n");
        var context = new RecordingSynchronizationContext();
        SynchronizationContext? previous = SynchronizationContext.Current;
        Task<XcodesResult> operation;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            operation = new XcodesRunner(script.Path).RunAsync("list");
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        XcodesResult result = await operation.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.True(result.Success);
        Assert.Equal(0, context.Posts);
    }

    [Theory]
    [InlineData(false, "standard output")]
    [InlineData(true, "standard error")]
    public async Task RunAsync_StopsWhenEitherPipeExceedsItsLimit(bool standardError, string pipe)
    {
        if (!HasShell)
            return;
        string redirect = standardError ? " >&2" : "";
        using var script = new FakeXcodesScript(
            $"head -c {XcodesRunner.MaximumOutputCharacters + 1} /dev/zero{redirect}\nsleep 30\n");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var clock = Stopwatch.StartNew();

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => new XcodesRunner(script.Path)
            .RunAsync(["list"], TimeSpan.FromSeconds(30), cancellation.Token));

        Assert.Contains(pipe, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("limit", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(12), $"It took {clock.Elapsed}.");
    }

    [Fact]
    public async Task RunAsync_AcceptsCompleteOutputAtTheLimit()
    {
        if (!HasShell)
            return;
        using var script = new FakeXcodesScript($"head -c {XcodesRunner.MaximumOutputCharacters} /dev/zero\n");

        XcodesResult result = await new XcodesRunner(script.Path).RunAsync("list");

        Assert.True(result.Success);
        Assert.Equal(XcodesRunner.MaximumOutputCharacters, result.StandardOutput.Length);
    }

    [Theory]
    [InlineData(false, "standard output")]
    [InlineData(true, "standard error")]
    public async Task RunAsync_ReportsAnOversizedPipeEvenWhenTheCommandExitsImmediately(bool standardError, string pipe)
    {
        if (!HasShell)
            return;
        string redirect = standardError ? " >&2" : "";
        using var script = new FakeXcodesScript(
            $"head -c {XcodesRunner.MaximumOutputCharacters + 1} /dev/zero{redirect}\n");

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(
            () => new XcodesRunner(script.Path).RunAsync("list"));

        Assert.Contains(pipe, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("limit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_DoesNotTreatAnUnclosedPipeAsACompleteListing()
    {
        if (!HasShell)
            return;
        using var script = new FakeXcodesScript(
            "sleep 30 &\nprintf '%s' \"$!\" > \"$2\"\nprintf '26.4 (17E192)\\n'\nexit 0\n");
        string pidPath = Path.Combine(script.Folder, "child.pid");

        try
        {
            XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => new XcodesRunner(script.Path)
                .RunAsync(["list", pidPath], TimeSpan.FromSeconds(20), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(12)));

            Assert.Contains("output", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("incomplete", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(pidPath) && int.TryParse(await File.ReadAllTextAsync(pidPath), out int pid))
            {
                try
                {
                    using Process child = Process.GetProcessById(pid);
                    if (child.ProcessName == "sleep")
                        child.Kill(entireProcessTree: true);
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
        }
    }

    [Fact]
    public async Task RunAsync_PassesEachArgumentAsOne()
    {
        if (!HasShell)
            return;
        using var script = new FakeXcodesScript("for argument in \"$@\"; do echo \"[$argument]\"; done\n");

        XcodesResult result = await new XcodesRunner(script.Path).RunAsync("uninstall", "26.1 Beta 2");

        Assert.True(result.Success);
        Assert.Equal(["[uninstall]", "[26.1 Beta 2]"], result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries));
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
    public void Kill_ToleratesWhatStoppingAProcessTreeCanThrow()
    {
        XcodesRunner.Kill(() => throw new InvalidOperationException("The process has exited."));
        XcodesRunner.Kill(() => throw new Win32Exception(1, "Operation not permitted"));
        XcodesRunner.Kill(() => throw new AggregateException(
            "The process tree was not completely terminated.", new Win32Exception(1, "Operation not permitted")));
    }

    [Fact]
    public void Kill_DoesNotHideAnythingElse()
        => Assert.Throws<ArgumentException>(() => XcodesRunner.Kill(() => throw new ArgumentException("A bug.")));

    [Fact]
    public async Task Kill_StopsARunningProcess()
    {
        if (!HasShell)
            return;
        using Process process = Process.Start(new ProcessStartInfo("sleep", "60") { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException("sleep did not start.");

        XcodesRunner.Kill(process);

        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(safety.Token);
        Assert.True(process.HasExited);
    }

    [Fact]
    public async Task Kill_AcceptsAProcessThatHasAlreadyExited()
    {
        if (!HasShell)
            return;
        using Process process = Process.Start(new ProcessStartInfo("true") { UseShellExecute = false, CreateNoWindow = true })
            ?? throw new InvalidOperationException("true did not start.");
        using var safety = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await process.WaitForExitAsync(safety.Token);

        XcodesRunner.Kill(process);

        Assert.True(process.HasExited);
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
