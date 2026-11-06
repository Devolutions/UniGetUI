using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using UniGetUI.Avalonia.Infrastructure;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Avalonia.Views.Pages;
using UniGetUI.Extensions.AgentSkills;

namespace UniGetUI.Tests;

public sealed class ClaudePluginsViewModelTests
{
    private const string SkillsId = "ux-skills@devolutions-ux";
    private const string AgentsId = "ux-design-agents@devolutions-ux";
    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(ClaudePluginsViewModelTests).Assembly);

    [Fact]
    public async Task InstalledInventory_ReadsTheBareArrayClaudePrintsWithoutAvailable()
    {
        var runner = new FakeRunner
        {
            Override = (args, _) => args is ["plugin", "list", "--json"]
                ? Task.FromResult("""
                    [
                      {"id":"copilot-resources@copilot-resources","version":"5004c6ba4a15","scope":"user","enabled":true,
                       "installPath":"C:\\cache","errors":["Plugin copilot-resources not found in marketplace copilot-resources"],
                       "errorDetails":[{"type":"plugin-not-found"}],"projectEnabled":false},
                      {"id":"gitkraken-hooks@gitkraken","version":"3.1.76","scope":"user","enabled":false,"folderVersion":"3.1.76"},
                      {"id":"local@team","version":"1","scope":"project","enabled":true}
                    ]
                    """)
                : null,
        };

        var catalog = await new ClaudePluginService(runner).GetInstalledCatalogAsync();

        Assert.Equal(["plugin", "list", "--json"], runner.Calls[0]);
        Assert.Equal(["copilot-resources@copilot-resources", "gitkraken-hooks@gitkraken"],
            catalog.Plugins.Select(item => item.Id));
        Assert.All(catalog.Plugins, item => Assert.True(item.IsInstalled));
        Assert.Equal([true, false], catalog.Plugins.Select(item => item.Enabled));
        Assert.Equal("Plugin copilot-resources not found in marketplace copilot-resources", catalog.Plugins[0].Description);
        Assert.Equal(["devolutions-ux", "official"], catalog.Marketplaces.Select(item => item.Name));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PastedExample_RequiresTrustBeforeEachNativeUserMutation(bool accept) => _session.Dispatch<bool>(async () =>
    {
        var runner = new FakeRunner();
        runner.Items.RemoveAll(item => item.Marketplace == "devolutions-ux");
        runner.Markets.RemoveAll(item => item.Name == "devolutions-ux");
        List<string> prompts = [];
        var vm = Create(runner, message => { prompts.Add(message); return Task.FromResult(accept); });
        await vm.LoadAsync();
        runner.Calls.Clear();
        vm.Query = "invisible filter";
        vm.InstalledOnly = true;
        vm.MarketplaceInput = "/plugin marketplace add Devolutions/ux-prototypes";
        await vm.AddMarketplaceCommand.ExecuteAsync(null);
        Assert.Contains("Devolutions/ux-prototypes", prompts[0]);
        Assert.Contains("trust", prompts[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("executable", prompts[0]);
        if (accept)
        {
            Assert.Equal(["plugin", "marketplace", "add", "Devolutions/ux-prototypes", "--scope", "user"], runner.Calls[0]);
            AssertCatalogReads(runner.Calls.Skip(1).Take(2).ToArray());
            // Only the newly added marketplace is browsed.
            Assert.Equal(BrowseRead, runner.Calls[3]);
            Assert.Equal(4, runner.Calls.Count);
            Assert.Equal("", vm.MarketplaceInput);
            Assert.Equal("", vm.Query);
            Assert.False(vm.InstalledOnly);
            Assert.Equal("devolutions-ux", vm.SelectedMarketplace?.Name);
            Assert.Equal([AgentsId, SkillsId], vm.Plugins.Select(item => item.Id));
            Assert.Contains("Plugin marketplace added.", vm.Status);
            Assert.Contains("fixture output", vm.Activity);
        }
        else
        {
            Assert.Empty(runner.Calls);
            Assert.Equal("/plugin marketplace add Devolutions/ux-prototypes", vm.MarketplaceInput);
            Assert.Equal("invisible filter", vm.Query);
            Assert.True(vm.InstalledOnly);
        }
        foreach (string id in new[] { AgentsId, SkillsId })
        {
            runner.Calls.Clear();
            vm.PluginInput = "/plugin install " + id;
            await vm.InstallByIdCommand.ExecuteAsync(null);
            Assert.Contains(id, prompts[^1]);
            Assert.Contains("hooks", prompts[^1]);
            Assert.Contains("MCP servers", prompts[^1]);
            if (accept)
            {
                Assert.Equal(["plugin", "install", id, "--scope", "user"], runner.Calls[0]);
                AssertCatalogReads(runner.Calls.Skip(1).ToArray());
                Assert.True(vm.Plugins.Single(item => item.Id == id).IsInstalled);
                Assert.Contains("Plugin operation completed.", vm.Status);
                Assert.Contains("fixture output", vm.Activity);
                Assert.Contains("Restart active Claude Code sessions", vm.Activity);
            }
            else
            {
                Assert.Empty(runner.Calls);
                Assert.Equal("Cancelled", vm.Status);
            }
        }
        Assert.Equal(3, prompts.Count);
        Assert.False(vm.HasError);
        Assert.True(vm.CanWork);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task SelectedMarketplaceAndQueryFiltersIntersectWithoutAnotherCatalogRead() => _session.Dispatch<bool>(async () =>
    {
        var runner = new FakeRunner();
        var vm = Create(runner);
        await vm.LoadAsync();
        await vm.LoadAsync();
        Assert.Equal(2, runner.Calls.Count);
        Assert.True(vm.InstalledOnly);
        Assert.Equal([SkillsId, "ux-skills@official"], vm.Plugins.Select(item => item.Id));
        Assert.Equal(2, vm.Plugins.Count);
        Assert.Equal(2, runner.Calls.Count);
        vm.View = PluginLibraryView.Browse;
        foreach (var marketplace in vm.Marketplaces.ToArray())
        {
            vm.SelectedMarketplace = marketplace;
            await vm.BrowseSelectedMarketplaceCommand.ExecuteAsync(null);
        }
        vm.SelectedMarketplace = null;
        // Selecting each marketplace reuses Claude's one shared --available read.
        Assert.Equal(BrowseRead, Assert.Single(runner.Calls.Skip(2)));
        Assert.Equal(4, vm.Plugins.Count);
        vm.SelectedPlugin = vm.Plugins.Single(item => item.Id == SkillsId);
        vm.Query = "  DESIGN  ";
        Assert.Equal([AgentsId, SkillsId], vm.Plugins.Select(item => item.Id));
        Assert.Equal(SkillsId, vm.SelectedPlugin?.Id);
        vm.InstalledOnly = true;
        Assert.Equal(SkillsId, Assert.Single(vm.Plugins).Id);
        vm.SelectedMarketplace = vm.Marketplaces.Single(item => item.Name == "official");
        Assert.Empty(vm.Plugins);
        Assert.Null(vm.SelectedPlugin);
        vm.Query = "UX-SKILLS";
        Assert.Equal("ux-skills@official", Assert.Single(vm.Plugins).Id);
        vm.SelectedMarketplace = null;
        Assert.Null(vm.SelectedMarketplace);
        Assert.Equal([SkillsId, "ux-skills@official"], vm.Plugins.Select(item => item.Id));
        vm.Query = "";
        vm.InstalledOnly = false;
        Assert.Equal(4, vm.Plugins.Count);
        Assert.Equal(3, runner.Calls.Count);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(5, runner.Calls.Count);
        Assert.Equal(4, vm.Plugins.Count);
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(AgentPluginAction.Install, false)]
    [InlineData(AgentPluginAction.Install, true)]
    [InlineData(AgentPluginAction.Update, false)]
    [InlineData(AgentPluginAction.Update, true)]
    [InlineData(AgentPluginAction.Enable, false)]
    [InlineData(AgentPluginAction.Enable, true)]
    [InlineData(AgentPluginAction.Disable, false)]
    [InlineData(AgentPluginAction.Disable, true)]
    [InlineData(AgentPluginAction.Uninstall, false)]
    [InlineData(AgentPluginAction.Uninstall, true)]
    public Task SelectedActions_ConfirmAndRefreshAuthoritativeCatalog(AgentPluginAction action, bool accept) => _session.Dispatch<bool>(async () =>
    {
        var runner = new FakeRunner();
        List<string> prompts = [];
        var vm = Create(runner, message => { prompts.Add(message); return Task.FromResult(accept); });
        await LoadAndBrowseAsync(vm);
        string id = action == AgentPluginAction.Install ? AgentsId
            : action == AgentPluginAction.Enable ? "ux-skills@official" : SkillsId;
        vm.SelectedPlugin = vm.Plugins.Single(item => item.Id == id);
        runner.Calls.Clear();
        var before = vm.SelectedPlugin;
        await Command(vm, action).ExecuteAsync(null);
        string prompt = Assert.Single(prompts);
        Assert.Contains(id, prompt);
        if (action == AgentPluginAction.Uninstall)
        {
            Assert.Contains("delete", prompt);
            Assert.Contains("saved data, options and secrets", prompt);
        }
        if (!accept)
        {
            Assert.Empty(runner.Calls);
            Assert.Same(before, vm.SelectedPlugin);
            Assert.Equal("Cancelled", vm.Status);
        }
        else
        {
            Assert.Equal(["plugin", action.ToString().ToLowerInvariant(), id, "--scope", "user"], runner.Calls[0]);
            AssertCatalogReads(runner.Calls.Skip(1).ToArray());
            var selected = Assert.IsType<AgentPluginItem>(vm.SelectedPlugin);
            Assert.Equal(id, selected.Id);
            Assert.Equal(action != AgentPluginAction.Uninstall, selected.IsInstalled);
            if (action == AgentPluginAction.Update) Assert.Equal("2", selected.Version);
            if (action == AgentPluginAction.Enable) Assert.True(selected.Enabled);
            if (action == AgentPluginAction.Disable) Assert.False(selected.Enabled);
            Assert.Contains("Plugin operation completed.", vm.Status);
            Assert.Contains("fixture output", vm.Activity);
        }
        Assert.False(vm.HasError);
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RemoveMarketplace_ListsAffectedUserPluginsWarnsOfDeletionAndHonorsDecline(bool accept) => _session.Dispatch<bool>(async () =>
    {
        var runner = new FakeRunner();
        runner.Items.Add(new("hidden@devolutions-ux", "Saved plugin", "1", true, false));
        List<string> prompts = [];
        var vm = Create(runner, message => { prompts.Add(message); return Task.FromResult(accept); });
        await LoadAndBrowseAsync(vm);
        vm.SelectedMarketplace = vm.Marketplaces.Single(item => item.Name == "devolutions-ux");
        // The confirmation must use the full catalog, not just visible/filter-matching rows.
        vm.Query = "no matching plugin";
        runner.Calls.Clear();
        await vm.RemoveMarketplaceCommand.ExecuteAsync(null);
        string prompt = Assert.Single(prompts);
        Assert.Contains(SkillsId, prompt);
        Assert.Contains("hidden@devolutions-ux", prompt);
        Assert.DoesNotContain(AgentsId, prompt);
        Assert.DoesNotContain("ux-skills@official", prompt);
        Assert.Contains("last declaration", prompt);
        Assert.Contains("uninstalls", prompt);
        Assert.Contains("deletes their saved data, options and secrets", prompt);
        if (accept)
        {
            Assert.Equal(["plugin", "marketplace", "remove", "devolutions-ux", "--scope", "user"], runner.Calls[0]);
            AssertCatalogReads(runner.Calls.Skip(1).ToArray());
            Assert.Null(vm.SelectedMarketplace);
            Assert.Equal("official", Assert.Single(vm.Marketplaces).Name);
            vm.Query = "";
            Assert.Equal(["other@official", "ux-skills@official"], vm.Plugins.Select(item => item.Id));
            Assert.Contains("Plugin marketplace removed.", vm.Status);
            Assert.Contains("fixture output", vm.Activity);
        }
        else
        {
            Assert.Empty(runner.Calls);
            Assert.Equal(2, vm.Marketplaces.Count);
            Assert.Equal("devolutions-ux", vm.SelectedMarketplace?.Name);
            Assert.Equal("Cancelled", vm.Status);
        }
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task RefreshMarketplace_ReloadsCatalogAndRetainsSelectedMarketplace() => _session.Dispatch<bool>(async () =>
    {
        var runner = new FakeRunner();
        var vm = Create(runner, _ => throw new Xunit.Sdk.XunitException("Refresh does not change trust"));
        await vm.LoadAsync();
        vm.SelectedMarketplace = vm.Marketplaces.Single(item => item.Name == "devolutions-ux");
        runner.Calls.Clear();
        await vm.RefreshMarketplaceCommand.ExecuteAsync(null);
        Assert.Equal(["plugin", "marketplace", "update", "devolutions-ux"], runner.Calls[0]);
        Assert.Equal(BrowseRead, runner.Calls[1]);
        AssertCatalogReads(runner.Calls.Skip(2).ToArray());
        Assert.Equal("devolutions-ux", vm.SelectedMarketplace?.Name);
        Assert.Equal("2", vm.Plugins.Single(item => item.Id == SkillsId).Version);
        Assert.Contains("Plugin marketplace refreshed.", vm.Status);
        Assert.Contains("fixture output", vm.Activity);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task SelectionGates_RejectWrongStateWithoutPromptOrSubprocess() => _session.Dispatch<bool>(async () =>
    {
        var runner = new FakeRunner();
        var vm = Create(runner, _ => throw new Xunit.Sdk.XunitException("Invalid action must not prompt"));
        Assert.False(vm.HasSelection);
        Assert.False(vm.CanInstall);
        Assert.False(vm.CanManage);
        Assert.False(vm.CanEnable);
        Assert.False(vm.CanDisable);
        Assert.False(vm.CanRefreshMarketplace);
        foreach (var action in Enum.GetValues<AgentPluginAction>()) await Command(vm, action).ExecuteAsync(null);
        await LoadAndBrowseAsync(vm);
        runner.Calls.Clear();
        vm.SelectedPlugin = vm.Plugins.Single(item => item.Id == AgentsId);
        Assert.True(vm.HasSelection);
        Assert.True(vm.CanInstall);
        Assert.False(vm.CanManage);
        foreach (var action in Enum.GetValues<AgentPluginAction>().Where(action => action != AgentPluginAction.Install))
            await Command(vm, action).ExecuteAsync(null);
        vm.SelectedPlugin = vm.Plugins.Single(item => item.Id == SkillsId);
        Assert.False(vm.CanInstall);
        Assert.True(vm.CanManage);
        Assert.True(vm.CanDisable);
        Assert.False(vm.CanEnable);
        await vm.InstallCommand.ExecuteAsync(null);
        await vm.EnableCommand.ExecuteAsync(null);
        vm.SelectedPlugin = vm.Plugins.Single(item => item.Id == "ux-skills@official");
        Assert.True(vm.CanEnable);
        Assert.False(vm.CanDisable);
        await vm.DisableCommand.ExecuteAsync(null);
        Assert.Empty(runner.Calls);
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task MarketplaceActions_WithoutSelectionShowErrorWithoutPromptOrCli(bool remove) => _session.Dispatch<bool>(async () =>
    {
        var runner = new FakeRunner();
        var vm = Create(runner, _ => throw new Xunit.Sdk.XunitException("Missing selection must not prompt"));
        await (remove ? vm.RemoveMarketplaceCommand : vm.RefreshMarketplaceCommand).ExecuteAsync(null);
        Assert.Empty(runner.Calls);
        Assert.True(vm.HasError);
        Assert.Contains("Select a marketplace first.", vm.Status);
        Assert.False(vm.IsBusy);
        Assert.True(vm.CanWork);
        return true;
    }, CancellationToken.None);

    public enum FailurePoint { PluginInventory, MarketplaceInventory, Mutation, MalformedInventory, InvalidInput }

    [Theory]
    [InlineData(FailurePoint.PluginInventory)]
    [InlineData(FailurePoint.MarketplaceInventory)]
    [InlineData(FailurePoint.Mutation)]
    [InlineData(FailurePoint.MalformedInventory)]
    [InlineData(FailurePoint.InvalidInput)]
    public Task Errors_AreVisibleAssertiveAndRecoverOnNextRefresh(FailurePoint point) => _session.Dispatch<bool>(async () =>
    {
        var runner = new FakeRunner();
        var vm = Create(runner);
        List<AccessibilityAnnouncement> announcements = [];
        void Record(object? sender, AccessibilityAnnouncement message) => announcements.Add(message);
        AccessibilityAnnouncementService.AnnouncementRequested += Record;
        try
        {
            runner.Override = (args, _) => point switch
            {
                FailurePoint.PluginInventory when args[1] == "list" => Task.FromException<string>(new InvalidOperationException("inventory denied")),
                FailurePoint.MarketplaceInventory when args[1] == "marketplace" => Task.FromException<string>(new InvalidOperationException("marketplaces denied")),
                FailurePoint.MalformedInventory => Task.FromResult("{"),
                FailurePoint.Mutation when args[1] == "install" => Task.FromException<string>(new InvalidOperationException("install denied")),
                _ => null,
            };
            if (point is FailurePoint.Mutation or FailurePoint.InvalidInput)
            {
                vm.PluginInput = point == FailurePoint.InvalidInput ? "--help" : SkillsId;
                await vm.InstallByIdCommand.ExecuteAsync(null);
                Assert.Equal(point == FailurePoint.InvalidInput ? 0 : 1, runner.Calls.Count);
            }
            else await vm.RefreshCommand.ExecuteAsync(null);
            Assert.True(vm.HasError);
            Assert.False(vm.IsBusy);
            Assert.True(vm.CanWork);
            Assert.Contains(point is FailurePoint.Mutation or FailurePoint.InvalidInput
                ? "Could not manage Claude Code plugins:"
                : "Could not load installed plugins for Claude Code:", vm.Status);
            Assert.DoesNotContain("operation completed", vm.Status);
            if (point == FailurePoint.Mutation) Assert.Contains("install denied", vm.Status);
            global::Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.Contains(announcements, message => message.Message == vm.Status
                && message.LiveSetting == AutomationLiveSetting.Assertive);
            runner.Override = null;
            await vm.RefreshCommand.ExecuteAsync(null);
            Assert.False(vm.HasError);
            Assert.Equal([SkillsId, "ux-skills@official"], vm.Plugins.Select(item => item.Id));
            Assert.Equal("Loaded 2 installed plugins for Claude Code.", vm.Status);
        }
        finally { AccessibilityAnnouncementService.AnnouncementRequested -= Record; }
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task CancellationAndStop_ExcludeConcurrentWorkAndDiscardLateInventory(bool stop) => _session.Dispatch<bool>(async () =>
    {
        var runner = new FakeRunner();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken observed = default;
        runner.Override = (args, token) =>
        {
            observed = token;
            return args[1] == "list"
                ? Task.FromResult("""[{"id":"late@market","scope":"user","version":"1","enabled":true}]""")
                : release.Task;
        };
        var vm = Create(runner, _ => throw new Xunit.Sdk.XunitException("Busy operations must not prompt"));
        Task pending = vm.RefreshCommand.ExecuteAsync(null);
        Assert.True(vm.IsLoadingCatalog);
        Assert.True(vm.CanWork);
        Assert.False(vm.CanChange);
        Assert.True(observed.CanBeCanceled);
        vm.PluginInput = SkillsId;
        await vm.InstallByIdCommand.ExecuteAsync(null);
        await vm.AddMarketplaceCommand.ExecuteAsync(null);
        await vm.RefreshMarketplaceCommand.ExecuteAsync(null);
        AssertCatalogReads(runner.Calls);
        if (stop) vm.Stop();
        else vm.CancelCommand.Execute(null);
        Assert.True(observed.IsCancellationRequested);
        string statusAtStop = vm.Status;
        // A noncooperative CLI that returns after cancellation must not publish inventory.
        release.SetResult("[]");
        await pending.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(vm.Plugins);
        Assert.Empty(vm.Marketplaces);
        Assert.False(vm.IsBusy);
        Assert.False(vm.HasError);
        Assert.Equal(stop ? statusAtStop : "Plugin catalog loading cancelled.", vm.Status);
        runner.Override = null;
        runner.Calls.Clear();
        await vm.LoadAsync();
        if (stop)
        {
            Assert.Empty(runner.Calls);
            Assert.False(vm.CanWork);
        }
        else
        {
            AssertCatalogReads(runner.Calls);
            Assert.Equal([SkillsId, "ux-skills@official"], vm.Plugins.Select(item => item.Id));
            Assert.True(vm.CanWork);
        }
        return true;
    }, CancellationToken.None);

    public enum ConfirmationAction { AddMarketplace, InstallById, RemoveMarketplace }

    [Theory]
    [InlineData(ConfirmationAction.AddMarketplace, false)]
    [InlineData(ConfirmationAction.AddMarketplace, true)]
    [InlineData(ConfirmationAction.InstallById, false)]
    [InlineData(ConfirmationAction.InstallById, true)]
    [InlineData(ConfirmationAction.RemoveMarketplace, false)]
    [InlineData(ConfirmationAction.RemoveMarketplace, true)]
    public Task CancelDuringConfirmation_AcceptingLaterCannotStartMutation(ConfirmationAction action, bool stop) => _session.Dispatch<bool>(async () =>
    {
        var runner = new FakeRunner();
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = Create(runner, _ => decision.Task);
        vm.PluginInput = SkillsId;
        vm.MarketplaceInput = "Devolutions/ux-prototypes";
        vm.SelectedMarketplace = new("devolutions-ux", "Devolutions/ux-prototypes");
        Task pending = (action switch
        {
            ConfirmationAction.AddMarketplace => vm.AddMarketplaceCommand,
            ConfirmationAction.InstallById => vm.InstallByIdCommand,
            _ => vm.RemoveMarketplaceCommand,
        }).ExecuteAsync(null);
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanWork);
        if (stop) vm.Stop();
        else vm.CancelCommand.Execute(null);
        decision.SetResult(true);
        await pending.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(runner.Calls);
        Assert.False(vm.IsBusy);
        Assert.False(vm.HasError);
        Assert.Equal(!stop, vm.CanWork);
        return true;
    }, CancellationToken.None);

    private static AgentPluginsViewModel Create(FakeRunner runner, Func<string, Task<bool>>? confirm = null) =>
        new(new ClaudePluginService(runner), confirm ?? (_ => Task.FromResult(true)));

    private static IAsyncRelayCommand Command(AgentPluginsViewModel vm, AgentPluginAction action) => action switch
    {
        AgentPluginAction.Install => vm.InstallCommand,
        AgentPluginAction.Update => vm.UpdateCommand,
        AgentPluginAction.Enable => vm.EnableCommand,
        AgentPluginAction.Disable => vm.DisableCommand,
        AgentPluginAction.Uninstall => vm.UninstallCommand,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private static void AssertCatalogReads(IReadOnlyList<string[]> calls)
    {
        Assert.Equal(2, calls.Count);
        Assert.Equal(["plugin", "list", "--json"], calls[0]);
        Assert.Equal(["plugin", "marketplace", "list", "--json"], calls[1]);
    }

    private static readonly string[] BrowseRead = ["plugin", "list", "--json", "--available"];

    // Claude browses per marketplace, so the not-installed catalog appears only after a browse.
    private static async Task LoadAndBrowseAsync(AgentPluginsViewModel vm)
    {
        await vm.LoadAsync();
        vm.View = PluginLibraryView.Browse;
        foreach (var marketplace in vm.Marketplaces.ToArray())
        {
            vm.SelectedMarketplace = marketplace;
            await vm.BrowseSelectedMarketplaceCommand.ExecuteAsync(null);
        }
        vm.SelectedMarketplace = null;
        Assert.False(vm.HasError);
    }

    private sealed class FakeRunner : IAgentPluginRunner
    {
        public List<string[]> Calls { get; } = [];
        public List<AgentPluginItem> Items { get; } =
        [
            new(AgentsId, "Design agents", "1", false, false),
            new(SkillsId, "Design skills", "1", true, true),
            new("ux-skills@official", "Official tools", "3", true, false),
            new("other@official", "Other tools", "1", false, false),
        ];
        public List<AgentPluginMarketplace> Markets { get; } =
        [
            new("devolutions-ux", "Devolutions/ux-prototypes"),
            new("official", "official-source"),
        ];
        public Func<IReadOnlyList<string>, CancellationToken, Task<string>?>? Override { get; set; }

        public Task<string> RunAsync(IReadOnlyList<string> args, CancellationToken token)
        {
            Calls.Add(args.ToArray());
            if (Override?.Invoke(args, token) is { } overridden) return overridden;
            token.ThrowIfCancellationRequested();
            if (args[1] == "list")
            {
                var installed = Items.Where(item => item.IsInstalled).Select(item => new
                {
                    id = item.Id,
                    scope = "user",
                    version = item.Version,
                    enabled = item.Enabled,
                });
                // Matches Claude Code: a bare array unless --available is passed.
                return Task.FromResult(args.Contains("--available")
                    ? JsonSerializer.Serialize(new
                    {
                        installed,
                        available = Items.Select(item => new { pluginId = item.Id, description = item.Description, version = item.Version }),
                    })
                    : JsonSerializer.Serialize(installed));
            }
            if (args[1] == "marketplace")
            {
                switch (args[2])
                {
                    case "list":
                        return Task.FromResult(JsonSerializer.Serialize(Markets.Select(market => new { name = market.Name, source = market.Source })));
                    case "add":
                        Markets.Add(new("devolutions-ux", "Devolutions/ux-prototypes"));
                        Items.Add(new(AgentsId, "Design agents", "1", false, false));
                        Items.Add(new(SkillsId, "Design skills", "1", false, false));
                        break;
                    case "remove":
                        Markets.RemoveAll(market => market.Name == args[3]);
                        Items.RemoveAll(item => item.Marketplace == args[3]);
                        break;
                    case "update":
                        for (int i = 0; i < Items.Count; i++)
                            if (Items[i].Marketplace == args[3]) Items[i] = Items[i] with { Version = "2" };
                        break;
                }
            }
            else
            {
                int index = Items.FindIndex(item => item.Id == args[2]);
                var item = Items[index];
                Items[index] = args[1] switch
                {
                    "install" => item with { IsInstalled = true, Enabled = true },
                    "update" => item with { Version = "2" },
                    "enable" => item with { Enabled = true },
                    "disable" => item with { Enabled = false },
                    "uninstall" => item with { IsInstalled = false, Enabled = false },
                    _ => throw new InvalidOperationException("Unexpected fake command"),
                };
            }
            return Task.FromResult("fixture output");
        }
    }
}
