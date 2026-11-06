using Avalonia.Headless;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Extensions.AgentSkills;

namespace UniGetUI.Tests;

public sealed class AgentPluginsMultiHostViewModelTests
{
    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(AgentPluginsMultiHostViewModelTests).Assembly);

    [Fact]
    public Task ProviderSelection_ClearsHostStateAndLoadsOnlyTheNewCatalog() => _session.Dispatch<bool>(async () =>
    {
        var claude = new ProviderStub("Claude", supportsUpdate: true, supportsToggle: true)
        {
            Catalog = new(
                [new("claude-plugin@claude-market", "Claude catalog", "1", true, true)],
                [new("claude-market", "claude source")]),
        };
        var codex = new ProviderStub("Codex", supportsUpdate: false, supportsToggle: false)
        {
            Catalog = new(
                [new("codex-plugin@codex-market", "Codex catalog", "2", false, false)],
                [new("codex-market", "codex source")]),
        };
        var vm = new AgentPluginsViewModel([claude, codex], _ => Task.FromResult(true));
        await vm.LoadAsync();
        vm.SelectedPlugin = vm.Plugins.Single();
        vm.SelectedMarketplace = vm.Marketplaces.Single();
        vm.Query = "Claude";
        vm.Status = "stale status";
        vm.HasError = true;
        vm.Activity = "old host output";
        vm.ShowRestartNotice = true;
        vm.NeedsRefresh = true;

        vm.SelectedProvider = codex;

        Assert.Same(codex, vm.SelectedProvider);
        Assert.Empty(vm.Plugins);
        Assert.Empty(vm.Marketplaces);
        Assert.Null(vm.SelectedPlugin);
        Assert.Null(vm.SelectedMarketplace);
        Assert.Equal("", vm.Query);
        Assert.Equal("", vm.Status);
        Assert.False(vm.HasError);
        Assert.False(vm.NeedsRefresh);
        Assert.False(vm.ShowRestartNotice);
        Assert.Equal("", vm.Activity);
        Assert.Equal("Codex guidance", vm.Guidance);
        Assert.Equal("Codex restart", vm.RestartNotice);
        Assert.False(vm.SupportsUpdate);
        Assert.False(vm.SupportsToggle);
        Assert.Equal(0, codex.CatalogReads);

        await vm.LoadAsync();

        Assert.Equal(1, claude.CatalogReads);
        Assert.Equal(1, codex.CatalogReads);
        Assert.Equal("codex-plugin@codex-market", Assert.Single(vm.Plugins).Id);
        Assert.Equal("codex-market", Assert.Single(vm.Marketplaces).Name);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task ProviderSwitchDuringCatalogRead_CancelsOldRequestAndRejectsLateCompletion() =>
        _session.Dispatch<bool>(async () =>
        {
            var oldResult = new TaskCompletionSource<AgentPluginCatalog>(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldReadStarted = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
            var oldProvider = new ProviderStub("Old provider", true, true)
            {
                CatalogRead = token =>
                {
                    oldReadStarted.TrySetResult(token);
                    return oldResult.Task;
                },
            };
            var currentProvider = new ProviderStub("Current provider", true, true)
            {
                Catalog = new(
                    [new("current@market", "Current provider result", "2", true, true)],
                    [new("market", "current source")]),
            };
            var vm = new AgentPluginsViewModel([oldProvider, currentProvider], _ => Task.FromResult(true));

            Task oldLoad = vm.LoadAsync();
            CancellationToken oldToken = await oldReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            vm.SelectedProvider = currentProvider;
            bool switchedWhileBusy = ReferenceEquals(currentProvider, vm.SelectedProvider);
            bool oldRequestCancelled = oldToken.IsCancellationRequested;
            oldResult.SetResult(new(
                [new("late@market", "Must not replace the current provider", "1", false, false)],
                [new("market", "old source")]));
            await oldLoad.WaitAsync(TimeSpan.FromSeconds(10));
            if (switchedWhileBusy) await vm.LoadAsync();

            Assert.True(switchedWhileBusy, "Selecting a provider during a catalog read must take effect immediately.");
            Assert.True(oldRequestCancelled, "Switching providers must cancel the previous provider's read.");
            Assert.Same(currentProvider, vm.SelectedProvider);
            Assert.Equal("current@market", Assert.Single(vm.Plugins).Id);
            Assert.Equal("current source", Assert.Single(vm.Marketplaces).Source);
            Assert.Equal(1, currentProvider.CatalogReads);
            vm.Stop();
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task ReselectingProvider_RestoresSnapshotWithoutAnotherCatalogRead() =>
        _session.Dispatch<bool>(async () =>
        {
            var firstProvider = new ProviderStub("First provider", true, true)
            {
                Catalog = new(
                    [new("first@market", "First provider snapshot", "1", true, false)],
                    [new("market", "first source")]),
            };
            var secondProvider = new ProviderStub("Second provider", true, true)
            {
                Catalog = new(
                    [new("second@market", "Second provider snapshot", "2", false, false)],
                    [new("market", "second source")]),
            };
            var vm = new AgentPluginsViewModel([firstProvider, secondProvider], _ => Task.FromResult(true));

            await vm.LoadAsync();
            vm.SelectedProvider = secondProvider;
            await vm.LoadAsync();
            Assert.Equal("second@market", Assert.Single(vm.Plugins).Id);

            vm.SelectedProvider = firstProvider;
            await vm.LoadAsync();

            Assert.Equal("first@market", Assert.Single(vm.Plugins).Id);
            Assert.Equal("first source", Assert.Single(vm.Marketplaces).Source);
            Assert.Equal(1, firstProvider.CatalogReads);
            Assert.Equal(1, secondProvider.CatalogReads);
            vm.Stop();
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task ReselectingProvider_RestoresQueryViewMarketplaceAndSelectionWithoutRefetch() =>
        _session.Dispatch<bool>(async () =>
        {
            var firstProvider = new ProviderStub("First provider", true, true)
            {
                SupportsScopedBrowse = true,
                InstalledCatalog = new([], [new("team", "owner/team"), new("community", "owner/community")]),
                MarketplaceCatalogs = new()
                {
                    ["team"] = new(
                        [new("alpha@team", "Alpha plugin", "1", false, false),
                         new("beta@team", "Beta plugin", "1", false, false)], []),
                },
            };
            var secondProvider = new ProviderStub("Second provider", true, true)
            {
                SupportsScopedBrowse = true,
                InstalledCatalog = new([], [new("other", "owner/other")]),
            };
            var vm = new AgentPluginsViewModel([firstProvider, secondProvider], _ => Task.FromResult(true));

            await vm.LoadAsync();
            vm.View = PluginLibraryView.Browse;
            vm.SelectedMarketplace = vm.Marketplaces.Single(item => item.Name == "team");
            await vm.BrowseSelectedMarketplaceCommand.ExecuteAsync(null);
            vm.Query = "ALPHA";
            vm.SelectedPlugin = Assert.Single(vm.Plugins);
            Assert.Equal("alpha@team", vm.SelectedPlugin.Id);

            vm.SelectedProvider = secondProvider;
            await vm.LoadAsync();
            Assert.Equal("other", Assert.Single(vm.Marketplaces).Name);

            vm.SelectedProvider = firstProvider;
            await vm.LoadAsync();

            Assert.Same(firstProvider, vm.SelectedProvider);
            Assert.Equal("ALPHA", vm.Query);
            Assert.Equal(PluginLibraryView.Browse, vm.View);
            Assert.Equal("team", vm.SelectedMarketplace?.Name);
            Assert.Equal("alpha@team", vm.SelectedPlugin?.Id);
            Assert.Equal("alpha@team", Assert.Single(vm.Plugins).Id);
            Assert.Equal(1, firstProvider.InstalledCatalogReads);
            Assert.Equal(1, firstProvider.MarketplaceCatalogReads);
            Assert.Equal(1, secondProvider.InstalledCatalogReads);
            Assert.Equal(0, secondProvider.MarketplaceCatalogReads);
            vm.Stop();
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task ScopedMarketplaceBrowseShowsLoadingErrorsAndRetriesOnlySelectedSource() =>
        _session.Dispatch<bool>(async () =>
        {
            List<string> requestedSources = [];
            var firstRead = new TaskCompletionSource<AgentPluginCatalog>(TaskCreationOptions.RunContinuationsAsynchronously);
            var provider = new ProviderStub("Selected provider", true, true)
            {
                SupportsScopedBrowse = true,
                InstalledCatalog = new([], [new("selected", "owner/selected"), new("untouched", "owner/untouched")]),
                MarketplaceCatalogRead = (name, _) =>
                {
                    requestedSources.Add(name);
                    return requestedSources.Count == 1
                        ? firstRead.Task
                        : Task.FromResult(new AgentPluginCatalog(
                            [new("review@selected", "Review plugin", "1", false, false)], []));
                },
            };
            var otherProvider = new ProviderStub("Unselected provider", true, true)
            {
                SupportsScopedBrowse = true,
                InstalledCatalog = new([], [new("other", "owner/other")]),
            };
            var vm = new AgentPluginsViewModel([provider, otherProvider], _ => Task.FromResult(true));
            await vm.LoadAsync();
            vm.View = PluginLibraryView.Browse;
            vm.SelectedMarketplace = vm.Marketplaces.Single(item => item.Name == "selected");

            Task browsing = vm.BrowseSelectedMarketplaceCommand.ExecuteAsync(null);
            Assert.True(vm.IsLoadingCatalog);
            Assert.True(vm.IsWorking);
            Assert.False(vm.CanChange);
            Assert.Equal(["selected"], requestedSources);
            Assert.Equal(1, provider.MarketplaceCatalogReads);
            Assert.Equal(0, otherProvider.InstalledCatalogReads);
            Assert.Equal(0, otherProvider.MarketplaceCatalogReads);

            firstRead.SetException(new InvalidOperationException("fixture source unavailable"));
            await browsing;
            Assert.False(vm.IsLoadingCatalog);
            Assert.False(vm.IsWorking);
            Assert.True(vm.HasError);
            Assert.Contains("fixture source unavailable", vm.Status);
            Assert.Empty(vm.Plugins);
            Assert.True(vm.CanRetry);

            await vm.BrowseSelectedMarketplaceCommand.ExecuteAsync(null);

            Assert.Equal(["selected", "selected"], requestedSources);
            Assert.False(vm.IsLoadingCatalog);
            Assert.False(vm.HasError);
            Assert.Equal("review@selected", Assert.Single(vm.Plugins).Id);
            Assert.Equal("Loaded 1 plugins from selected.", vm.Status);
            Assert.Equal(1, provider.InstalledCatalogReads);
            Assert.Equal(0, otherProvider.InstalledCatalogReads);
            Assert.Equal(0, otherProvider.MarketplaceCatalogReads);
            vm.Stop();
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task PluginUpdateIsAnExplicitOperationOnTheSelectedProviderOnly() =>
        _session.Dispatch<bool>(async () =>
        {
            var selectedProvider = new ProviderStub("Selected provider", true, true)
            {
                Catalog = new([new("plugin@selected", "Installed plugin", "1", true, true)], []),
            };
            var otherProvider = new ProviderStub("Other provider", true, true)
            {
                Catalog = new([new("plugin@other", "Other installed plugin", "1", true, true)], []),
            };
            var vm = new AgentPluginsViewModel([selectedProvider, otherProvider], _ => Task.FromResult(true));

            await vm.LoadAsync();

            Assert.Empty(selectedProvider.Actions);
            Assert.Equal(1, selectedProvider.CatalogReads);
            Assert.Equal(0, otherProvider.CatalogReads);
            vm.SelectedPlugin = Assert.Single(vm.Plugins);
            Assert.True(vm.CanUpdate);
            await vm.UpdateCommand.ExecuteAsync(null);

            Assert.Equal([("plugin@selected", AgentPluginAction.Update)], selectedProvider.Actions);
            Assert.Empty(otherProvider.Actions);
            Assert.Equal(2, selectedProvider.CatalogReads);
            Assert.Equal(0, otherProvider.CatalogReads);
            Assert.False(vm.HasError, vm.Status);
            Assert.False(vm.NeedsRefresh);
            vm.Stop();
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task ProviderCapabilitiesAndPolicy_GateActionsButAllowClearingReadOnlyMarketplaceFilter() =>
        _session.Dispatch<bool>(async () =>
        {
            var codex = new ProviderStub("Codex", supportsUpdate: false, supportsToggle: false)
            {
                Catalog = new(
                    [
                        new AgentPluginItem("blocked@built-in", "Policy blocked", "1", false, false)
                            { CanInstall = false, Notice = "Unavailable under policy." },
                        new("installed@built-in", "Installed", "2", true, false),
                    ],
                    [new("built-in", "Agent-owned source") { CanManage = false }]),
            };
            var cursor = new ProviderStub("Cursor", supportsUpdate: true, supportsToggle: false)
            {
                Catalog = new(
                    [
                        new("available@cursor-market", "Available", "3", false, false),
                        new("installed@cursor-market", "Installed", "4", true, true),
                    ],
                    [new("cursor-market", "local source")]),
            };
            var vm = new AgentPluginsViewModel([codex, cursor], _ => Task.FromResult(true));
            await vm.LoadAsync();

            vm.SelectedPlugin = vm.Plugins.Single(item => item.Id == "blocked@built-in");
            Assert.False(vm.CanInstall);
            Assert.False(vm.CanManage);
            vm.SelectedPlugin = vm.Plugins.Single(item => item.Id == "installed@built-in");
            Assert.True(vm.CanManage);
            Assert.False(vm.CanUpdate);
            Assert.False(vm.CanEnable);
            Assert.False(vm.CanDisable);
            vm.SelectedMarketplace = vm.Marketplaces.Single();
            Assert.True(vm.HasMarketplaceFilter);
            Assert.False(vm.CanRefreshMarketplace);
            Assert.True(vm.CanWork);
            vm.SelectedMarketplace = null;
            Assert.Null(vm.SelectedMarketplace);
            Assert.False(vm.HasMarketplaceFilter);
            Assert.Equal(2, vm.Plugins.Count);

            vm.SelectedProvider = cursor;
            await vm.LoadAsync();
            vm.SelectedPlugin = vm.Plugins.Single(item => item.Id == "available@cursor-market");
            Assert.True(vm.CanInstall);
            Assert.False(vm.CanUpdate);
            vm.SelectedPlugin = vm.Plugins.Single(item => item.Id == "installed@cursor-market");
            Assert.True(vm.CanManage);
            Assert.True(vm.CanUpdate);
            Assert.False(vm.CanEnable);
            Assert.False(vm.CanDisable);
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task Actions_UseCurrentProviderForTrustConfirmationAndRestartStatus() => _session.Dispatch<bool>(async () =>
    {
        var codex = new ProviderStub("Codex", supportsUpdate: false, supportsToggle: false)
        {
            Catalog = new([new("plugin@codex-market", "Codex plugin", "1", false, false)], []),
        };
        var cursor = new ProviderStub("Cursor", supportsUpdate: true, supportsToggle: false)
        {
            Catalog = new([new("cursor-plugin@cursor-market", "Cursor plugin", "1", false, false)], []),
        };
        List<string> prompts = [];
        var vm = new AgentPluginsViewModel([codex, cursor], message =>
        {
            prompts.Add(message);
            return Task.FromResult(true);
        });
        await vm.LoadAsync();
        vm.SelectedPlugin = vm.Plugins.Single();

        await vm.InstallCommand.ExecuteAsync(null);

        Assert.Contains("for Codex", Assert.Single(prompts));
        Assert.Contains("Codex", prompts[0]);
        Assert.Equal(("plugin@codex-market", AgentPluginAction.Install), Assert.Single(codex.Actions));
        Assert.Contains("Plugin operation completed.", vm.Status);
        Assert.Contains("Codex result", vm.Activity);
        Assert.Contains("Codex restart", vm.Activity);

        vm.SelectedProvider = cursor;
        await vm.LoadAsync();
        vm.MarketplaceInput = "owner/cursor-market";
        await vm.AddMarketplaceCommand.ExecuteAsync(null);

        Assert.Contains("for Cursor", prompts[1]);
        Assert.Contains("owner/cursor-market", prompts[1]);
        Assert.Single(cursor.AddedSources);
        Assert.Contains("Plugin marketplace added.", vm.Status);
        Assert.Contains("Cursor source added.", vm.Activity);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task CatalogReadAllowsProviderSwitchAndDiscardsOldResult() =>
        _session.Dispatch<bool>(async () =>
        {
            var lateCatalog = new TaskCompletionSource<AgentPluginCatalog>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var claude = new ProviderStub("Claude", supportsUpdate: true, supportsToggle: true)
            {
                CatalogRead = _ =>
                {
                    entered.SetResult();
                    return lateCatalog.Task;
                },
            };
            var codex = new ProviderStub("Codex", supportsUpdate: false, supportsToggle: false)
            {
                Catalog = new([new("fresh@codex-market", "Fresh catalog", "1", false, false)], []),
            };
            var vm = new AgentPluginsViewModel([claude, codex], _ => Task.FromResult(true));

            Task pending = vm.RefreshCommand.ExecuteAsync(null);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(vm.IsLoadingCatalog);
            vm.SelectedProvider = codex;
            Assert.Same(codex, vm.SelectedProvider);
            Assert.Equal(0, codex.CatalogReads);
            lateCatalog.SetResult(new(
                [new("late@claude-market", "Must be discarded", "1", false, false)],
                [new("claude-market", "late source")]));
            await pending.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Empty(vm.Plugins);
            Assert.Empty(vm.Marketplaces);
            Assert.False(vm.IsLoadingCatalog);
            await vm.LoadAsync();
            Assert.Equal("fresh@codex-market", Assert.Single(vm.Plugins).Id);
            Assert.Equal(1, codex.CatalogReads);
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task ProviderFailure_IsScopedToSelectedHostAndStopDiscardsLateDataPermanently() =>
        _session.Dispatch<bool>(async () =>
        {
            var lateCatalog = new TaskCompletionSource<AgentPluginCatalog>(TaskCreationOptions.RunContinuationsAsynchronously);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var codex = new ProviderStub("Codex", supportsUpdate: false, supportsToggle: false)
            {
                CatalogRead = _ => Task.FromException<AgentPluginCatalog>(new InvalidOperationException("Codex fixture error")),
            };
            var cursor = new ProviderStub("Cursor", supportsUpdate: true, supportsToggle: false)
            {
                CatalogRead = _ =>
                {
                    entered.SetResult();
                    return lateCatalog.Task;
                },
            };
            var vm = new AgentPluginsViewModel([codex, cursor], _ => Task.FromResult(true));
            await vm.LoadAsync();
            Assert.True(vm.HasError);
            Assert.Contains("Could not load installed plugins for Codex:", vm.Status);
            Assert.Contains("Codex fixture error", vm.Status);

            vm.SelectedProvider = cursor;
            Assert.False(vm.HasError);
            Assert.Equal("", vm.Status);
            Task pending = vm.RefreshCommand.ExecuteAsync(null);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            vm.Stop();
            lateCatalog.SetResult(new(
                [new("must-not-appear@cursor-market", "Late", "1", false, false)],
                [new("cursor-market", "late source")]));
            await pending.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Empty(vm.Plugins);
            Assert.Empty(vm.Marketplaces);
            Assert.False(vm.CanWork);
            vm.SelectedProvider = codex;
            Assert.Same(cursor, vm.SelectedProvider);
            Assert.Equal(1, codex.CatalogReads);
            Assert.Equal(1, cursor.CatalogReads);
            return true;
        }, CancellationToken.None);

    [Theory]
    [InlineData(AgentPluginAction.Install)]
    [InlineData(AgentPluginAction.Update)]
    [InlineData(AgentPluginAction.Enable)]
    [InlineData(AgentPluginAction.Disable)]
    [InlineData(AgentPluginAction.Uninstall)]
    public Task InlineActionsUseTheClickedPluginAndRefreshAuthoritativeState(AgentPluginAction action) =>
        _session.Dispatch<bool>(async () =>
        {
            bool installed = action != AgentPluginAction.Install;
            bool enabled = action != AgentPluginAction.Enable;
            var provider = new ProviderStub("Claude Code", true, true)
            {
                Catalog = new([
                    new("other@ux", "Other plugin", "1", true, true),
                    new("target@ux", "Target plugin", "1", installed, enabled),
                ], []),
            };
            List<string> prompts = [];
            var vm = new AgentPluginsViewModel(provider, message =>
            {
                prompts.Add(message);
                return Task.FromResult(true);
            });
            provider.PerformResult = (id, operation, _) =>
            {
                provider.Catalog = provider.Catalog with
                {
                    Plugins = provider.Catalog.Plugins.Select(item => item.Id != id ? item : item with
                    {
                        IsInstalled = operation != AgentPluginAction.Uninstall,
                        Enabled = operation != AgentPluginAction.Disable,
                        Version = operation == AgentPluginAction.Update ? "2" : "1",
                    }).ToArray(),
                };
                return Task.FromResult("changed");
            };
            await vm.LoadAsync();
            vm.SelectedPlugin = vm.Plugins.Single(item => item.Id == "other@ux");
            var target = vm.Plugins.Single(item => item.Id == "target@ux");
            var command = action switch
            {
                AgentPluginAction.Install => vm.InstallItemCommand,
                AgentPluginAction.Update => vm.UpdateItemCommand,
                AgentPluginAction.Enable or AgentPluginAction.Disable => vm.ToggleItemCommand,
                _ => vm.UninstallItemCommand,
            };
            await command.ExecuteAsync(target);

            Assert.Equal([("target@ux", action)], provider.Actions);
            Assert.Contains("target@ux", Assert.Single(prompts));
            Assert.DoesNotContain("other@ux", prompts[0]);
            Assert.Equal(2, provider.CatalogReads);
            var result = vm.Plugins.Single(item => item.Id == "target@ux");
            Assert.Equal(action != AgentPluginAction.Uninstall, result.IsInstalled);
            Assert.Equal(action != AgentPluginAction.Disable, result.Enabled);
            Assert.Equal(action == AgentPluginAction.Update ? "2" : "1", result.Version);
            Assert.False(vm.HasError);
            Assert.False(vm.NeedsRefresh);
            Assert.True(vm.ShowRestartNotice);
            vm.Stop();
            return true;
        }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task InlineToggleDoesNotChangeStateWhenDeclinedOrFailed(bool fail) => _session.Dispatch<bool>(async () =>
    {
        var provider = new ProviderStub("Claude Code", true, true)
        {
            Catalog = new([new("target@ux", "Target plugin", "1", true, true)], []),
            PerformResult = (_, _, _) => Task.FromException<string>(new InvalidOperationException("fixture toggle failed")),
        };
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new AgentPluginsViewModel(provider, _ => confirmation.Task);
        await vm.LoadAsync();
        Task toggling = vm.ToggleItemCommand.ExecuteAsync(vm.Plugins[0]);
        Assert.True(vm.IsBusy);
        await vm.UninstallItemCommand.ExecuteAsync(vm.Plugins[0]);
        Assert.Empty(provider.Actions);
        confirmation.SetResult(fail);
        await toggling;
        Assert.True(vm.Plugins[0].Enabled);
        Assert.Equal(fail, vm.HasError);
        Assert.Equal(fail, vm.NeedsRefresh);
        Assert.Equal(fail ? 1 : 0, provider.Actions.Count);
        Assert.Contains(fail ? "fixture toggle failed" : "Cancelled", vm.Status);
        Assert.Equal(1, provider.CatalogReads);
        vm.Stop();
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(true, true, false, true)]
    [InlineData(true, true, false, false)]
    [InlineData(false, false, true, true)]
    public Task InlineActionsRespectProviderAndPluginCapabilities(bool supportsToggle, bool supportsUpdate,
        bool canManage, bool canToggle) => _session.Dispatch<bool>(async () =>
    {
        var provider = new ProviderStub("Agent", supportsUpdate, supportsToggle)
        {
            Catalog = new([new("target@ux", "Target", "1", true, true) { CanManage = canManage, CanToggle = canToggle }], []),
        };
        int prompts = 0;
        var vm = new AgentPluginsViewModel(provider, _ =>
        {
            prompts++;
            return Task.FromResult(false);
        });
        await vm.LoadAsync();
        vm.SelectedPlugin = vm.Plugins[0];
        Assert.Equal(supportsToggle && canToggle, vm.CanDisable);
        Assert.Equal(supportsUpdate && canManage, vm.CanUpdate);
        Assert.Equal(canManage, vm.CanManage);
        if (!supportsUpdate || !canManage) await vm.UpdateItemCommand.ExecuteAsync(vm.Plugins[0]);
        if (!supportsToggle || !canToggle) await vm.ToggleItemCommand.ExecuteAsync(vm.Plugins[0]);
        if (!canManage) await vm.UninstallItemCommand.ExecuteAsync(vm.Plugins[0]);
        var stale = vm.Plugins[0];
        await vm.RefreshCommand.ExecuteAsync(null);
        // A value-equal row from an earlier snapshot must not target the current inventory.
        await vm.UpdateItemCommand.ExecuteAsync(stale with { });
        await vm.ToggleItemCommand.ExecuteAsync(stale with { });
        await vm.UninstallItemCommand.ExecuteAsync(stale with { });
        Assert.Equal(0, prompts);
        Assert.Equal("", vm.Activity);
        Assert.Empty(provider.Actions);
        vm.Stop();
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task TenThousandPluginCatalogFiltersAndPagesLocallyWithoutBackendCalls() => _session.Dispatch<bool>(async () =>
    {
        var provider = new ProviderStub("Codex", false, false)
        {
            Catalog = new(Enumerable.Range(0, 10000).Select(i =>
                new AgentPluginItem($"plugin-{i:D5}@catalog", $"Tool number {i}", "1", i % 2 == 0, false)).ToArray(),
                [new("catalog", "fixture")]),
        };
        var vm = new AgentPluginsViewModel(provider, _ => throw new Xunit.Sdk.XunitException("Filtering must not prompt"));
        await vm.LoadAsync();
        Assert.Equal(100, vm.Plugins.Count);
        vm.Query = "plugin-09999";
        Assert.Equal("plugin-09999@catalog", Assert.Single(vm.Plugins).Id);
        vm.Query = "";
        Assert.Equal(100, vm.Plugins.Count);
        Assert.True(vm.CanLoadMore);
        vm.LoadMoreCommand.Execute(null);
        Assert.Equal(200, vm.Plugins.Count);
        Assert.Equal("plugin-00199@catalog", vm.Plugins[^1].Id);
        Assert.Equal(1, provider.CatalogReads);
        Assert.Empty(provider.Actions);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task LargeCatalog_PagesVisibleResultsButSearchStillFindsBeyondTheFirstPage() =>
        _session.Dispatch<bool>(async () =>
        {
            const int catalogSize = 1000;
            var provider = new ProviderStub("Codex", false, false)
            {
                Catalog = new(Enumerable.Range(0, catalogSize).Select(i =>
                    new AgentPluginItem($"plugin-{i:D4}@catalog", $"Tool number {i}", "1", false, false)).ToArray(),
                    [new("catalog", "fixture")]),
            };
            var vm = new AgentPluginsViewModel(provider, _ => throw new Xunit.Sdk.XunitException("Searching must not prompt"));
            await vm.LoadAsync();
            int firstPageCount = vm.Plugins.Count;
            Assert.Equal(100, firstPageCount);
            Assert.True(vm.CanLoadMore);

            vm.Query = "plugin-0999";

            Assert.Equal("plugin-0999@catalog", Assert.Single(vm.Plugins).Id);
            Assert.Equal(1, provider.CatalogReads);
            Assert.InRange(firstPageCount, 1, catalogSize - 1);
            vm.Query = "";
            Assert.Equal(100, vm.Plugins.Count);
            vm.LoadMoreCommand.Execute(null);
            Assert.Equal(200, vm.Plugins.Count);
            vm.Stop();
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task MarketplaceBrowseUsesPersistentCacheAcrossProviderInstances() =>
        _session.Dispatch<bool>(async () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "UniGetUI-PluginVmCache-" + Guid.NewGuid().ToString("N"));
            try
            {
                AgentPluginCatalog installed = new([], [new("team", "owner/team")]);
                var firstProvider = new ProviderStub("Codex", false, false)
                {
                    SupportsScopedBrowse = true,
                    InstalledCatalog = installed,
                    MarketplaceCatalogs = new()
                    {
                        ["team"] = new([new("review@team", "Review tools", "1", false, false)], []),
                    },
                };
                var cache = new AgentPluginCatalogCache(directory);
                var first = new AgentPluginsViewModel([firstProvider], _ => Task.FromResult(true), cache);
                await first.LoadAsync();
                first.View = PluginLibraryView.Browse;
                first.SelectedMarketplace = first.Marketplaces.Single();
                await first.BrowseSelectedMarketplaceCommand.ExecuteAsync(null);
                Assert.Equal(1, firstProvider.MarketplaceCatalogReads);
                first.Stop();

                var secondProvider = new ProviderStub("Codex", false, false)
                {
                    SupportsScopedBrowse = true,
                    InstalledCatalog = installed,
                };
                var second = new AgentPluginsViewModel([secondProvider], _ => Task.FromResult(true), cache);
                await second.LoadAsync();
                second.View = PluginLibraryView.Browse;
                second.SelectedMarketplace = second.Marketplaces.Single();
                await second.BrowseSelectedMarketplaceCommand.ExecuteAsync(null);

                Assert.Equal("review@team", Assert.Single(second.Plugins).Id);
                Assert.Equal(0, secondProvider.MarketplaceCatalogReads);
                Assert.Contains("cached", second.Status);
                second.Stop();
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            }
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task PluginRowInstallUsesParameterAcrossConfirmationAndRefreshesInstalledBadge() => _session.Dispatch<bool>(async () =>
    {
        var provider = new ProviderStub("Claude Code", true, true)
        {
            Catalog = new([new("first@ux", "First", "1", false, false), new("target@ux", "Target", "1", false, false)], [new("ux", "fixture")]),
            ApplyMutations = true,
        };
        var confirm = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string? prompt = null;
        var vm = new AgentPluginsViewModel(provider, message => { prompt = message; return confirm.Task; });
        await vm.LoadAsync();
        vm.SelectedPlugin = vm.Plugins.Single(item => item.Id == "first@ux");
        Task installing = vm.InstallItemCommand.ExecuteAsync(vm.Plugins[1]);
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanChange);
        vm.SelectedPlugin = vm.Plugins[0];
        await vm.InstallItemCommand.ExecuteAsync(vm.Plugins[0]);
        confirm.SetResult(true);
        await installing.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains("target@ux", prompt);
        Assert.Equal(("target@ux", AgentPluginAction.Install), Assert.Single(provider.Actions));
        Assert.False(vm.Plugins.Single(item => item.Id == "first@ux").IsInstalled);
        Assert.True(vm.Plugins.Single(item => item.Id == "target@ux").IsInstalled);
        Assert.True(vm.ShowRestartNotice);
        Assert.False(vm.NeedsRefresh);
        Assert.Equal(2, provider.CatalogReads);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task PluginMutation_RefreshesInstalledStateWithoutReloadingMarketplaceCatalog() =>
        _session.Dispatch<bool>(async () =>
        {
            var provider = new ProviderStub("Copilot", true, true)
            {
                Catalog = new(
                    [new("managed@selected", "Installed target", "1", false, false),
                     new("unrelated@other", "Unrelated marketplace item", "1", false, false)],
                    [new("selected", "selected source"), new("other", "unrelated source")]),
                SupportsScopedBrowse = true,
                InstalledCatalog = new(
                    [],
                    [new("selected", "selected source"), new("other", "unrelated source")]),
                MarketplaceCatalogs = new()
                {
                    ["selected"] = new([new("managed@selected", "Installed target", "1", false, false)], []),
                    ["other"] = new([new("unrelated@other", "Unrelated marketplace item", "1", false, false)], []),
                },
                ApplyMutations = true,
            };
            var vm = new AgentPluginsViewModel(provider, _ => Task.FromResult(true));
            await vm.LoadAsync();
            vm.View = PluginLibraryView.Browse;
            foreach (var marketplace in vm.Marketplaces.ToArray())
            {
                vm.SelectedMarketplace = marketplace;
                await vm.BrowseSelectedMarketplaceCommand.ExecuteAsync(null);
            }
            vm.SelectedMarketplace = null;
            vm.SelectedPlugin = vm.Plugins.Single(item => item.Id == "managed@selected");

            await vm.InstallCommand.ExecuteAsync(null);

            Assert.True(vm.Plugins.Single(item => item.Id == "managed@selected").IsInstalled);
            Assert.False(vm.Plugins.Single(item => item.Id == "unrelated@other").IsInstalled);
            Assert.Equal(2, provider.InstalledCatalogReads);
            Assert.Equal(2, provider.MarketplaceCatalogReads);
            Assert.Equal(("managed@selected", AgentPluginAction.Install), Assert.Single(provider.Actions));
            vm.Stop();
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task UnsupportedAndReadOnlyPluginActionsNeverReachProviderMutations() => _session.Dispatch<bool>(async () =>
    {
        var provider = new ProviderStub("Codex", false, false)
        {
            Catalog = new(
                [new("installed@builtin", "", "1", true, false), new("blocked@builtin", "", "1", false, false) { CanInstall = false }],
                [new("builtin", "agent owned") { CanManage = false }]),
        };
        var vm = new AgentPluginsViewModel(provider, _ => throw new Xunit.Sdk.XunitException("Unsupported actions cannot prompt"));
        await vm.LoadAsync();
        vm.SelectedPlugin = vm.Plugins.Single(item => item.Id == "installed@builtin");
        await vm.UpdateCommand.ExecuteAsync(null);
        await vm.EnableCommand.ExecuteAsync(null);
        await vm.DisableCommand.ExecuteAsync(null);
        vm.SelectedPlugin = vm.Plugins.Single(item => item.Id == "installed@builtin");
        await vm.InstallItemCommand.ExecuteAsync(vm.Plugins[1]);
        vm.SelectedMarketplace = vm.Marketplaces[0];
        await vm.RefreshMarketplaceCommand.ExecuteAsync(null);
        Assert.True(vm.HasError);
        Assert.Contains("managed by the agent", vm.Status);
        await vm.RefreshCommand.ExecuteAsync(null);
        await vm.RemoveMarketplaceCommand.ExecuteAsync(null);
        Assert.True(vm.HasError);
        Assert.Empty(provider.Actions);
        Assert.Equal(0, provider.MarketplaceChanges);
        Assert.Equal("builtin", vm.SelectedMarketplace?.Name);
        return true;
    }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task PluginMutationFailureOrCancellationKeepsStaleInventoryAndRequiresReload(bool cancel) => _session.Dispatch<bool>(async () =>
    {
        var provider = new ProviderStub("Cursor", true, false)
        {
            Catalog = new([new("tool@ux", "", "1", false, false)], []),
        };
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.PerformResult = (_, _, _) => cancel ? release.Task : Task.FromException<string>(new InvalidOperationException("fixture import failed"));
        var vm = new AgentPluginsViewModel(provider, _ => Task.FromResult(true));
        await vm.LoadAsync();
        vm.SelectedPlugin = vm.Plugins[0];
        Task changing = vm.InstallCommand.ExecuteAsync(null);
        if (cancel)
        {
            vm.CancelCommand.Execute(null);
            release.SetResult("noncooperative late success");
        }
        await changing.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(vm.NeedsRefresh);
        Assert.False(vm.CanChange);
        Assert.False(vm.ShowRestartNotice);
        Assert.DoesNotContain("completed", vm.Status, StringComparison.OrdinalIgnoreCase);
        Assert.False(Assert.Single(vm.Plugins).IsInstalled);
        Assert.Contains(cancel ? "Cancelled" : "fixture import failed", vm.Status);
        Assert.Equal(!cancel, vm.HasError);
        await vm.InstallItemCommand.ExecuteAsync(vm.Plugins[0]);
        Assert.Single(provider.Actions);
        provider.Catalog = new([new("tool@ux", "", "1", true, false) { StatusText = "Imported locally; reload Cursor to activate" }], []);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.False(vm.NeedsRefresh);
        Assert.False(vm.HasError);
        Assert.Equal("Imported locally; reload Cursor to activate", Assert.Single(vm.Plugins).Status);
        Assert.DoesNotContain("Enabled", vm.Plugins[0].Status);
        Assert.True(vm.CanManage);
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task BulkUpdateSnapshotsOnlyFilteredInstalledManageablePluginsAndRunsSerially() =>
        _session.Dispatch<bool>(async () =>
        {
            var provider = BulkProvider();
            var otherProvider = new ProviderStub("Other", true, true);
            var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            List<string> prompts = [];
            var vm = new AgentPluginsViewModel([provider, otherProvider], message =>
            {
                prompts.Add(message);
                return confirmation.Task;
            });
            await vm.LoadAsync();
            vm.SelectedMarketplace = vm.Marketplaces.Single(item => item.Name == "team");
            vm.Query = "matching";
            provider.PerformResult = (id, _, _) =>
            {
                if (id != "first@team") return Task.FromResult("updated second");
                started.TrySetResult();
                return release.Task;
            };
            Task updating = vm.UpdateAllCommand.ExecuteAsync(null);
            Assert.True(vm.IsBusy);
            Assert.False(vm.CanUpdateAll);
            vm.SelectedProvider = otherProvider;
            Assert.Same(provider, vm.SelectedProvider);
            await vm.UpdateAllCommand.ExecuteAsync(null);
            Assert.Single(prompts);
            Assert.Contains("2 installed plugins", prompts[0]);
            Assert.Contains("does not mean newer versions were detected", prompts[0]);
            Assert.Contains("first@team", prompts[0]);
            Assert.Contains("second@team", prompts[0]);
            Assert.DoesNotContain("readonly", prompts[0]);
            Assert.DoesNotContain("available", prompts[0]);
            Assert.DoesNotContain("other", prompts[0]);
            vm.Query = "excluded";
            confirmation.SetResult(true);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(("first@team", AgentPluginAction.Update), Assert.Single(provider.Actions));
            Assert.Equal(1, provider.CatalogReads);
            Assert.Equal(0, otherProvider.CatalogReads);
            release.SetResult("updated first");
            await updating.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal([("first@team", AgentPluginAction.Update), ("second@team", AgentPluginAction.Update)], provider.Actions);
            Assert.Equal(2, provider.CatalogReads);
            Assert.Contains("Updated 2 plugins", vm.Status);
            Assert.Contains("first@team: updated first", vm.Activity);
            Assert.Contains("second@team: updated second", vm.Activity);
            Assert.True(vm.ShowRestartNotice);
            Assert.False(vm.NeedsRefresh);
            Assert.False(vm.HasError);
            Assert.Equal("excluded", vm.Query);
            vm.Stop();
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task BulkUpdatePartialFailureReportsSuccessfulCountAndRereadsAuthoritativeInventory() =>
        _session.Dispatch<bool>(async () =>
        {
            var provider = BulkProvider();
            provider.PerformResult = (id, _, _) =>
            {
                if (id == "first@team") throw new InvalidOperationException("fixture rejected first");
                provider.Catalog = provider.Catalog with
                {
                    Plugins = provider.Catalog.Plugins.Select(item => item.Id == id ? item with { Version = "2" } : item).ToArray(),
                };
                return Task.FromResult("success second");
            };
            var vm = new AgentPluginsViewModel(provider, _ => Task.FromResult(true));
            await vm.LoadAsync();
            vm.SelectedMarketplace = vm.Marketplaces.Single(item => item.Name == "team");
            vm.Query = "matching";
            vm.SelectedPlugin = vm.Plugins.Single(item => item.Id == "second@team");
            await vm.UpdateAllCommand.ExecuteAsync(null);
            Assert.Equal([("first@team", AgentPluginAction.Update), ("second@team", AgentPluginAction.Update)], provider.Actions);
            Assert.Equal(2, provider.CatalogReads);
            Assert.True(vm.HasError);
            Assert.False(vm.NeedsRefresh); // Reread settled; the error still prevents another mutation.
            Assert.False(vm.CanUpdateAll);
            Assert.Contains("1 plugins updated; 1 failed", vm.Status);
            Assert.Contains("first@team: fixture rejected first", vm.Status);
            Assert.Equal("2", vm.Plugins.Single(item => item.Id == "second@team").Version);
            Assert.Equal("1", vm.Plugins.Single(item => item.Id == "first@team").Version);
            Assert.Equal("second@team", vm.SelectedPlugin?.Id);
            Assert.True(vm.ShowRestartNotice);
            vm.Stop();
            return true;
        }, CancellationToken.None);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task BulkUpdateDeclineOrCancelConfirmationDoesNotMutate(bool cancel) => _session.Dispatch<bool>(async () =>
    {
        var provider = BulkProvider();
        var choice = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new AgentPluginsViewModel(provider, _ => choice.Task);
        await vm.LoadAsync();
        Task updating = vm.UpdateAllCommand.ExecuteAsync(null);
        if (cancel) vm.CancelCommand.Execute(null);
        choice.SetResult(cancel);
        await updating;
        Assert.Empty(provider.Actions);
        Assert.Equal(1, provider.CatalogReads);
        Assert.Contains("Cancelled", vm.Status);
        Assert.False(vm.ShowRestartNotice);
        vm.Stop();
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task BulkUpdateCancellationKeepsUnprocessedRowsAndRequiresReload() => _session.Dispatch<bool>(async () =>
    {
        var provider = BulkProvider();
        var release = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.PerformResult = (_, _, _) => release.Task;
        var vm = new AgentPluginsViewModel(provider, _ => Task.FromResult(true));
        await vm.LoadAsync();
        vm.SelectedMarketplace = vm.Marketplaces.Single(item => item.Name == "team");
        vm.Query = "matching";
        Task updating = vm.UpdateAllCommand.ExecuteAsync(null);
        Assert.Equal(("first@team", AgentPluginAction.Update), Assert.Single(provider.Actions));
        vm.CancelCommand.Execute(null);
        release.SetResult("late uncooperative success");
        await updating;
        Assert.Single(provider.Actions);
        Assert.Equal(1, provider.CatalogReads);
        Assert.True(vm.NeedsRefresh);
        Assert.False(vm.CanUpdateAll);
        Assert.Contains("Cancelled. Reload", vm.Status);
        Assert.DoesNotContain("Updated", vm.Status);
        Assert.False(vm.ShowRestartNotice);
        Assert.Equal("1", vm.Plugins.Single(item => item.Id == "first@team").Version);
        Assert.Equal("1", vm.Plugins.Single(item => item.Id == "second@team").Version);
        await vm.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(2, provider.CatalogReads);
        Assert.False(vm.NeedsRefresh);
        Assert.True(vm.CanUpdateAll);
        vm.Stop();
        return true;
    }, CancellationToken.None);

    [Fact]
    public Task AppProvidedPluginsCanBeToggledButNotRemovedOrUpdated() =>
        _session.Dispatch<bool>(async () =>
        {
            var host = new ProviderStub("Codex", supportsUpdate: true, supportsToggle: true)
            {
                Catalog = new([
                    new("browser@openai-bundled", "Bundled", "1", true, true) { CanManage = false, CanToggle = true, Notice = "Provided by the app" },
                    new("tool@market", "Tool", "1", true, true),
                ], []),
            };
            List<string> prompts = [];
            var vm = new AgentPluginsViewModel([host], message =>
            {
                prompts.Add(message);
                return Task.FromResult(true);
            });
            await vm.LoadAsync();

            vm.SelectedPlugin = vm.Plugins.Single(item => item.Id == "browser@openai-bundled");
            Assert.True(vm.CanDisable);
            await vm.UninstallCommand.ExecuteAsync(null);
            await vm.UpdateCommand.ExecuteAsync(null);
            await vm.DisableCommand.ExecuteAsync(null);
            Assert.Equal([("browser@openai-bundled", AgentPluginAction.Disable)], host.Actions);
            Assert.Single(prompts);

            await vm.UpdateAllCommand.ExecuteAsync(null);
            Assert.Equal(("tool@market", AgentPluginAction.Update), host.Actions[^1]);
            Assert.DoesNotContain(("browser@openai-bundled", AgentPluginAction.Update), host.Actions);
            vm.Stop();
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task UnsupportedBulkAndReadOnlyLifecycleActionsAreBlockedWhileBuiltinMarketplaceCanRefresh() =>
        _session.Dispatch<bool>(async () =>
        {
            var codex = new ProviderStub("Codex", false, false) { Catalog = BulkProvider().Catalog };
            var copilot = new ProviderStub("GitHub Copilot CLI", true, true)
            {
                Catalog = new([new("builtin", "Built-in", "1", true, true) { CanManage = false }],
                    [new("awesome-copilot", "github/awesome-copilot") { CanManage = false, CanRefresh = true }]),
            };
            var vm = new AgentPluginsViewModel([codex, copilot], _ => throw new Xunit.Sdk.XunitException("Read-only actions must not prompt"));
            await vm.LoadAsync();
            Assert.False(vm.CanUpdateAll);
            await vm.UpdateAllCommand.ExecuteAsync(null);
            Assert.Empty(codex.Actions);
            vm.SelectedProvider = copilot;
            await vm.LoadAsync();
            vm.SelectedPlugin = vm.Plugins[0];
            foreach (var command in new[] { vm.UpdateCommand, vm.EnableCommand, vm.DisableCommand, vm.UninstallCommand })
                await command.ExecuteAsync(null);
            Assert.Empty(copilot.Actions);
            vm.SelectedMarketplace = vm.Marketplaces[0];
            Assert.True(vm.CanRefreshMarketplace);
            Assert.False(vm.CanRemoveMarketplace);
            await vm.RefreshMarketplaceCommand.ExecuteAsync(null);
            Assert.Equal(1, copilot.MarketplaceChanges);
            Assert.Equal(2, copilot.CatalogReads);
            await vm.RemoveMarketplaceCommand.ExecuteAsync(null);
            Assert.Equal(1, copilot.MarketplaceChanges);
            Assert.True(vm.HasError);
            vm.Stop();
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task UpdateAllTracksFiltersAndRetainsSelectionAfterReread() => _session.Dispatch<bool>(async () =>
    {
        var provider = BulkProvider();
        var vm = new AgentPluginsViewModel(provider, _ => Task.FromResult(true));
        await vm.LoadAsync();
        vm.SelectedMarketplace = vm.Marketplaces.Single(item => item.Name == "team");
        vm.Query = "matching";
        vm.SelectedPlugin = vm.Plugins.Single(item => item.Id == "second@team");
        Assert.True(vm.CanUpdateAll);
        await vm.UpdateAllCommand.ExecuteAsync(null);
        Assert.Equal(2, provider.Actions.Count);
        Assert.Equal("second@team", vm.SelectedPlugin?.Id);
        vm.Query = "available";
        Assert.False(vm.CanUpdateAll);
        Assert.DoesNotContain(provider.Actions, action => action.Action != AgentPluginAction.Update);
        vm.Stop();
        return true;
    }, CancellationToken.None);

    private static ProviderStub BulkProvider() => new("GitHub Copilot CLI", true, true)
    {
        Catalog = new(
            [new("first@team", "Matching first", "1", true, true),
             new("second@team", "Matching second", "1", true, false),
             new("readonly@team", "Matching readonly", "1", true, true) { CanManage = false },
             new("available@team", "Matching available", "1", false, false),
             new("excluded@team", "Not searched", "1", true, true),
             new("other@elsewhere", "Matching other", "1", true, true)],
            [new("team", "owner/repo"), new("elsewhere", "other/repo")]),
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task BulkUpdateAllFailuresOrFinalInventoryFailureCannotReportCompletion(bool inventoryFails) =>
        _session.Dispatch<bool>(async () =>
        {
            var provider = BulkProvider();
            var vm = new AgentPluginsViewModel(provider, _ => Task.FromResult(true));
            await vm.LoadAsync();
            vm.Query = "matching";
            vm.SelectedMarketplace = vm.Marketplaces.Single(item => item.Name == "team");
            if (inventoryFails)
                provider.CatalogRead = _ => Task.FromException<AgentPluginCatalog>(new InvalidOperationException("fixture inventory unavailable"));
            else
                provider.PerformResult = (_, _, _) => Task.FromException<string>(new InvalidOperationException("fixture update unavailable"));
            await vm.UpdateAllCommand.ExecuteAsync(null);
            Assert.Equal(2, provider.Actions.Count);
            Assert.Equal(2, provider.CatalogReads);
            Assert.True(vm.HasError);
            Assert.Equal(inventoryFails, vm.NeedsRefresh);
            Assert.Equal(inventoryFails, vm.ShowRestartNotice);
            Assert.Contains(inventoryFails ? "fixture inventory unavailable" : "0 plugins updated; 2 failed", vm.Status);
            Assert.DoesNotContain("Updated 2", vm.Status);
            Assert.False(vm.CanUpdateAll);
            vm.Stop();
            return true;
        }, CancellationToken.None);

    private sealed class ProviderStub(string name, bool supportsUpdate, bool supportsToggle) : IAgentPluginService
    {
        public string Name { get; } = name;
        public string Guidance => Name + " guidance";
        public string RemovalWarning => Name + " removal warning";
        public string RestartNotice => Name + " restart";
        public bool SupportsUpdate { get; } = supportsUpdate;
        public bool SupportsToggle { get; } = supportsToggle;
        public bool SupportsScopedBrowse { get; init; }
        public AgentPluginCatalog Catalog { get; set; } = new([], []);
        public AgentPluginCatalog InstalledCatalog { get; set; } = new([], []);
        public Dictionary<string, AgentPluginCatalog> MarketplaceCatalogs { get; init; } = [];
        public Func<CancellationToken, Task<AgentPluginCatalog>>? CatalogRead { get; set; }
        public Func<string, CancellationToken, Task<AgentPluginCatalog>>? MarketplaceCatalogRead { get; set; }
        public int CatalogReads { get; private set; }
        public int InstalledCatalogReads { get; private set; }
        public int MarketplaceCatalogReads { get; private set; }
        public List<(string Id, AgentPluginAction Action)> Actions { get; } = [];
        public List<string> AddedSources { get; } = [];
        public bool ApplyMutations { get; init; }
        public int MarketplaceChanges { get; private set; }
        public Func<string, AgentPluginAction, CancellationToken, Task<string>>? PerformResult { get; set; }

        public Task<AgentPluginCatalog> GetCatalogAsync(CancellationToken token = default)
        {
            CatalogReads++;
            return CatalogRead?.Invoke(token) ?? Task.FromResult(Catalog);
        }

        public Task<AgentPluginCatalog> GetInstalledCatalogAsync(CancellationToken token = default)
        {
            if (!SupportsScopedBrowse) return GetCatalogAsync(token);
            InstalledCatalogReads++;
            return Task.FromResult(InstalledCatalog);
        }

        public Task<AgentPluginCatalog> GetMarketplaceCatalogAsync(string marketplace, CancellationToken token = default)
        {
            MarketplaceCatalogReads++;
            if (MarketplaceCatalogRead is not null) return MarketplaceCatalogRead(marketplace, token);
            return Task.FromResult(MarketplaceCatalogs.TryGetValue(marketplace, out var catalog)
                ? catalog
                : new AgentPluginCatalog(Catalog.Plugins.Where(item => !item.IsInstalled && item.Marketplace == marketplace).ToArray(), []));
        }

        public Task<string> AddMarketplaceAsync(string input, CancellationToken token = default)
        {
            AddedSources.Add(input);
            return Task.FromResult(Name + " source added.");
        }

        public Task<string> UpdateMarketplaceAsync(string name, CancellationToken token = default)
        {
            MarketplaceChanges++;
            return Task.FromResult(Name + " source refreshed.");
        }

        public Task<string> RemoveMarketplaceAsync(string name, CancellationToken token = default)
        {
            MarketplaceChanges++;
            return Task.FromResult(Name + " source removed.");
        }

        public Task<string> PerformAsync(string input, AgentPluginAction action, CancellationToken token = default)
        {
            Actions.Add((input, action));
            if (PerformResult is not null) return PerformResult(input, action, token);
            if (ApplyMutations)
            {
                Catalog = Catalog with
                {
                    Plugins = Catalog.Plugins.Select(item => item.Id != input ? item : item with
                    {
                        IsInstalled = action != AgentPluginAction.Uninstall,
                        Enabled = action != AgentPluginAction.Uninstall,
                    }).ToArray(),
                };
                if (SupportsScopedBrowse)
                {
                    InstalledCatalog = InstalledCatalog with
                    {
                        Plugins = InstalledCatalog.Plugins.Select(item => item.Id != input ? item : item with
                        {
                            IsInstalled = action != AgentPluginAction.Uninstall,
                            Enabled = action != AgentPluginAction.Uninstall,
                        }).ToArray(),
                    };
                    if (SupportsScopedBrowse)
                    {
                        AgentPluginItem? updated = Catalog.Plugins.FirstOrDefault(item => item.Id == input);
                        InstalledCatalog = InstalledCatalog with
                        {
                            Plugins = action == AgentPluginAction.Uninstall
                                ? InstalledCatalog.Plugins.Where(item => item.Id != input).ToArray()
                                : updated is null
                                    ? InstalledCatalog.Plugins
                                    : InstalledCatalog.Plugins.Where(item => item.Id != input).Append(updated).ToArray(),
                        };
                    }
                }
            }
            return Task.FromResult(Name + " result");
        }
    }
}
