using System.Diagnostics;

namespace UniGetUI.Extensions.Xcode.Tests;

public sealed class XcodeSystemProcessRunnerTests
{
    [Fact]
    public async Task StreamsBoundedRedactedOutputWithoutPassingXcodesCredentials()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var script = new FakeXcodesScript("""
            printf 'arg:%s\n' "$@"
            printf 'credentials:%s/%s\n' "${XCODES_USERNAME-unset}" "${XCODES_PASSWORD-unset}"
            printf '\033[31mCookie: session-secret\033[0m\n'
            printf 'https://example.test/download?token=secret\n'
            printf 'Downloading: 1.0%%\rDownloading: 2.0%%\n'
            printf 'diagnostic on stderr\n' >&2
            read -r line && printf 'unexpected input\n' || printf 'stdin closed\n'
            """);
        Dictionary<string, string?> environment = new()
        {
            ["XCODES_USERNAME"] = null,
            ["XCODES_PASSWORD"] = null,
        };
        List<XcodesOutputLine> output = [];

        XcodesResult result = await XcodeSystemProcessRunner.RunAsync(
            script.Path, ["argument with spaces", "-buildVersion", "23E100"],
            environment, output.Add, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("arg:argument with spaces", result.StandardOutput);
        Assert.Contains("arg:-buildVersion", result.StandardOutput);
        Assert.Contains("credentials:unset/unset", result.StandardOutput);
        Assert.Contains("Cookie: [redacted]", result.StandardOutput);
        Assert.Contains("[URL redacted]", result.StandardOutput);
        Assert.DoesNotContain("session-secret", result.Output);
        Assert.DoesNotContain("token=secret", result.Output);
        Assert.DoesNotContain("1.0%", result.StandardOutput);
        Assert.Contains("Downloading: 2.0%", result.StandardOutput);
        Assert.Contains(output, line => line.Text == "Downloading: 2.0%");
        Assert.Contains("diagnostic on stderr", result.StandardError);
        Assert.Contains("stdin closed", result.StandardOutput);
    }

    [Fact]
    public async Task OmitsAnOversizedLineRatherThanRetainingItsContents()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var script = new FakeXcodesScript("printf '%17000s\\n' 'secret-leak'\n");

        XcodesResult result = await XcodeSystemProcessRunner.RunAsync(
            script.Path, [], new Dictionary<string, string?>(), null, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("[A long output line was omitted.]", result.StandardOutput);
        Assert.DoesNotContain("secret-leak", result.Output);
    }

    [Fact]
    public async Task ProgressUpdatesCannotReplaceAnInterleavedError()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var script = new FakeXcodesScript("""
            printf 'Downloading: 1%%\r'
            while [ ! -e "$1/first" ]; do sleep 0.01; done
            printf 'important diagnostic\n' >&2
            while [ ! -e "$1/second" ]; do sleep 0.01; done
            printf 'Downloading: 2%%\r'
            while [ ! -e "$1/third" ]; do sleep 0.01; done
            printf 'Downloading: 3%%\n'
            """);
        List<XcodesOutputLine> output = [];
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        XcodesResult result = await XcodeSystemProcessRunner.RunAsync(
            script.Path, [script.Folder], new Dictionary<string, string?>(), line =>
            {
                output.Add(line);
                string? gate = line.Text switch
                {
                    "Downloading: 1%" => "first",
                    "important diagnostic" => "second",
                    "Downloading: 2%" => "third",
                    _ => null,
                };
                if (gate is not null)
                    File.WriteAllText(Path.Combine(script.Folder, gate), "");
            }, cancellation.Token);

        Assert.True(result.Success);
        Assert.Equal(["Downloading: 1%", "important diagnostic", "Downloading: 2%", "Downloading: 3%"],
            output.Select(line => line.Text));
        Assert.Equal([false, false, false, true], output.Select(line => line.ReplacesPrevious));
        Assert.Contains("important diagnostic", result.StandardError);
    }

    [Fact]
    public async Task CancellationStopsTheRunningProcess()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var script = new FakeXcodesScript("printf 'ready\\n'\nsleep 30\n");
        using var cancellation = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<XcodesResult> operation = XcodeSystemProcessRunner.RunAsync(
            script.Path, [], new Dictionary<string, string?>(),
            line =>
            {
                if (line.Text == "ready")
                    started.TrySetResult();
            }, cancellation.Token);

        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => operation.WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            cancellation.Cancel();
        }
    }

    [Fact]
    public async Task AFailingOutputConsumerStopsTheProcessAndReportsTheFailure()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var script = new FakeXcodesScript("printf 'ready\\n'\nsleep 30\n");

        XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => XcodeSystemProcessRunner.RunAsync(
            script.Path, [], new Dictionary<string, string?>(),
            _ => throw new InvalidOperationException("consumer failed"), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.IsType<InvalidOperationException>(error.InnerException);
        Assert.Contains("consumer failed", error.InnerException.Message);
    }

    [Fact]
    public async Task AFailingOutputConsumerIsReportedEvenWhenTheProcessExitsImmediately()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var script = new FakeXcodesScript("printf 'ready\\n'\n");
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<XcodesResult> operation = Task.Run(() => XcodeSystemProcessRunner.RunAsync(
            script.Path, [], new Dictionary<string, string?>(),
            _ =>
            {
                entered.TrySetResult();
                release.Wait();
                throw new InvalidOperationException("consumer failed");
            }, CancellationToken.None));

        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(200);
            release.Set();
            XcodesException error = await Assert.ThrowsAsync<XcodesException>(
                () => operation.WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.IsType<InvalidOperationException>(error.InnerException);
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task InheritedPipesCannotKeepAnExitedProcessOpenIndefinitely()
    {
        if (OperatingSystem.IsWindows())
            return;
        using var script = new FakeXcodesScript("sleep 30 &\nprintf '%s' \"$!\" > \"$1\"\nexit 0\n");
        string pidPath = Path.Combine(script.Folder, "child.pid");

        try
        {
            XcodesException error = await Assert.ThrowsAsync<XcodesException>(() => XcodeSystemProcessRunner.RunAsync(
                script.Path, [pidPath], new Dictionary<string, string?>(), null, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(12)));

            Assert.Contains("exited without closing its output", error.Message);
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
}
