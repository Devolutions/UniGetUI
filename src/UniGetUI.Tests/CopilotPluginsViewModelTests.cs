using Avalonia.Headless;
using UniGetUI.Avalonia.ViewModels.Pages;
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
    public Task DefaultProvidersExposeCopilotAsSingleProvider() =>
        _session.Dispatch<bool>(async () =>
        {
            using var fixture = new AgentSkillsSettingsTestFixture();
            var vm = new AgentPluginsViewModel();
            var copilot = Assert.Single(vm.Providers, provider =>
                provider.Name.StartsWith("GitHub Copilot", StringComparison.Ordinal));
            Assert.Equal("GitHub Copilot", copilot.Name);
            Assert.IsType<CopilotPluginService>(copilot);
            Assert.DoesNotContain(vm.Providers, provider => provider is CopilotAppPluginService);
            vm.Stop();
            return true;
        }, CancellationToken.None);

    [Fact]
    public Task AppHandoffOpensConfirmationFormsWithoutClaimingInstallation() =>
        _session.Dispatch<bool>(async () =>
        {
            List<Uri> opened = [];
            List<string> prompts = [];
            var service = new CopilotAppPluginService(opened.Add);
            var vm = new AgentPluginsViewModel(service, message =>
            {
                prompts.Add(message);
                return Task.FromResult(true);
            });
            await vm.LoadAsync();
            Assert.False(vm.HasError);
            Assert.Empty(opened);
            vm.MarketplaceInput = "owner/repo";
            await vm.AddMarketplaceCommand.ExecuteAsync(null);
            Assert.EndsWith("ghapp%3A%2F%2Fplugins%2Fmarketplace%2Fadd%3Fsource%3Downer%252Frepo", Assert.Single(opened).AbsoluteUri);
            Assert.Contains("owner/repo", Assert.Single(prompts));
            Assert.Equal("Opened the Copilot app confirmation form. Nothing is installed or added until you confirm in Copilot.", vm.Status);
            Assert.False(vm.NeedsRefresh);
            Assert.False(vm.ShowRestartNotice);
            vm.PluginInput = "/plugin install review@team";
            await vm.InstallByIdCommand.ExecuteAsync(null);
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
