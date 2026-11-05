using System.Text.Json;
using UniGetUI.Extensions.AgentSkills;

namespace UniGetUI.Extensions.AgentSkills.Tests;

public sealed class CodexPluginsServiceTests
{
    [Fact]
    public async Task GetInstalledCatalog_UsesInstalledOnlyListAndReturnsMarketplaceChoices()
    {
        var runner = new RecordingRunner
        {
            Respond = (args, _) => Task.FromResult(args[1] == "list"
                ? """{"installed":[{"pluginId":"installed@market","version":"2","installed":true,"enabled":false}],"available":[]}"""
                : """{"marketplaces":[{"name":"market","root":"/plugins","marketplaceSource":{"source":"owner/repo"}}]}"""),
        };
        var service = new CodexPluginService(runner);

        AgentPluginCatalog catalog = await service.GetInstalledCatalogAsync();

        Assert.Equal(
            [new[] { "plugin", "list", "--json" }, ["plugin", "marketplace", "list", "--json"]],
            runner.Calls);
        Assert.Equal(new AgentPluginItem("installed@market", "", "2", true, false), Assert.Single(catalog.Plugins));
        Assert.Equal(new AgentPluginMarketplace("market", "owner/repo"), Assert.Single(catalog.Marketplaces));
    }

    [Fact]
    public async Task GetMarketplaceCatalog_UsesOneScopedQueryAndRejectsWrongMarketplaceIdentity()
    {
        var runner = new RecordingRunner
        {
            Respond = (_, _) => Task.FromResult("""{"installed":[],"available":[{"pluginId":"review@team","version":"1"}]}"""),
        };
        var service = new CodexPluginService(runner);

        AgentPluginCatalog catalog = await service.GetMarketplaceCatalogAsync("team");

        Assert.Equal([new[] { "plugin", "list", "--json", "--available", "--marketplace", "team" }], runner.Calls);
        Assert.Equal(new AgentPluginItem("review@team", "", "1", false, false), Assert.Single(catalog.Plugins));
        await Assert.ThrowsAsync<JsonException>(() => service.GetMarketplaceCatalogAsync("other"));
        Assert.Equal(2, runner.Calls.Count);
    }

    [Fact]
    public async Task GetCatalog_UsesNativeJsonCommandsAndPreservesPolicyAndMarketplaceOwnership()
    {
        using var cancel = new CancellationTokenSource();
        var runner = new RecordingRunner
        {
            Respond = (args, _) => Task.FromResult(args[1] == "list"
                ? """
                  {"available":[
                    {"pluginId":"blocked@market","installPolicy":"NOT_AVAILABLE"},
                    {"pluginId":"z-plugin@market","description":"available description","version":"1"},
                    {"pluginId":"allowed@market","description":"Available","version":"3"}],
                   "installed":[
                    {"pluginId":"z-plugin@market","description":"Installed description","version":"2","installed":true,"enabled":false}]}
                  """
                : """
                  {"marketplaces":[
                    {"name":"personal","root":"/plugins/personal","marketplaceSource":{"source":"owner/repo"}},
                    {"name":"built-in","root":"Codex built-in"}]}
                  """),
        };
        var service = new CodexPluginService(runner);

        AgentPluginCatalog catalog = await service.GetCatalogAsync(cancel.Token);

        Assert.Equal("Codex", service.Name);
        Assert.False(service.SupportsUpdate);
        Assert.True(service.SupportsToggle);
        Assert.Contains("Codex CLI", service.Guidance);
        Assert.Contains("Codex app", service.Guidance);
        Assert.Contains("Codex configuration", service.RemovalWarning);
        Assert.Contains("Restart active Codex sessions", service.RestartNotice);
        Assert.Contains("Codex app", service.RestartNotice);
        Assert.Equal(["plugin", "list", "--json", "--available"], runner.Calls[0]);
        Assert.Equal(["plugin", "marketplace", "list", "--json"], runner.Calls[1]);
        Assert.Equal([cancel.Token, cancel.Token], runner.Tokens);
        Assert.Equal(["allowed@market", "blocked@market", "z-plugin@market"], catalog.Plugins.Select(item => item.Id));
        Assert.Equal(new AgentPluginItem("allowed@market", "Available", "3", false, false), catalog.Plugins[0]);
        Assert.False(catalog.Plugins[1].CanInstall);
        Assert.Contains("policy", catalog.Plugins[1].Notice, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(new AgentPluginItem("z-plugin@market", "Installed description", "2", true, false), catalog.Plugins[2]);
        Assert.Equal(
            [new AgentPluginMarketplace("personal", "owner/repo"), new AgentPluginMarketplace("built-in", "Codex built-in") { CanManage = false }],
            catalog.Marketplaces);
    }

    [Fact]
    public async Task MarketplaceAndPluginMutations_MapToCodexCommandsWithoutClaudeFlags()
    {
        var runner = new RecordingRunner();
        var service = new CodexPluginService(runner);

        await service.AddMarketplaceAsync(" codex plugin marketplace add owner/repo ");
        await service.AddMarketplaceAsync("/plugin marketplace add https://example.test/plugins.git");
        await service.UpdateMarketplaceAsync("market");
        await service.RemoveMarketplaceAsync("market");
        await service.PerformAsync("/plugin install plugin@market", AgentPluginAction.Install);
        await service.PerformAsync("codex plugin add plugin@market", AgentPluginAction.Install);
        await service.PerformAsync("plugin@market", AgentPluginAction.Uninstall);

        Assert.Equal(
            [
                new[] { "plugin", "marketplace", "add", "owner/repo" },
                new[] { "plugin", "marketplace", "add", "https://example.test/plugins.git" },
                new[] { "plugin", "marketplace", "upgrade", "market" },
                new[] { "plugin", "marketplace", "remove", "market" },
                new[] { "plugin", "add", "plugin@market" },
                new[] { "plugin", "add", "plugin@market" },
                new[] { "plugin", "remove", "plugin@market" },
            ],
            runner.Calls);
    }

    [Fact]
    public async Task Perform_UpdateFailsBeforeRunner()
    {
        var runner = new RecordingRunner();
        var config = new RecordingConfigWriter();

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            new CodexPluginService(runner, config).PerformAsync("plugin@market", AgentPluginAction.Update));

        Assert.Empty(runner.Calls);
        Assert.Empty(config.Calls);
    }

    [Fact]
    public async Task Perform_EnableAndDisableWriteCodexConfigInsteadOfRunningCli()
    {
        var runner = new RecordingRunner();
        var config = new RecordingConfigWriter();
        var service = new CodexPluginService(runner, config);
        using var cancel = new CancellationTokenSource();

        Assert.Equal("written", await service.PerformAsync("codex plugin add figma@openai-curated", AgentPluginAction.Disable, cancel.Token));
        Assert.Equal("written", await service.PerformAsync("figma@openai-curated", AgentPluginAction.Enable));

        Assert.Empty(runner.Calls);
        Assert.Equal([("figma@openai-curated", false, cancel.Token), ("figma@openai-curated", true, CancellationToken.None)], config.Calls);
        await Assert.ThrowsAsync<ArgumentException>(() => service.PerformAsync("--help@market", AgentPluginAction.Enable));
        Assert.Equal(2, config.Calls.Count);
    }

    [Fact]
    public async Task GetInstalledCatalog_AppProvidedPluginsCanOnlyBeToggled()
    {
        var runner = new RecordingRunner
        {
            Respond = (args, _) => Task.FromResult(args[1] == "list"
                ? """
                  {"installed":[
                    {"pluginId":"browser@openai-bundled","installed":true,"enabled":true},
                    {"pluginId":"documents@openai-primary-runtime","installed":true,"enabled":false},
                    {"pluginId":"github@openai-curated-remote","installed":true,"enabled":true},
                    {"pluginId":"tool@market","installed":true,"enabled":true}],"available":[]}
                  """
                : """{"marketplaces":[]}"""),
        };

        AgentPluginCatalog catalog = await new CodexPluginService(runner, new RecordingConfigWriter()).GetInstalledCatalogAsync();

        foreach (string id in new[] { "browser@openai-bundled", "documents@openai-primary-runtime" })
        {
            AgentPluginItem item = catalog.Plugins.Single(plugin => plugin.Id == id);
            Assert.False(item.CanManage);
            Assert.True(item.CanToggle);
            Assert.Contains("Codex app", item.Notice);
        }
        foreach (string id in new[] { "github@openai-curated-remote", "tool@market" })
        {
            AgentPluginItem item = catalog.Plugins.Single(plugin => plugin.Id == id);
            Assert.True(item.CanManage);
            Assert.True(item.CanToggle);
            Assert.Null(item.Notice);
        }
    }

    [Fact]
    public void ParseCatalog_AppProvidedFlagsOnlyApplyToInstalledPlugins()
    {
        AgentPluginCatalog catalog = CodexPluginService.ParseCatalog(
            """
            {"available":[{"pluginId":"visualize@openai-bundled","version":"1"}],
             "installed":[{"pluginId":"browser@openai-bundled","installed":true,"enabled":true}]}
            """,
            """{"marketplaces":[]}""");

        Assert.False(catalog.Plugins.Single(plugin => plugin.Id == "browser@openai-bundled").CanManage);
        AgentPluginItem available = catalog.Plugins.Single(plugin => plugin.Id == "visualize@openai-bundled");
        Assert.True(available.CanInstall);
        Assert.True(available.CanManage);
        Assert.Null(available.Notice);
    }

    public enum InvalidInventory
    {
        EmptyPlugins, MalformedPlugins, NullPlugins, MissingInstalled, MissingAvailable,
        NullInstalled, NullAvailable, EmptyMarketplaces, MalformedMarketplaces,
        NullMarketplaces, MissingMarketplaces,
    }

    [Theory]
    [InlineData(InvalidInventory.EmptyPlugins)]
    [InlineData(InvalidInventory.MalformedPlugins)]
    [InlineData(InvalidInventory.NullPlugins)]
    [InlineData(InvalidInventory.MissingInstalled)]
    [InlineData(InvalidInventory.MissingAvailable)]
    [InlineData(InvalidInventory.NullInstalled)]
    [InlineData(InvalidInventory.NullAvailable)]
    [InlineData(InvalidInventory.EmptyMarketplaces)]
    [InlineData(InvalidInventory.MalformedMarketplaces)]
    [InlineData(InvalidInventory.NullMarketplaces)]
    [InlineData(InvalidInventory.MissingMarketplaces)]
    public void ParseCatalog_InvalidInventory_ThrowsJsonException(InvalidInventory kind)
    {
        string plugins = kind switch
        {
            InvalidInventory.EmptyPlugins => "",
            InvalidInventory.MalformedPlugins => "{",
            InvalidInventory.NullPlugins => "null",
            InvalidInventory.MissingInstalled => """{"available":[]}""",
            InvalidInventory.MissingAvailable => """{"installed":[]}""",
            InvalidInventory.NullInstalled => """{"installed":null,"available":[]}""",
            InvalidInventory.NullAvailable => """{"installed":[],"available":null}""",
            _ => """{"installed":[],"available":[]}""",
        };
        string marketplaces = kind switch
        {
            InvalidInventory.EmptyMarketplaces => "",
            InvalidInventory.MalformedMarketplaces => "{",
            InvalidInventory.NullMarketplaces => "null",
            InvalidInventory.MissingMarketplaces => "{}",
            _ => """{"marketplaces":[]}""",
        };

        Assert.Throws<JsonException>(() => CodexPluginService.ParseCatalog(plugins, marketplaces));
    }

    [Fact]
    public void ParseCatalog_RejectsMalformedPluginAndMarketplaceIdentities()
    {
        Assert.Throws<ArgumentException>(() => CodexPluginService.ParseCatalog(
            """{"installed":[],"available":[{"pluginId":"--help@market"}]}""", """{"marketplaces":[]}"""));
        Assert.Throws<ArgumentException>(() => CodexPluginService.ParseCatalog(
            """{"installed":[{"pluginId":"plugin"}],"available":[]}""", """{"marketplaces":[]}"""));
        Assert.Throws<ArgumentException>(() => CodexPluginService.ParseCatalog(
            """{"installed":[],"available":[]}""", """{"marketplaces":[{"name":".."}]}"""));
    }

    public enum InvalidPluginId { Empty, Option, MissingMarket, MultipleMarkets, Space, Control }

    [Theory]
    [InlineData(InvalidPluginId.Empty)]
    [InlineData(InvalidPluginId.Option)]
    [InlineData(InvalidPluginId.MissingMarket)]
    [InlineData(InvalidPluginId.MultipleMarkets)]
    [InlineData(InvalidPluginId.Space)]
    [InlineData(InvalidPluginId.Control)]
    public async Task Perform_InvalidPluginIdNeverInvokesRunner(InvalidPluginId kind)
    {
        string id = kind switch
        {
            InvalidPluginId.Empty => "",
            InvalidPluginId.Option => "--help@market",
            InvalidPluginId.MissingMarket => "plugin@",
            InvalidPluginId.MultipleMarkets => "plugin@market@other",
            InvalidPluginId.Space => "plugin name@market",
            _ => "plugin\u0000@market",
        };
        var runner = new RecordingRunner();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            new CodexPluginService(runner).PerformAsync(id, AgentPluginAction.Install));

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task MarketplaceMutation_RejectsOptionAndControlInputsBeforeRunner()
    {
        var runner = new RecordingRunner();
        var service = new CodexPluginService(runner);

        foreach (string input in new[] { "", " \t ", "--help", "repo\u0000name" })
            await Assert.ThrowsAsync<ArgumentException>(() => service.AddMarketplaceAsync(input));
        foreach (string name in new[] { "", "--help", "..", "market\nname" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateMarketplaceAsync(name));
            await Assert.ThrowsAsync<ArgumentException>(() => service.RemoveMarketplaceAsync(name));
        }

        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task CatalogAndMutations_PropagateRunnerFailureAndCancellation()
    {
        var failure = new InvalidOperationException("fixture Codex failure");
        var runner = new RecordingRunner { Respond = (_, _) => Task.FromException<string>(failure) };
        var service = new CodexPluginService(runner);

        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetCatalogAsync()));
        Assert.Single(runner.Calls);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        runner.Respond = (_, token) => Task.FromCanceled<string>(token);
        OperationCanceledException cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.PerformAsync("plugin@market", AgentPluginAction.Install, cancel.Token));
        Assert.Equal(cancel.Token, cancelled.CancellationToken);
        Assert.Equal(cancel.Token, runner.Tokens[^1]);
    }

    private sealed class RecordingConfigWriter : ICodexConfigWriter
    {
        public List<(string Id, bool Enabled, CancellationToken Token)> Calls { get; } = [];

        public Task<string> SetPluginEnabledAsync(string pluginId, bool enabled, CancellationToken token)
        {
            Calls.Add((pluginId, enabled, token));
            return Task.FromResult("written");
        }
    }

    private sealed class RecordingRunner : IAgentPluginRunner
    {
        public List<string[]> Calls { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public Func<IReadOnlyList<string>, CancellationToken, Task<string>> Respond { get; set; } =
            (_, _) => Task.FromResult("fixture result");

        public Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken token)
        {
            Calls.Add(arguments.ToArray());
            Tokens.Add(token);
            return Respond(arguments, token);
        }
    }
}
