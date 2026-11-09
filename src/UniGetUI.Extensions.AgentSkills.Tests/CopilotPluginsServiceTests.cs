using System.Text.Json;

namespace UniGetUI.Extensions.AgentSkills.Tests;

public sealed class CopilotPluginsServiceTests
{
    [Fact]
    public async Task GetInstalledCatalog_DoesNotBrowseMarketplaces()
    {
        var runner = new Runner
        {
            Installed = """[{"name":"review","marketplace":"team","version":"2","enabled":false,"source":"installed"}]""",
            Markets = """[{"name":"team","source":"owner/team"}]""",
        };
        var service = new CopilotPluginService(runner);

        AgentPluginCatalog catalog = await service.GetInstalledCatalogAsync();

        Assert.Equal(
            [new[] { "plugin", "list", "--json" }, ["plugin", "marketplace", "list", "--json"]],
            runner.Calls);
        Assert.Equal(new AgentPluginItem("review@team", "", "2", true, false), Assert.Single(catalog.Plugins));
        var marketplace = Assert.Single(catalog.Marketplaces);
        Assert.Equal("team", marketplace.Name);
        Assert.Equal("owner/team", marketplace.Source);
        Assert.True(marketplace.CanManage);
        Assert.True(marketplace.CanRefresh);
    }

    [Fact]
    public async Task GetMarketplaceCatalog_BrowsesOnlyRequestedMarketplaceAndRejectsCrossMarketplaceEntries()
    {
        var runner = new Runner
        {
            Browse = name => name == "team"
                ? """[{"name":"review","marketplace":"team","description":"Available","version":"1"}]"""
                : """[{"name":"review","marketplace":"unexpected"}]""",
        };
        var service = new CopilotPluginService(runner);

        AgentPluginCatalog catalog = await service.GetMarketplaceCatalogAsync("team");

        Assert.Equal([new[] { "plugin", "marketplace", "browse", "team", "--json" }], runner.Calls);
        Assert.Equal(new AgentPluginItem("review@team", "Available", "1", false, false), Assert.Single(catalog.Plugins));
        await Assert.ThrowsAsync<JsonException>(() => service.GetMarketplaceCatalogAsync("other"));
        Assert.Equal(["team", "other"], runner.Calls.Select(call => call[3]));
    }

    [Fact]
    public async Task CatalogMergesNativeFlatArraysAndPreservesOwnershipAndExactIdentity()
    {
        var runner = new Runner
        {
            Installed = """
                [{"name":"review","marketplace":"team","version":"2","enabled":false,"source":"installed"},
                 {"name":"builtin","version":"1","enabled":true,"source":"builtin"},
                 {"name":"external","enabled":true,"source":"external"},
                 {"name":"managed","marketplace":"team","enabled":true,"source":"installed","managed":true}]
                """,
            Markets = """
                [{"name":"team","source":"owner/repo","isDefault":false},
                 {"name":"awesome-copilot","source":"github/awesome-copilot","isDefault":true},
                 {"name":"policy","source":"org/policy","managed":true}]
                """,
            Browse = name => name == "team"
                ? """[{"name":"review","description":"Available description","marketplace":"team"},{"name":"new","description":"New plugin","marketplace":"team"}]"""
                : name == "awesome-copilot"
                    ? """[{"name":"review","description":"Different marketplace","marketplace":"awesome-copilot"}]""" : "[]",
        };
        var service = new CopilotPluginService(runner);
        using var cancel = new CancellationTokenSource();

        var catalog = await service.GetCatalogAsync(cancel.Token);

        Assert.Equal(["builtin", "external", "managed@team", "new@team", "review@awesome-copilot", "review@team"],
            catalog.Plugins.Select(item => item.Id));
        var review = catalog.Plugins.Single(item => item.Id == "review@team");
        Assert.Equal(new AgentPluginItem("review@team", "Available description", "2", true, false), review);
        Assert.False(catalog.Plugins.Single(item => item.Id == "review@awesome-copilot").IsInstalled);
        foreach (var id in new[] { "builtin", "external", "managed@team" })
        {
            var item = catalog.Plugins.Single(item => item.Id == id);
            Assert.True(item.IsInstalled);
            Assert.False(item.CanManage);
            Assert.False(item.ShowInstall);
            Assert.Contains("Manage this entry in Copilot", item.Notice);
        }
        Assert.True(catalog.Marketplaces[0].CanManage);
        Assert.True(catalog.Marketplaces[0].CanRefresh);
        Assert.False(catalog.Marketplaces[1].CanManage);
        Assert.True(catalog.Marketplaces[1].CanRefresh);
        Assert.False(catalog.Marketplaces[2].CanManage);
        Assert.False(catalog.Marketplaces[2].CanRefresh);
        Assert.Equal(
            [new[] { "plugin", "list", "--json" }, ["plugin", "marketplace", "list", "--json"],
             ["plugin", "marketplace", "browse", "team", "--json"],
             ["plugin", "marketplace", "browse", "awesome-copilot", "--json"],
             ["plugin", "marketplace", "browse", "policy", "--json"]], runner.Calls);
        Assert.All(runner.Tokens, token => Assert.Equal(cancel.Token, token));
        Assert.Equal("GitHub Copilot", service.Name);
        Assert.True(service.SupportsUpdate);
        Assert.True(service.SupportsToggle);
        Assert.Contains("shared with the desktop app", service.Guidance);
        Assert.Contains("active desktop sessions may need to reload", service.RestartNotice);
    }

    [Fact]
    public async Task SingleMarketplaceBrowse_UsesExactMarketplaceAndPreservesInstalledIdentity()
    {
        var runner = new Runner
        {
            Installed = """
                [{"name":"review","marketplace":"team","version":"2","enabled":false,"source":"installed"},
                 {"name":"built-in-tool","version":"1","enabled":true,"source":"builtin"}]
                """,
            Markets = """[{"name":"team","source":"owner/team"}]""",
            Browse = name => name == "team"
                ? """[{"name":"review","marketplace":"team","description":"Marketplace description"},{"name":"new","marketplace":"team"}]"""
                : throw new Xunit.Sdk.XunitException($"Unexpected marketplace browse: {name}"),
        };
        var service = new CopilotPluginService(runner);

        AgentPluginCatalog catalog = await service.GetCatalogAsync();

        Assert.Equal(
            [
                new[] { "plugin", "list", "--json" },
                new[] { "plugin", "marketplace", "list", "--json" },
                new[] { "plugin", "marketplace", "browse", "team", "--json" },
            ],
            runner.Calls);
        Assert.Equal(["built-in-tool", "new@team", "review@team"], catalog.Plugins.Select(plugin => plugin.Id));
        Assert.Equal(new AgentPluginItem("review@team", "Marketplace description", "2", true, false),
            catalog.Plugins.Single(plugin => plugin.Id == "review@team"));
        Assert.True(catalog.Plugins.Single(plugin => plugin.Id == "built-in-tool").IsInstalled);
        Assert.False(catalog.Plugins.Single(plugin => plugin.Id == "new@team").IsInstalled);
        var marketplace = Assert.Single(catalog.Marketplaces);
        Assert.Equal("team", marketplace.Name);
        Assert.Equal("owner/team", marketplace.Source);
        Assert.True(marketplace.CanManage);
        Assert.True(marketplace.CanRefresh);
    }

    [Fact]
    public async Task EmptyCatalogIsValidWithoutInventingPluginsOrMarketplaces()
    {
        var runner = new Runner();
        var result = await new CopilotPluginService(runner).GetCatalogAsync();
        Assert.Empty(result.Plugins);
        Assert.Empty(result.Marketplaces);
        Assert.Equal(2, runner.Calls.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(11)]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(14)]
    [InlineData(15)]
    public async Task InvalidNativeInventoryIsRejectedRatherThanClaimingEmptySuccess(int kind)
    {
        var runner = new Runner
        {
            Markets = """[{"name":"team","source":"owner/repo"}]""",
            Browse = _ => "[]",
        };
        switch (kind)
        {
            case 0: runner.Installed = "{}"; break;
            case 1: runner.Installed = "null"; break;
            case 2: runner.Installed = "[null]"; break;
            case 3: runner.Markets = "{}"; break;
            case 4: runner.Markets = "null"; break;
            case 5: runner.Markets = "[null]"; break;
            case 6: runner.Browse = _ => "null"; break;
            case 7: runner.Browse = _ => "[null]"; break;
            case 8: runner.Browse = _ => """[{"name":"review","marketplace":"other"}]"""; break;
            case 9: runner.Installed = """[{"name":"review","source":"installed"}]"""; break;
            case 10: runner.Installed = """[{"name":"review","enabled":false}]"""; break;
            case 11:
                runner.Installed = """[{"name":"review","source":"installed","enabled":true},{"name":"review","source":"installed","enabled":false}]""";
                break;
            case 12:
                runner.Browse = _ => """[{"name":"review","marketplace":"team"},{"name":"review","marketplace":"team"}]""";
                break;
            case 13: runner.Markets = """[{"name":"team"}]"""; break;
            case 14: runner.Markets = """[{"name":"team","source":"owner/repo"},{"name":"team","source":"other/repo"}]"""; break;
            case 15: runner.Browse = _ => "{}"; break;
        }
        await Assert.ThrowsAsync<JsonException>(() => new CopilotPluginService(runner).GetCatalogAsync());
        Assert.DoesNotContain(runner.Calls, args => args.Contains("install") || args.Contains("update"));
    }

    [Fact]
    public async Task CommandsNormalizePastedPrefixesAndPassUnknownSourcesAsOneLiteralWithoutForce()
    {
        var runner = new Runner();
        var service = new CopilotPluginService(runner);
        using var cancel = new CancellationTokenSource();
        foreach (string prefix in new[] { "", "copilot plugin marketplace add ", "copilot plugins marketplace add ", "/plugin marketplace add " })
            await service.AddMarketplaceAsync(prefix + "owner/repo", cancel.Token);
        await service.UpdateMarketplaceAsync("team", cancel.Token);
        await service.RemoveMarketplaceAsync("team", cancel.Token);
        foreach (string prefix in new[] { "", "copilot plugin install ", "copilot plugins install ", "copilot plugin add ", "/plugin install " })
            await service.PerformAsync(prefix + "review@team", AgentPluginAction.Install, cancel.Token);
        const string literal = @"C:\fixture plugins\odd;name&literal";
        await service.PerformAsync(literal, AgentPluginAction.Install, cancel.Token);
        foreach (var action in new[] { AgentPluginAction.Update, AgentPluginAction.Enable, AgentPluginAction.Disable, AgentPluginAction.Uninstall })
            await service.PerformAsync("review@team", action, cancel.Token);

        Assert.All(runner.Calls.Take(4), call => Assert.Equal(["plugin", "marketplace", "add", "owner/repo"], call));
        Assert.Equal(["plugin", "marketplace", "update", "team"], runner.Calls[4]);
        Assert.Equal(["plugin", "marketplace", "remove", "team"], runner.Calls[5]);
        Assert.All(runner.Calls.Skip(6).Take(5), call => Assert.Equal(["plugin", "install", "review@team"], call));
        Assert.Equal(["plugin", "install", literal], runner.Calls[11]);
        Assert.Equal(
            [new[] { "plugin", "update", "review@team" }, ["plugin", "enable", "review@team"],
             ["plugin", "disable", "review@team"], ["plugin", "uninstall", "review@team"]], runner.Calls.Skip(12));
        Assert.All(runner.Tokens, token => Assert.Equal(cancel.Token, token));
        Assert.DoesNotContain(runner.Calls, call => call.Contains("--force"));
    }

    [Fact]
    public async Task InvalidCommandInputsNeverReachRunner()
    {
        var runner = new Runner();
        var service = new CopilotPluginService(runner);
        foreach (string input in new[] { "", " ", "--help", "name\u0000suffix", "name\nsuffix" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => service.AddMarketplaceAsync(input));
            foreach (var action in Enum.GetValues<AgentPluginAction>())
                await Assert.ThrowsAsync<ArgumentException>(() => service.PerformAsync(input, action));
        }
        foreach (string name in new[] { "..", "team/name", "--all", "team name" })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => service.UpdateMarketplaceAsync(name));
            await Assert.ThrowsAsync<ArgumentException>(() => service.RemoveMarketplaceAsync(name));
        }
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.PerformAsync("review@team", (AgentPluginAction)100));
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task NativeFailuresAndCancellationPropagateWithOriginalToken()
    {
        var failure = new InvalidOperationException("fixture native error");
        var runner = new Runner { Failure = failure };
        var service = new CopilotPluginService(runner);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetCatalogAsync()));
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => service.PerformAsync("review@team", AgentPluginAction.Update)));
        runner.Failure = null;
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetCatalogAsync(cancel.Token));
        Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.Equal(3, runner.Calls.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppHandoffEncodesOfficialLauncherAndRequiresConfirmation(bool marketplace)
    {
        List<Uri> opened = [];
        var app = new CopilotAppPluginService(opened.Add);
        string status = marketplace
            ? await app.AddMarketplaceAsync("copilot plugins marketplace add https://example.test/team/plugins.git#v2")
            : await app.PerformAsync("/plugin install review@team", AgentPluginAction.Install);
        var uri = Assert.Single(opened);
        Assert.Equal("https", uri.Scheme);
        Assert.Equal("github.com", uri.Host);
        Assert.Equal("/copilot/app/launch", uri.AbsolutePath);
        Assert.Equal(marketplace
            ? "?open=ghapp%3A%2F%2Fplugins%2Fmarketplace%2Fadd%3Fsource%3Dhttps%253A%252F%252Fexample.test%252Fteam%252Fplugins.git%2523v2"
            : "?open=ghapp%3A%2F%2Fplugins%2Finstall%3Fsource%3Dreview%2540team", uri.Query);
        Assert.Equal("Opened the Copilot app confirmation form. Nothing is installed or added until you confirm in Copilot.", status);
        Assert.False(app.SupportsUpdate);
        Assert.False(app.SupportsToggle);
    }

    [Fact]
    public async Task AppRejectsInventoryLifecycleLocalPathsCredentialsAndInvalidIdentifiers()
    {
        List<Uri> opened = [];
        var app = new CopilotAppPluginService(opened.Add);
        await Assert.ThrowsAsync<NotSupportedException>(() => app.GetCatalogAsync());
        await Assert.ThrowsAsync<NotSupportedException>(() => app.UpdateMarketplaceAsync("team"));
        await Assert.ThrowsAsync<NotSupportedException>(() => app.RemoveMarketplaceAsync("team"));
        foreach (var action in new[] { AgentPluginAction.Update, AgentPluginAction.Enable, AgentPluginAction.Disable, AgentPluginAction.Uninstall })
            await Assert.ThrowsAsync<NotSupportedException>(() => app.PerformAsync("review@team", action));
        foreach (string source in new[] { @"C:\private\plugins", "/private/plugins", "../private", "owner/repo/extra",
                     "https://user:secret@example.test/plugins", "https://example.test/plugins?secret=token",
                     "http://example.test/plugins", "file:///private/plugins", "--help", "owner/repo\u0000" })
            await Assert.ThrowsAsync<ArgumentException>(() => app.AddMarketplaceAsync(source));
        foreach (string id in new[] { "review", "review@", "review@team@other", "review name@team", "--help@team" })
            await Assert.ThrowsAsync<ArgumentException>(() => app.PerformAsync(id, AgentPluginAction.Install));
        Assert.Empty(opened);
        await app.AddMarketplaceAsync("/plugin marketplace add owner/repo");
        Assert.EndsWith("owner%252Frepo", Assert.Single(opened).AbsoluteUri);
    }

    [Fact]
    public async Task AppCancellationAndLauncherFailureCannotClaimSuccess()
    {
        int opens = 0;
        var failure = new InvalidOperationException("fixture opener failed");
        var app = new CopilotAppPluginService(_ => { opens++; throw failure; });
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.AddMarketplaceAsync("owner/repo", cancel.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => app.PerformAsync("review@team", AgentPluginAction.Install, cancel.Token));
        Assert.Equal(0, opens);
        Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(() => app.AddMarketplaceAsync("owner/repo")));
        Assert.Equal(1, opens);
    }

    private sealed class Runner : IAgentPluginRunner
    {
        public string Installed { get; set; } = "[]";
        public string Markets { get; set; } = "[]";
        public Func<string, string> Browse { get; set; } = _ => "[]";
        public Exception? Failure { get; set; }
        public List<string[]> Calls { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken token)
        {
            Calls.Add(args.ToArray());
            Tokens.Add(token);
            token.ThrowIfCancellationRequested();
            if (Failure is { } failure) throw failure;
            return Task.FromResult(args[1] == "list" ? Installed
                : args.Count > 2 && args[2] == "list" ? Markets
                : args.Count > 2 && args[2] == "browse" ? Browse(args[3]) : "fixture output");
        }
    }
}
