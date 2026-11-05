using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.VisualTree;
using UniGetUI.Avalonia.ViewModels.Pages;
using UniGetUI.Avalonia.Views.Pages;
using UniGetUI.Extensions.AgentSkills;

namespace UniGetUI.Tests;

public sealed class CopilotPluginsViewModelTests
{
    private sealed class UnavailableCopilotRunner : IAgentPluginRunner
    {
        public Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken token) =>
            Task.FromException<string>(new InvalidOperationException("Copilot CLI unavailable"));
    }

    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.GetOrStartForAssembly(typeof(CopilotPluginsViewModelTests).Assembly);

    [Fact]
    public Task DefaultProvidersExposeCopilotAsOneSelectableLibrary() =>
        _session.Dispatch<bool>(async () =>
        {
            using var fixture = new AgentSkillsSettingsTestFixture();
            var vm = new AgentPluginsViewModel();
            var copilot = Assert.Single(vm.Providers, provider =>
                provider.Name.StartsWith("GitHub Copilot", StringComparison.Ordinal));
            Assert.Equal("GitHub Copilot", copilot.Name);
            Assert.IsType<CopilotPluginService>(copilot);
            Assert.DoesNotContain(vm.Providers, provider => provider is CopilotAppPluginService);
            vm.SelectedProvider = copilot;
            Assert.Equal(PluginLibraryView.Installed, vm.View);
            vm.Stop();
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task AttachedAppHandoffHidesInventoryAndOpensTwoProgressiveConfirmationForms() =>
        _session.Dispatch<bool>(async () =>
        {
            using var fixture = new AgentSkillsSettingsTestFixture();
            List<Uri> opened = [];
            List<string> prompts = [];
            var service = new CopilotAppPluginService(opened.Add);
            var vm = new AgentPluginsViewModel(service, message =>
            {
                prompts.Add(message);
                return Task.FromResult(true);
            });
            var control = new AgentPluginsControl { DataContext = vm };
            AgentLibraryTestEvidence.AddApplicationResources(control);
            using var host = new AgentSkillsSettingsTestWindow(control, 1000, height: 700);
            await vm.LoadAsync();
            host.Pump();
            Assert.False(vm.HasError); // GetCatalogAsync throws; app loading must never call it.
            Assert.False(control.FindControl<TabStrip>("PluginViews")!.IsEffectivelyVisible);
            Assert.False(control.FindControl<ListBox>("PluginList")!.IsEffectivelyVisible);
            Assert.False(control.FindControl<TextBox>("PluginSearch")!.IsEffectivelyVisible);
            Assert.False(control.FindControl<Button>("UpdateAllPluginsButton")!.IsEffectivelyVisible);
            Assert.DoesNotContain(control.GetVisualDescendants().OfType<TextBlock>(),
                text => text.IsEffectivelyVisible && text.Text == "0 plugins");
            Assert.False(vm.ShowEmpty);
            Assert.False(control.FindControl<TextBox>("MarketplaceInput")!.IsEffectivelyVisible);
            Assert.False(control.FindControl<TextBox>("PluginInput")!.IsEffectivelyVisible);
            Assert.Empty(opened);
            AgentLibraryTestEvidence.Capture(host, "copilot-app-start");

            var add = control.GetVisualDescendants().OfType<Button>().Single(button => button.Command == vm.ShowAddMarketplaceCommand);
            host.Click(add);
            Assert.True(vm.AddingMarketplace);
            Assert.False(vm.AddingPlugin);
            host.EnterText(control.FindControl<TextBox>("MarketplaceInput")!, "owner/repo");
            var openMarket = control.GetVisualDescendants().OfType<Button>().Single(button => button.Command == vm.AddMarketplaceCommand);
            Assert.Equal("Open in Copilot app", openMarket.Content);
            host.Click(openMarket);
            if (vm.AddMarketplaceCommand.ExecutionTask is { } adding) await adding;
            host.Pump();
            Assert.EndsWith("ghapp%3A%2F%2Fplugins%2Fmarketplace%2Fadd%3Fsource%3Downer%252Frepo", Assert.Single(opened).AbsoluteUri);
            Assert.Contains("owner/repo", Assert.Single(prompts));
            Assert.Equal("Opened the Copilot app confirmation form. Nothing is installed or added until you confirm in Copilot.", vm.Status);
            Assert.False(vm.NeedsRefresh);
            Assert.False(vm.ShowRestartNotice);
            AgentLibraryTestEvidence.Capture(host, "copilot-app-marketplace");

            var more = control.FindControl<DropDownButton>("PluginMoreActions")!;
            host.Click(more);
            var menu = Assert.IsType<MenuFlyout>(more.Flyout);
            host.Enter(menu.Items.Cast<MenuItem>().Single(item => item.Command == vm.ShowInstallIdentifierCommand));
            Assert.True(vm.AddingPlugin);
            Assert.False(vm.AddingMarketplace);
            host.EnterText(control.FindControl<TextBox>("PluginInput")!, "/plugin install review@team");
            var openPlugin = control.GetVisualDescendants().OfType<Button>().Single(button => button.Command == vm.InstallByIdCommand);
            Assert.Equal("Open in Copilot app", openPlugin.Content);
            host.Click(openPlugin);
            if (vm.InstallByIdCommand.ExecutionTask is { } installing) await installing;
            host.Pump();
            Assert.Equal(2, opened.Count);
            Assert.EndsWith("ghapp%3A%2F%2Fplugins%2Finstall%3Fsource%3Dreview%2540team", opened[1].AbsoluteUri);
            Assert.Equal(2, prompts.Count);
            Assert.Contains("review@team", prompts[1]);
            Assert.Contains("Nothing is installed or added until you confirm in Copilot", vm.Status);
            Assert.DoesNotContain("operation completed", vm.Status, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("marketplace added", vm.Status, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(vm.Plugins);
            Assert.Empty(vm.Marketplaces);
            Assert.False(vm.HasError);
            Assert.False(vm.ShowRestartNotice);
            AgentLibraryTestEvidence.Capture(host, "copilot-app-install");
            vm.Stop();
            return true;
        }, CancellationToken.None);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public Task AppDeclineOrCancelConfirmationNeverLaunches(bool marketplace, bool cancel) =>
        _session.Dispatch<bool>(async () =>
        {
            List<Uri> opened = [];
            var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var vm = new AgentPluginsViewModel(new CopilotAppPluginService(opened.Add), _ => confirmation.Task)
            {
                MarketplaceInput = "owner/repo",
                PluginInput = "review@team",
            };
            Task pending = (marketplace ? vm.AddMarketplaceCommand : vm.InstallByIdCommand).ExecuteAsync(null);
            Assert.True(vm.IsBusy);
            if (cancel) vm.CancelCommand.Execute(null);
            confirmation.SetResult(cancel);
            await pending;
            Assert.Empty(opened);
            Assert.Contains("Cancelled", vm.Status);
            Assert.False(vm.NeedsRefresh);
            Assert.False(vm.ShowRestartNotice);
            Assert.Empty(vm.Plugins);
            vm.Stop();
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task AppLauncherFailureIsVisibleWithoutClaimingInstallationAndCanRetry() =>
        _session.Dispatch<bool>(async () =>
        {
            int calls = 0;
            var vm = new AgentPluginsViewModel(new CopilotAppPluginService(_ =>
            {
                if (++calls == 1) throw new InvalidOperationException("fixture browser unavailable");
            }), _ => Task.FromResult(true))
            { PluginInput = "review@team" };
            await vm.InstallByIdCommand.ExecuteAsync(null);
            Assert.True(vm.HasError);
            Assert.Contains("fixture browser unavailable", vm.Status);
            Assert.DoesNotContain("completed", vm.Status);
            Assert.False(vm.NeedsRefresh);
            Assert.False(vm.ShowRestartNotice);
            await vm.InstallByIdCommand.ExecuteAsync(null);
            Assert.Equal(2, calls);
            Assert.False(vm.HasError);
            Assert.Contains("confirmation form", vm.Status);
            Assert.Empty(vm.Plugins);
            vm.Stop();
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task AppFallbackRemainsAvailableWhenCliInventoryFails() =>
        _session.Dispatch<bool>(async () =>
        {
            using var fixture = new AgentSkillsSettingsTestFixture();
            List<Uri> opened = [];
            var copilot = new CopilotPluginService(new UnavailableCopilotRunner());
            var app = new CopilotAppPluginService(opened.Add);
            var vm = new AgentPluginsViewModel([copilot], _ => Task.FromResult(true), copilotApp: app)
            {
                MarketplaceInput = "owner/repo",
            };
            await vm.LoadAsync();
            Assert.True(vm.HasError);
            Assert.True(vm.ShowCopilotAppFallback);
            Assert.True(vm.CanOpenCopilotApp);

            await vm.OpenMarketplaceInCopilotAppCommand.ExecuteAsync(null);

            Assert.Single(opened);
            Assert.Contains("owner%252Frepo", opened[0].AbsoluteUri);
            Assert.False(vm.HasError);
            Assert.Contains("confirmation form", vm.Status);
            Assert.Empty(vm.Plugins);
            vm.Stop();
            return true;
        }, CancellationToken.None);
}
