using System.Diagnostics;
using System.Text.Json;

namespace UniGetUI.Extensions.AgentSkills.Tests;

public sealed class CodexAppServerClientTests
{
    private static string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
        "WindowsPowerShell", "v1.0", "powershell.exe");

    private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

    private static JsonElement[] Requests(StringWriter input) => input.ToString()
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(line => JsonDocument.Parse(line).RootElement.Clone())
        .ToArray();

    [Fact]
    public async Task Exchange_InitializesThenUpsertsTheEnabledFlagAndSkipsUnrelatedMessages()
    {
        var input = new StringWriter();
        var output = new StringReader(Lines(
            "codex app-server listening",
            "",
            """{"method":"remoteControl/status/changed","params":{"status":"disabled"}}""",
            """{"id":1,"result":{"userAgent":"codex_app_server/0.166.0"}}""",
            """{"id":"2","result":{"status":"okOverridden"}}""",
            """{"id":9,"result":{"status":"okOverridden"}}""",
            """{"id":2,"method":"item/tool/requestUserInput","params":{}}""",
            """["not","an","object"]""",
            """{"id":2,"result":{"status":"ok","version":"sha256:1","filePath":"C:\\Users\\me\\.codex\\config.toml"}}"""));

        string result = await CodexAppServerConfigWriter.ExchangeAsync(input, output, "figma@openai-curated", false,
            CancellationToken.None);

        Assert.Equal(@"Disabled figma@openai-curated in C:\Users\me\.codex\config.toml", result);
        JsonElement[] requests = Requests(input);
        Assert.Equal(3, requests.Length);
        Assert.All(requests, request => Assert.False(request.TryGetProperty("jsonrpc", out _)));

        Assert.Equal(1, requests[0].GetProperty("id").GetInt32());
        Assert.Equal("initialize", requests[0].GetProperty("method").GetString());
        Assert.Equal("unigetui", requests[0].GetProperty("params").GetProperty("clientInfo").GetProperty("name").GetString());

        Assert.Equal("initialized", requests[1].GetProperty("method").GetString());
        Assert.False(requests[1].TryGetProperty("id", out _));

        Assert.Equal(2, requests[2].GetProperty("id").GetInt32());
        Assert.Equal("config/value/write", requests[2].GetProperty("method").GetString());
        JsonElement parameters = requests[2].GetProperty("params");
        Assert.Equal("plugins.figma@openai-curated", parameters.GetProperty("keyPath").GetString());
        Assert.Equal(JsonValueKind.False, parameters.GetProperty("value").GetProperty("enabled").ValueKind);
        Assert.Equal("upsert", parameters.GetProperty("mergeStrategy").GetString());
    }

    [Fact]
    public async Task Exchange_EnableReportsTheSavedFile()
    {
        var input = new StringWriter();
        var output = new StringReader(Lines("""{"id":1,"result":{}}""", """{"id":2,"result":{"status":"ok","filePath":"config.toml"}}"""));

        Assert.Equal("Enabled tool@market in config.toml",
            await CodexAppServerConfigWriter.ExchangeAsync(input, output, "tool@market", true, CancellationToken.None));
        Assert.Equal(JsonValueKind.True, Requests(input)[2].GetProperty("params").GetProperty("value").GetProperty("enabled").ValueKind);
    }

    [Theory]
    [InlineData("tool@market", "plugins.tool@market")]
    [InlineData("tool-1_x@open-ai_market", "plugins.tool-1_x@open-ai_market")]
    [InlineData("tool.v2@market", "plugins.\"tool.v2@market\"")]
    [InlineData("tool@market.dev", "plugins.\"tool@market.dev\"")]
    public void PluginKeyPath_QuotesOnlyIdsContainingDots(string id, string expected) =>
        Assert.Equal(expected, CodexAppServerConfigWriter.PluginKeyPath(id));

    [Theory]
    [InlineData("""{"id":1,"error":{"code":-32600,"message":"Not initialized"}}""", "Not initialized")]
    [InlineData("""{"id":1,"error":"plain failure"}""", "\"plain failure\"")]
    public async Task Exchange_ErrorResponseIsReported(string response, string detail)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CodexAppServerConfigWriter.ExchangeAsync(
            new StringWriter(), new StringReader(Lines(response)), "tool@market", true, CancellationToken.None));

        Assert.Contains("Codex rejected the plugin change", error.Message);
        Assert.Contains(detail, error.Message);
    }

    [Fact]
    public async Task Exchange_ConfigValidationErrorFromWriteIsReported()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CodexAppServerConfigWriter.ExchangeAsync(
            new StringWriter(),
            new StringReader(Lines("""{"id":1,"result":{}}""",
                """{"id":2,"error":{"code":-32600,"message":"Invalid configuration","data":{"config_write_error_code":"configValidationError"}}}""")),
            "tool@market", true, CancellationToken.None));

        Assert.Contains("Invalid configuration", error.Message);
    }

    [Fact]
    public async Task Exchange_OverriddenWriteIsAFailure()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CodexAppServerConfigWriter.ExchangeAsync(
            new StringWriter(),
            new StringReader(Lines("""{"id":1,"result":{}}""", """{"id":2,"result":{"status":"okOverridden","overriddenMetadata":{}}}""")),
            "tool@market", false, CancellationToken.None));

        Assert.Contains("overrides it", error.Message);
    }

    [Theory]
    [InlineData("""{"id":2,"result":{"status":"pending"}}""")]
    [InlineData("""{"id":2,"result":{}}""")]
    [InlineData("""{"id":2,"result":"ok"}""")]
    [InlineData("""{"id":2}""")]
    public async Task Exchange_UnexpectedWriteResultIsRejected(string response)
    {
        await Assert.ThrowsAsync<InvalidDataException>(() => CodexAppServerConfigWriter.ExchangeAsync(
            new StringWriter(), new StringReader(Lines("""{"id":1,"result":{}}""", response)),
            "tool@market", true, CancellationToken.None));
    }

    [Fact]
    public async Task Exchange_EndOfStreamIncludesServerDiagnostics()
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => CodexAppServerConfigWriter.ExchangeAsync(
            new StringWriter(), new StringReader(Lines("""{"id":1,"result":{}}""")), "tool@market", true,
            CancellationToken.None, () => "  unknown subcommand 'app-server'  "));

        Assert.Equal("Codex app-server stopped before replying. unknown subcommand 'app-server'", error.Message);

        error = await Assert.ThrowsAsync<InvalidOperationException>(() => CodexAppServerConfigWriter.ExchangeAsync(
            new StringWriter(), new StringReader(""), "tool@market", true, CancellationToken.None));
        Assert.Equal("Codex app-server stopped before replying.", error.Message);
    }

    [Fact]
    public async Task Exchange_CancellationStopsBeforeWriting()
    {
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var input = new StringWriter();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CodexAppServerConfigWriter.ExchangeAsync(
            input, new StringReader(Lines("""{"id":1,"result":{}}""")), "tool@market", true, cancel.Token));
        Assert.Empty(input.ToString());
    }

    [Fact]
    public async Task SetPluginEnabled_ValidatesBeforeStartingCodex()
    {
        int lookups = 0;
        var writer = new CodexAppServerConfigWriter(() => { lookups++; return null; }, ["app-server"], TimeSpan.FromSeconds(30));

        await Assert.ThrowsAsync<ArgumentException>(() => writer.SetPluginEnabledAsync("--config@x", true, CancellationToken.None));
        Assert.Equal(0, lookups);

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            writer.SetPluginEnabledAsync("tool@market", true, CancellationToken.None));
        Assert.Contains("Codex CLI or the Codex app", missing.Message);

        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.SetPluginEnabledAsync("tool@market", true, cancel.Token));
        Assert.Equal(1, lookups);
    }

    [Fact]
    public async Task SetPluginEnabled_TalksToALocalServerOverUtf8StdioWithoutBom()
    {
        using var fixture = new ServerFixture("""
            $in = [Console]::In
            $first = $in.ReadLine()
            [Console]::Out.WriteLine('{"method":"remoteControl/status/changed","params":{}}')
            [Console]::Out.WriteLine('{"id":1,"result":{}}')
            [Console]::Out.Flush()
            $second = $in.ReadLine()
            $third = $in.ReadLine()
            [IO.File]::WriteAllLines($args[0], [string[]]@($first, $second, $third, $env:NO_COLOR))
            [Console]::Out.WriteLine('{"id":2,"result":{"status":"ok","filePath":"fixture config.toml"}}')
            [Console]::Out.Flush()
            [Console]::Error.WriteLine('fixture stderr')
            $null = $in.ReadToEnd()
            """);

        string result = await fixture.Writer().SetPluginEnabledAsync("tool.v2@market", false, CancellationToken.None);

        Assert.Equal("Disabled tool.v2@market in fixture config.toml", result);
        string[] requests = await File.ReadAllLinesAsync(fixture.Capture);
        Assert.StartsWith("{\"id\":1,", requests[0], StringComparison.Ordinal);
        Assert.Equal("""{"method":"initialized"}""", requests[1]);
        Assert.Contains("\"keyPath\":\"plugins.\\u0022tool.v2@market\\u0022\"", requests[2]);
        Assert.Equal("1", requests[3]);
    }

    [Fact]
    public async Task SetPluginEnabled_SilentServerTimesOutAndIsStopped()
    {
        using var fixture = new ServerFixture("""
            [IO.File]::WriteAllText($args[0], "$PID")
            [Threading.Thread]::Sleep(-1)
            """);
        var stopwatch = Stopwatch.StartNew();

        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            fixture.Writer(TimeSpan.FromSeconds(3)).SetPluginEnabledAsync("tool@market", true, CancellationToken.None));

        Assert.Contains("timed out", error.Message);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20));
        if (File.Exists(fixture.Capture) && int.TryParse(await File.ReadAllTextAsync(fixture.Capture), out int pid))
        {
            try
            {
                using Process server = Process.GetProcessById(pid);
                await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (ArgumentException)
            {
            }
        }
    }

    private sealed class ServerFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "UniGetUI-CodexAppServer-" + Guid.NewGuid().ToString("N"));
        private readonly string _script;
        public string Capture => Path.Combine(_root, "capture.txt");

        public ServerFixture(string script)
        {
            Assert.True(File.Exists(PowerShell), "These local process regressions require Windows PowerShell");
            Directory.CreateDirectory(_root);
            _script = Path.Combine(_root, "fake app-server.ps1");
            File.WriteAllText(_script, script);
        }

        public CodexAppServerConfigWriter Writer(TimeSpan? timeout = null) => new(() => PowerShell,
            ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", _script, Capture],
            timeout ?? TimeSpan.FromSeconds(30));

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
        }
    }
}
