using System.Diagnostics;
using System.Text.Json;
using UniGetUI.Core.Logging;

namespace UniGetUI.Extensions.AgentSkills.Tests;

public sealed class ClaudePluginsRunnerTests
{
    private static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    [Fact]
    public async Task MissingExecutable_ExplainsBothCliAndDesktopHosts()
    {
        var runner = new AgentPluginProcessRunner(() => null, TimeSpan.FromSeconds(30));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync([], CancellationToken.None));
        Assert.Contains("Claude Code was not found", error.Message);
        Assert.Contains("Claude Code CLI or Claude Desktop", error.Message);

        var codex = new AgentPluginProcessRunner(() => null, TimeSpan.FromSeconds(30), "Codex");
        error = await Assert.ThrowsAsync<InvalidOperationException>(() => codex.RunAsync([], CancellationToken.None));
        Assert.Contains("Codex CLI or the Codex app", error.Message);

        var git = new AgentPluginProcessRunner(() => null, TimeSpan.FromSeconds(30), "Git");
        error = await Assert.ThrowsAsync<InvalidOperationException>(() => git.RunAsync([], CancellationToken.None));
        Assert.Contains("Git was not found", error.Message);
    }

    [Fact]
    public async Task PreCancelledToken_DoesNotEvenLookUpExecutable()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var runner = new AgentPluginProcessRunner(() => throw new Xunit.Sdk.XunitException("Executable lookup must not run"),
            TimeSpan.FromSeconds(30));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.RunAsync([], cancel.Token));
        Assert.Equal(cancel.Token, error.CancellationToken);
    }

    [Fact]
    public async Task LocalProcess_PreservesLiteralArgumentsClosesStdinAndUsesNoninteractiveHomeEnvironment()
    {
        using var fixture = new ProcessFixture("""
            $inputEnded = $null -eq [Console]::ReadLine()
            [ordered]@{
                arguments = @($args)
                inputEnded = $inputEnded
                directory = [Environment]::CurrentDirectory
                prompt = $env:GIT_TERMINAL_PROMPT
                color = $env:NO_COLOR
            } | ConvertTo-Json -Compress
            """);
        string[] literals = ["name@market", "literal space", "quote\"inside", "semi;$(throw 'injected')", "&", "--scope", "user"];
        string output = await fixture.Runner.RunAsync(fixture.Arguments(literals), CancellationToken.None);
        using var json = JsonDocument.Parse(output);
        Assert.Equal(literals, json.RootElement.GetProperty("arguments").EnumerateArray().Select(value => value.GetString()));
        Assert.True(json.RootElement.GetProperty("inputEnded").GetBoolean());
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            json.RootElement.GetProperty("directory").GetString());
        Assert.Equal("0", json.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("1", json.RootElement.GetProperty("color").GetString());
    }

    [Fact]
    public async Task SuccessfulProcess_TrimsOutputAndDrainsBothPipes()
    {
        using var fixture = new ProcessFixture("""
            [Console]::Error.Write(('e' * 131072))
            [Console]::Out.Write(('o' * 131072) + "  `r`n")
            """);
        string result = await fixture.Runner.RunAsync(fixture.Arguments(), CancellationToken.None);
        Assert.Equal(new string('o', 131072), result);
    }

    [Fact]
    public async Task FailedProcess_ReportsExitCodeAndBothOutputStreams()
    {
        using var fixture = new ProcessFixture("""
            [Console]::Out.Write('fixture stdout')
            [Console]::Error.Write('fixture stderr')
            exit 7
            """);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Runner.RunAsync(fixture.Arguments(), CancellationToken.None));
        Assert.Contains("exit 7", error.Message);
        Assert.Contains("fixture stdout", error.Message);
        Assert.Contains("fixture stderr", error.Message);
    }

    [Fact]
    public async Task SuccessfulProcess_LogsStderrWarningWithoutTurningSuccessIntoFailure()
    {
        int previous = Logger.GetLogs().Length;
        using var fixture = new ProcessFixture("""
            [Console]::Out.Write('  fixture success  ')
            [Console]::Error.Write('  fixture warning code 117  ')
            """);
        Assert.Equal("fixture success", await fixture.Runner.RunAsync(fixture.Arguments(), CancellationToken.None));
        Assert.Contains(Logger.GetLogs().Skip(previous), entry =>
            entry.Severity == LogEntry.SeverityLevel.Warning && entry.Content == "fixture warning code 117");
    }

    [Fact]
    public async Task InFlightCancellation_KillsLocalProcessTreeAndReturnsCancellation()
    {
        string eventName = "Local\\UniGetUI-ClaudePlugins-" + Guid.NewGuid().ToString("N");
        using var entered = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        using var fixture = new ProcessFixture($"""
            $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes('[Threading.Thread]::Sleep(-1)'))
            $descendant = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -ArgumentList @('-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded) -PassThru
            [IO.File]::WriteAllText($args[0], "$PID,$($descendant.Id)")
            $ready = [Threading.EventWaitHandle]::OpenExisting('{eventName}')
            $ready.Set() | Out-Null
            [Threading.Thread]::Sleep(-1)
            """);
        using var cancel = new CancellationTokenSource();
        Task<string> pending = fixture.Runner.RunAsync(fixture.Arguments(fixture.PidFile), cancel.Token);
        try
        {
            Assert.True(await Task.Run(() => entered.WaitOne(TimeSpan.FromSeconds(30))), "Local fixture did not signal readiness");
            int[] pids = (await File.ReadAllTextAsync(fixture.PidFile)).Split(',').Select(int.Parse).ToArray();
            using var child = Process.GetProcessById(pids[0]);
            using var descendant = Process.GetProcessById(pids[1]);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            await descendant.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(child.HasExited);
            Assert.True(descendant.HasExited);
        }
        finally
        {
            cancel.Cancel();
            try { await pending; }
            catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task ZeroDeadline_IsTimeoutRatherThanUserCancellation()
    {
        using var fixture = new ProcessFixture("[Threading.Thread]::Sleep(-1)", TimeSpan.Zero);
        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            fixture.Runner.RunAsync(fixture.Arguments(), CancellationToken.None));
        Assert.Contains("timed out", error.Message);
    }

    private sealed class ProcessFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "UniGetUI-ClaudePlugins-" + Guid.NewGuid().ToString("N"));
        private readonly string _script;
        public string PidFile => Path.Combine(_root, "fixture.pid");
        public AgentPluginProcessRunner Runner { get; }
        public ProcessFixture(string script, TimeSpan? timeout = null)
        {
            Assert.True(File.Exists(PowerShell), "These local process regressions require Windows PowerShell");
            Directory.CreateDirectory(_root);
            _script = Path.Combine(_root, "local fixture.ps1");
            File.WriteAllText(_script, script);
            Runner = new AgentPluginProcessRunner(() => PowerShell, timeout ?? TimeSpan.FromSeconds(30));
        }
        public string[] Arguments(params string[] values) =>
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", _script, .. values];
        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
