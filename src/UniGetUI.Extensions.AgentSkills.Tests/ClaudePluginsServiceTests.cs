using System.Text.Json;

namespace UniGetUI.Extensions.AgentSkills.Tests;

public sealed class ClaudePluginsServiceTests
{
    [Fact]
    public void Guidance_CoversTheCliAndTheClaudeDesktopCodeTab()
    {
        var service = new ClaudePluginService(new RecordingRunner());

        Assert.Contains("Claude Code CLI", service.Guidance);
        Assert.Contains("Claude Desktop", service.Guidance);
        Assert.Contains("Cowork", service.Guidance);
        Assert.Contains("Restart active Claude Code sessions", service.RestartNotice);
        Assert.Contains("Claude Desktop", service.RestartNotice);
    }

    [Fact]
    public async Task PastedExample_UsesNativeUserCommandsWithoutAutomaticTrustFlags()
    {
        var runner = new RecordingRunner();
        var service = new ClaudePluginService(runner);

        Assert.Equal("done", await service.AddMarketplaceAsync("  /plugin marketplace add Devolutions/ux-prototypes  "));
        await service.PerformAsync("/plugin install ux-design-agents@devolutions-ux", AgentPluginAction.Install);
        await service.PerformAsync("/plugin install ux-skills@devolutions-ux", AgentPluginAction.Install);

        Assert.Equal(3, runner.Calls.Count);
        Assert.Equal(["plugin", "marketplace", "add", "Devolutions/ux-prototypes", "--scope", "user"], runner.Calls[0]);
        Assert.Equal(["plugin", "install", "ux-design-agents@devolutions-ux", "--scope", "user"], runner.Calls[1]);
        Assert.Equal(["plugin", "install", "ux-skills@devolutions-ux", "--scope", "user"], runner.Calls[2]);
    }

    [Theory]
    [InlineData(AgentPluginAction.Install, "install")]
    [InlineData(AgentPluginAction.Update, "update")]
    [InlineData(AgentPluginAction.Enable, "enable")]
    [InlineData(AgentPluginAction.Disable, "disable")]
    [InlineData(AgentPluginAction.Uninstall, "uninstall")]
    public async Task Perform_AllActionsPreserveFullyQualifiedIdentityAndUserScope(AgentPluginAction action, string command)
    {
        var runner = new RecordingRunner();
        await new ClaudePluginService(runner).PerformAsync(" ux-skills@other-market ", action);
        Assert.Equal(["plugin", command, "ux-skills@other-market", "--scope", "user"], Assert.Single(runner.Calls));
    }

    [Fact]
    public async Task MarketplaceOperations_PreserveLiteralSourceAndOnlyScopeMutationsThatSupportIt()
    {
        var runner = new RecordingRunner();
        var service = new ClaudePluginService(runner);
        await service.AddMarketplaceAsync(@"C:\trusted marketplace");
        await service.UpdateMarketplaceAsync("devolutions-ux");
        await service.RemoveMarketplaceAsync("devolutions-ux");
        Assert.Equal(["plugin", "marketplace", "add", @"C:\trusted marketplace", "--scope", "user"], runner.Calls[0]);
        Assert.Equal(["plugin", "marketplace", "update", "devolutions-ux"], runner.Calls[1]);
        Assert.Equal(["plugin", "marketplace", "remove", "devolutions-ux", "--scope", "user"], runner.Calls[2]);
    }

    [Fact]
    public async Task ValidAsciiIdentity_AllowsDotsUnderscoresDigitsAndForwardsCancellationToEveryMutation()
    {
        using var cancel = new CancellationTokenSource();
        var runner = new RecordingRunner();
        var service = new ClaudePluginService(runner);
        await service.PerformAsync("agent_2.v1@my-market_2.v1", AgentPluginAction.Install, cancel.Token);
        await service.AddMarketplaceAsync("owner/repo", cancel.Token);
        await service.UpdateMarketplaceAsync("my-market_2.v1", cancel.Token);
        await service.RemoveMarketplaceAsync("my-market_2.v1", cancel.Token);
        Assert.Equal(["plugin", "install", "agent_2.v1@my-market_2.v1", "--scope", "user"], runner.Calls[0]);
        Assert.Equal(4, runner.Tokens.Count);
        Assert.All(runner.Tokens, token => Assert.Equal(cancel.Token, token));
    }

    [Fact]
    public async Task GetCatalog_RequestsBothJsonInventoriesAndForwardsToken()
    {
        using var cancel = new CancellationTokenSource();
        var runner = new RecordingRunner
        {
            Respond = (args, _) => Task.FromResult(args[1] == "list" ? """{"installed":[],"available":[]}""" : "[]"),
        };
        var catalog = await new ClaudePluginService(runner).GetCatalogAsync(cancel.Token);
        Assert.Empty(catalog.Plugins);
        Assert.Empty(catalog.Marketplaces);
        Assert.Equal(["plugin", "list", "--json", "--available"], runner.Calls[0]);
        Assert.Equal(["plugin", "marketplace", "list", "--json"], runner.Calls[1]);
        Assert.All(runner.Tokens, token => Assert.Equal(cancel.Token, token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstalledCatalog_AcceptsBothShapesWithoutBrowsingAndPreservesUserState(bool wrapped)
    {
        const string installed = """
            [
              {"id":"enabled@market","version":"2","scope":"user","enabled":true,
               "errors":["configuration warning"],"installPath":"ignored","projectEnabled":false},
              {"id":"disabled@market","version":"1","scope":"user","enabled":false},
              {"id":"project@market","scope":"project","enabled":true},
              {"id":"local@market","scope":"local","enabled":true}
            ]
            """;
        string inventory = wrapped ? $$"""{"installed":{{installed}}}""" : "\r\n " + installed;
        var runner = new RecordingRunner
        {
            Respond = (args, _) => Task.FromResult(args.SequenceEqual(["plugin", "list", "--json"])
                ? inventory : """[{"name":"market","url":"https://example.com/plugins"}]"""),
        };
        using var cancel = new CancellationTokenSource();

        var result = await new ClaudePluginService(runner).GetInstalledCatalogAsync(cancel.Token);

        Assert.Equal(
            [
                new AgentPluginItem("enabled@market", "configuration warning", "2", true, true),
                new AgentPluginItem("disabled@market", "", "1", true, false),
            ], result.Plugins);
        Assert.Equal([new AgentPluginMarketplace("market", "https://example.com/plugins")], result.Marketplaces);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(["plugin", "list", "--json"], runner.Calls[0]);
        Assert.Equal(["plugin", "marketplace", "list", "--json"], runner.Calls[1]);
        Assert.All(runner.Tokens, token => Assert.Equal(cancel.Token, token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InstalledCatalog_AcceptsEmptyInventories(bool wrapped)
    {
        var result = ClaudePluginService.ParseInstalledCatalog(wrapped ? """{"installed":[]}""" : "[]", "[]");

        Assert.Empty(result.Plugins);
        Assert.Empty(result.Marketplaces);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"available":[]}""")]
    [InlineData("""{"installed":null}""")]
    [InlineData("""{"installed":{}}""")]
    public void InstalledCatalog_RejectsInvalidInventoriesInsteadOfReportingNoPlugins(string json) =>
        Assert.Throws<JsonException>(() => ClaudePluginService.ParseInstalledCatalog(json, "[]"));

    [Fact]
    public async Task MarketplaceBrowse_SharesOneAvailableReadAndKeepsInstalledPluginsInstallable()
    {
        var runner = new RecordingRunner
        {
            // Claude omits installed plugins from "available"; a malformed entry elsewhere must not block other marketplaces.
            Respond = (args, _) => Task.FromResult(args.Contains("--available") ? """
                {"installed":[
                   {"id":"kept@market-a","version":"2","scope":"user","enabled":true},
                   {"id":"project@market-a","version":"1","scope":"project","enabled":true}],
                 "available":[
                   {"pluginId":"new@market-a","description":"New plugin","version":"1"},
                   {"pluginId":"other@market-b","description":"Other plugin","version":"3"},
                   {"pluginId":"bad id@market-c"}]}
                """ : "done"),
        };
        var service = new ClaudePluginService(runner);

        Assert.True(service.SupportsScopedBrowse);
        var first = await service.GetMarketplaceCatalogAsync("market-a");
        var second = await service.GetMarketplaceCatalogAsync("market-b");

        Assert.Equal(
            [
                new AgentPluginItem("kept@market-a", "", "2", false, false),
                new AgentPluginItem("new@market-a", "New plugin", "1", false, false),
            ], first.Plugins);
        Assert.Empty(first.Marketplaces);
        Assert.Equal([new AgentPluginItem("other@market-b", "Other plugin", "3", false, false)], second.Plugins);
        Assert.Equal(["plugin", "list", "--json", "--available"], Assert.Single(runner.Calls));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetMarketplaceCatalogAsync("market-c"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.GetMarketplaceCatalogAsync("--help"));
        Assert.Single(runner.Calls);

        await service.UpdateMarketplaceAsync("market-a");
        await service.GetMarketplaceCatalogAsync("market-a");
        Assert.Equal(3, runner.Calls.Count);
        Assert.Equal(["plugin", "list", "--json", "--available"], runner.Calls[2]);
    }

    [Fact]
    public async Task MarketplaceBrowse_RetriesAFailedReadAndSurvivesACancelledCaller()
    {
        var runner = new RecordingRunner { Respond = (_, _) => Task.FromException<string>(new InvalidOperationException("offline")) };
        var service = new ClaudePluginService(runner);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetMarketplaceCatalogAsync("market"));

        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        runner.Respond = (_, _) => release.Task;
        using var cancel = new CancellationTokenSource();
        Task<AgentPluginCatalog> cancelled = service.GetMarketplaceCatalogAsync("market", cancel.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Task<AgentPluginCatalog> waiting = service.GetMarketplaceCatalogAsync("market");
        release.SetResult("""{"installed":[],"available":[{"pluginId":"plugin@market"}]}""");

        Assert.Equal("plugin@market", Assert.Single((await waiting).Plugins).Id);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Equal(CancellationToken.None, runner.Tokens[^1]);
    }

    [Fact]
    public void ParseCatalog_MergesOnlyUserInstallsWithoutCollapsingMarketsAndKeepsMetadata()
    {
        var result = ClaudePluginService.ParseCatalog("""
            {"available":[
              {"pluginId":"ux-skills@z-market","description":"Other market","version":"9"},
              {"pluginId":"ux-skills@devolutions-ux","description":"Trusted skills","version":"2"},
              {"pluginId":"agents@devolutions-ux"}],
             "installed":[
              {"id":"ux-skills@devolutions-ux","scope":"user","version":"1","enabled":true,"errors":["hook failed","config warning"]},
              {"id":"ux-skills@z-market","scope":"project","version":"project-version","enabled":true},
              {"id":"project-only@market","scope":"local","enabled":true},
              {"id":"missing-scope@market","enabled":true},
              {"id":"USER-scope@market","scope":"USER","enabled":true},
              {"id":"offline@market","scope":"user","enabled":false}]}
            """, """
            [{"name":"z-market","source":"fallback"},{"name":"devolutions-ux","url":"Devolutions/ux-prototypes","source":"ignored"}]
            """);

        Assert.Equal(
            [
                new AgentPluginItem("agents@devolutions-ux", "", "", false, false),
                new AgentPluginItem("offline@market", "", "", true, false),
                new AgentPluginItem("ux-skills@devolutions-ux",
                    string.Join(Environment.NewLine, "Trusted skills", "hook failed", "config warning"), "1", true, true),
                new AgentPluginItem("ux-skills@z-market", "Other market", "9", false, false),
            ], result.Plugins);
        Assert.Equal(
            [new AgentPluginMarketplace("devolutions-ux", "Devolutions/ux-prototypes"), new AgentPluginMarketplace("z-market", "fallback")],
            result.Marketplaces);
        Assert.Equal("ux-skills", result.Plugins[2].Name);
        Assert.Equal("devolutions-ux", result.Plugins[2].Marketplace);
        Assert.Equal("Enabled", result.Plugins[2].Status);
        Assert.Equal("Disabled", result.Plugins[1].Status);
        Assert.Equal("Not installed", result.Plugins[0].Status);
    }

    public enum InvalidInventory { Empty, Malformed, Null, Array, MissingInstalled, MissingAvailable, NullInstalled, NullAvailable }

    [Theory]
    [InlineData(InvalidInventory.Empty)]
    [InlineData(InvalidInventory.Malformed)]
    [InlineData(InvalidInventory.Null)]
    [InlineData(InvalidInventory.Array)]
    [InlineData(InvalidInventory.MissingInstalled)]
    [InlineData(InvalidInventory.MissingAvailable)]
    [InlineData(InvalidInventory.NullInstalled)]
    [InlineData(InvalidInventory.NullAvailable)]
    public void ParseCatalog_RejectsInvalidPluginInventory(InvalidInventory kind)
    {
        string json = kind switch
        {
            InvalidInventory.Empty => "",
            InvalidInventory.Malformed => "{",
            InvalidInventory.Null => "null",
            InvalidInventory.Array => "[]",
            InvalidInventory.MissingInstalled => """{"available":[]}""",
            InvalidInventory.MissingAvailable => """{"installed":[]}""",
            InvalidInventory.NullInstalled => """{"installed":null,"available":[]}""",
            _ => """{"installed":[],"available":null}""",
        };
        Assert.Throws<JsonException>(() => ClaudePluginService.ParseCatalog(json, "[]"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{}")]
    public void ParseCatalog_RejectsInvalidMarketplaceInventory(string json) =>
        Assert.Throws<JsonException>(() => ClaudePluginService.ParseCatalog("""{"installed":[],"available":[]}""", json));

    [Fact]
    public void ParseCatalog_RejectsInvalidIdentitiesInBothInventories()
    {
        Assert.Throws<ArgumentException>(() => ClaudePluginService.ParseCatalog(
            """{"installed":[],"available":[{"pluginId":"--help"}]}""", "[]"));
        Assert.Throws<ArgumentException>(() => ClaudePluginService.ParseCatalog(
            """{"installed":[{"id":"bare-name","scope":"user"}],"available":[]}""", "[]"));
        Assert.Throws<ArgumentException>(() => ClaudePluginService.ParseCatalog(
            """{"installed":[],"available":[]}""", """[{"name":"--help"}]"""));
    }

    public enum InvalidIdentifier { Empty, Whitespace, Option, BareName, MissingName, MissingMarket, MultipleMarkets, Space, Slash, Control, Newline, Unicode }

    [Theory]
    [InlineData(InvalidIdentifier.Empty)]
    [InlineData(InvalidIdentifier.Whitespace)]
    [InlineData(InvalidIdentifier.Option)]
    [InlineData(InvalidIdentifier.BareName)]
    [InlineData(InvalidIdentifier.MissingName)]
    [InlineData(InvalidIdentifier.MissingMarket)]
    [InlineData(InvalidIdentifier.MultipleMarkets)]
    [InlineData(InvalidIdentifier.Space)]
    [InlineData(InvalidIdentifier.Slash)]
    [InlineData(InvalidIdentifier.Control)]
    [InlineData(InvalidIdentifier.Newline)]
    [InlineData(InvalidIdentifier.Unicode)]
    public async Task Perform_InvalidIdentityNeverInvokesRunner(InvalidIdentifier kind)
    {
        string input = kind switch
        {
            InvalidIdentifier.Empty => "",
            InvalidIdentifier.Whitespace => " \t ",
            InvalidIdentifier.Option => "--help@market",
            InvalidIdentifier.BareName => "skills",
            InvalidIdentifier.MissingName => "@market",
            InvalidIdentifier.MissingMarket => "skills@",
            InvalidIdentifier.MultipleMarkets => "skills@market@other",
            InvalidIdentifier.Space => "skills name@market",
            InvalidIdentifier.Slash => "owner/skills@market",
            InvalidIdentifier.Control => "skills\u0000@market",
            InvalidIdentifier.Newline => "skills@\nmarket",
            _ => "skílls@market",
        };
        var runner = new RecordingRunner();
        var service = new ClaudePluginService(runner);
        await Assert.ThrowsAsync<ArgumentException>(() => service.PerformAsync(input, AgentPluginAction.Install));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task MarketplaceAndUnknownActionValidation_NeverInvokesRunner()
    {
        var runner = new RecordingRunner();
        var service = new ClaudePluginService(runner);
        foreach (string value in new[] { "", " ", "--help", "-x", "repo\u0000name", "repo\nname" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => service.AddMarketplaceAsync(value));
            await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateMarketplaceAsync(value));
            await Assert.ThrowsAsync<ArgumentException>(() => service.RemoveMarketplaceAsync(value));
        }
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.PerformAsync("skills@market", (AgentPluginAction)99));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task RunnerFailuresAndCancellation_ArePropagatedUnchangedWithoutCatalogContinuation()
    {
        var failure = new InvalidOperationException("fixture CLI error");
        var runner = new RecordingRunner { Respond = (_, _) => Task.FromException<string>(failure) };
        var service = new ClaudePluginService(runner);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetCatalogAsync()));
        Assert.Single(runner.Calls);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        runner.Respond = (_, token) => Task.FromCanceled<string>(token);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.PerformAsync("skills@market", AgentPluginAction.Install, cancel.Token));
        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.Equal(cancel.Token, runner.Tokens[^1]);
        runner.Respond = (_, _) => Task.FromException<string>(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => service.AddMarketplaceAsync("owner/repo")));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => service.UpdateMarketplaceAsync("market")));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => service.RemoveMarketplaceAsync("market")));
    }

    private sealed class RecordingRunner : IAgentPluginRunner
    {
        public List<string[]> Calls { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public Func<IReadOnlyList<string>, CancellationToken, Task<string>> Respond { get; set; } = (_, _) => Task.FromResult("done");
        public Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken token)
        {
            Calls.Add(arguments.ToArray());
            Tokens.Add(token);
            return Respond(arguments, token);
        }
    }
}
