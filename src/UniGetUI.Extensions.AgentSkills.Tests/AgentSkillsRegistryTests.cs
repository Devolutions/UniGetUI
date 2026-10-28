using System.Text.Json;
using Devolutions.AgentSkills;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Extensions.AgentSkills;
using UniGetUI.Extensions.AgentSkills.Tests.Infrastructure.Fakes;

namespace UniGetUI.Extensions.AgentSkills.Tests;

public sealed partial class AgentSkillsServiceTests
{
    private static SkillsShResult RegistryResult(string name = "review", string owner = "contoso", string repo = "skills") =>
        new(name, SkillSourceLocator.GitHub(owner, repo));
    private sealed class RecordingRegistry : ISkillsShClient
    {
        public List<string> Queries { get; } = [];
        public IReadOnlyList<SkillsShResult> Results { get; set; } = [RegistryResult()];
        public Func<IReadOnlyList<SkillsShResult>>? SearchResult { get; set; }
        public IReadOnlyList<SkillsShResult> Search(string query)
        {
            Queries.Add(query);
            return SearchResult is null ? Results : SearchResult();
        }
    }

    [Fact]
    public void RegistryDiscoveryIsOffByDefaultAndQueriesMustHaveTwoCharacters()
    {
        var registry = new RecordingRegistry();
        var service = CreateService(registry);
        Assert.False(service.SkillsShDiscoveryEnabled);
        Assert.Empty(service.Search("review"));
        Assert.Empty(registry.Queries);
        service.SkillsShDiscoveryEnabled = true;
        foreach (string query in new[] { "", " \t ", "a", " a " }) Assert.Empty(service.Search(query));
        Assert.Empty(registry.Queries);
        Assert.Equal("review", Assert.Single(service.Search("  UI widgets  ")).Id);
        Assert.Equal(["  UI widgets  "], registry.Queries);
        Assert.Empty(AgentSkillsService.GetConfiguredSources());
        Assert.Empty(_backend.ListedSources);
    }
    [Fact]
    public void RegistryUnionDeduplicatesOnlyExactSourceAndSelector()
    {
        AddSourceSetting("contoso/skills");
        _backend.SourceSkills["https://github.com/contoso/skills"] =
            [new("review", "Review", null), new("local-review", "Review", null), new("different", "Other", null)];
        var registry = new RecordingRegistry
        {
            Results = [RegistryResult(), RegistryResult(), RegistryResult(owner: "fabrikam"), RegistryResult("fuzzy")],
        };
        var service = CreateService(registry);
        service.SkillsShDiscoveryEnabled = true;
        Assert.Equal(["contoso/skills:review", "contoso/skills:local-review", "fabrikam/skills:review", "contoso/skills:fuzzy"],
            service.Search("review").Select(skill => $"{skill.SourceName}:{skill.Id}"));
        Assert.Single(AgentSkillsService.GetConfiguredSources());
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void RegistryFailuresPreserveLocalResultsWithoutConfiguringAnything(int failure)
    {
        AddSourceSetting("https://skills.contoso.com");
        _backend.SourceSkills["https://skills.contoso.com"] = [new("local-review", "Review", null)];
        var registry = new RecordingRegistry
        {
            SearchResult = () => throw (failure switch
            {
                0 => new HttpRequestException("offline"),
                1 => new JsonException("malformed"),
                _ => new OperationCanceledException(),
            })
        };
        var service = CreateService(registry);
        service.SkillsShDiscoveryEnabled = true;
        Assert.Equal("local-review", Assert.Single(service.Search("review")).Id);
        Assert.Equal("skills.contoso.com", Assert.Single(AgentSkillsService.GetConfiguredSources()).Name);
        Assert.Null(service.GetInstallSource(Skill("review")));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangingDiscoveryPreferenceRevokesPreviouslyAdmittedSkill(bool enabled)
    {
        var service = CreateService(new RecordingRegistry());
        service.SkillsShDiscoveryEnabled = true;
        var found = Assert.Single(service.Search("review"));
        Assert.NotNull(service.GetInstallSource(found));
        service.SkillsShDiscoveryEnabled = enabled;
        Assert.Null(service.GetInstallSource(found));
        await Assert.ThrowsAsync<SkillsException>(() => service.PerformAsync(found, SkillAction.Install));
        Assert.Empty(_backend.Installs);
        Assert.Empty(AgentSkillsService.GetConfiguredSources());
    }
    public enum RegistryInvalidation { Disable, DisableAndReenable, SetEnabledAgain, RemoveSource }
    [Theory]
    [InlineData(RegistryInvalidation.Disable)]
    [InlineData(RegistryInvalidation.DisableAndReenable)]
    [InlineData(RegistryInvalidation.SetEnabledAgain)]
    [InlineData(RegistryInvalidation.RemoveSource)]
    public async Task InFlightRegistryResultsCannotCrossGenerationBoundary(RegistryInvalidation change)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var registry = new RecordingRegistry
        {
            SearchResult = () =>
        {
            entered.TrySetResult();
            release.Task.GetAwaiter().GetResult();
            return [RegistryResult()];
        }
        };
        var service = CreateService(registry);
        service.SkillsShDiscoveryEnabled = true;
        var pending = Task.Run(() => service.Search("review"));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var settingsService = CreateService();
            switch (change)
            {
                case RegistryInvalidation.Disable: settingsService.SkillsShDiscoveryEnabled = false; break;
                case RegistryInvalidation.DisableAndReenable:
                    settingsService.SkillsShDiscoveryEnabled = false;
                    settingsService.SkillsShDiscoveryEnabled = true;
                    break;
                case RegistryInvalidation.SetEnabledAgain: settingsService.SkillsShDiscoveryEnabled = true; break;
                case RegistryInvalidation.RemoveSource: settingsService.RemoveSource(SkillSourceLocator.GitHub("contoso", "skills")); break;
            }
        }
        finally { release.TrySetResult(); }
        Assert.Empty(await pending.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Null(service.GetInstallSource(Skill("review")));
        await Assert.ThrowsAsync<SkillsException>(() => service.PerformAsync(Skill("review"), SkillAction.Install));
        Assert.Empty(_backend.Installs);
    }
    [Fact]
    public async Task SettingsServiceRevokesOtherServicesCachedAdmissionsWithoutRevivingThemOnNextSearch()
    {
        var registry = new RecordingRegistry();
        var pageService = CreateService(registry);
        pageService.SkillsShDiscoveryEnabled = true;
        var old = Assert.Single(pageService.Search("review"));
        var settingsService = CreateService();
        settingsService.SkillsShDiscoveryEnabled = false;
        settingsService.SkillsShDiscoveryEnabled = true;
        Assert.Null(pageService.GetInstallSource(old));
        registry.Results = [RegistryResult("deploy")];
        var current = Assert.Single(pageService.Search("deploy"));
        Assert.NotNull(pageService.GetInstallSource(current));
        Assert.Null(pageService.GetInstallSource(old));
        await Assert.ThrowsAsync<SkillsException>(() => pageService.PerformAsync(old, SkillAction.Install));
        Assert.Empty(_backend.Installs);
    }
    [Fact]
    public async Task DiscoveryAuthorizesOnlyExactSkillAndSourceAndPersistsOnlyAfterSuccess()
    {
        var service = CreateService(new RecordingRegistry());
        service.SkillsShDiscoveryEnabled = true;
        var skill = Assert.Single(service.Search("review"));
        foreach (var stranger in new[] { Skill("sibling"), Skill("review", "fabrikam/skills"), Skill("review", "contoso/other") })
            await Assert.ThrowsAsync<SkillsException>(() => service.PerformAsync(stranger, SkillAction.Install));
        Assert.Empty(_backend.Installs);
        Assert.Empty(AgentSkillsService.GetConfiguredSources());
        await service.PerformAsync(skill, SkillAction.Install);
        Assert.Equal("review", Assert.Single(_backend.Installs).Skill);
        Assert.Equal("https://github.com/contoso/skills", Assert.Single(AgentSkillsService.GetConfiguredSources()).InstallSource);
    }
    [Theory]
    [InlineData(SkillOperationStatus.Failed, false)]
    [InlineData(SkillOperationStatus.NotFound, false)]
    [InlineData(SkillOperationStatus.Succeeded, true)]
    public async Task FailedOrMismatchedInstallDoesNotSaveDiscoverySource(SkillOperationStatus status, bool mismatch)
    {
        _backend.InstallStatus = status;
        _backend.InstallOutcomeName = mismatch ? "sibling" : null;
        var service = CreateService(new RecordingRegistry());
        service.SkillsShDiscoveryEnabled = true;
        var skill = Assert.Single(service.Search("review"));
        await Assert.ThrowsAsync<SkillsException>(() => service.PerformAsync(skill, SkillAction.Install));
        Assert.Empty(AgentSkillsService.GetConfiguredSources());
        Assert.Equal("review", Assert.Single(_backend.Installs).Skill);
    }
    [Fact]
    public async Task SuccessfulDiscoveryInstallRemainsUpdatableAndRemovableWithDiscoveryOff()
    {
        var registry = new RecordingRegistry { Results = [RegistryResult("deploy")] };
        var service = CreateService(registry);
        service.SkillsShDiscoveryEnabled = true;
        var skill = Assert.Single(service.Search("deploy"));
        await service.PerformAsync(skill, SkillAction.Install);
        Assert.Null(Assert.Single(_backend.Installs).Agents);
        SeedUpdate();
        service.SkillsShDiscoveryEnabled = false;
        var update = Assert.Single(service.CheckUpdates());
        await service.PerformAsync(update, SkillAction.Update);
        await service.PerformAsync(skill, SkillAction.Remove);
        Assert.Single(_backend.AppliedUpdates);
        Assert.Equal(["deploy"], _backend.Removed);
        Assert.Equal(["deploy"], registry.Queries);
    }
    [Fact]
    public async Task ConfiguredFolderIsAuthoritativeOverRegistryRepositoryRoot()
    {
        const string folder = "https://github.com/contoso/skills/tree/main/curated";
        AddSourceSetting(folder);
        _backend.SourceSkills[folder] = [new("review", "Curated", null)];
        var service = CreateService(new RecordingRegistry());
        service.SkillsShDiscoveryEnabled = true;
        var skill = Assert.Single(service.Search("review"));
        Assert.Equal(folder, skill.Source!.InstallSource);
        await service.PerformAsync(skill, SkillAction.Install);
        Assert.Equal(folder, Assert.Single(_backend.Installs).Source);
        Assert.Equal([folder], Settings.GetList<string>(AgentSkillsService.SourcesListKey));
    }
    [Fact]
    public async Task WebsiteUsesActualSelectorNotWebsiteSlugAndRetainsIndexProvenance()
    {
        const string website = "https://uizze.sh";
        var registry = new RecordingRegistry { Results = [new("react:components", SkillSourceLocator.Parse(website)!)] };
        var service = CreateService(registry);
        service.SkillsShDiscoveryEnabled = true;
        AgentSkillsService.TargetAgents = ["codex"];
        var skill = Assert.Single(service.Search("UI"));
        await Assert.ThrowsAsync<SkillsException>(() => service.PerformAsync(skill with { Id = "reactcomponents" }, SkillAction.Install));
        await service.PerformAsync(skill, SkillAction.Install);
        var install = Assert.Single(_backend.Installs);
        Assert.Equal("react:components", install.Skill);
        Assert.Equal(website, install.Source);
        Assert.Equal(["codex"], install.Agents);
        service.SkillsShDiscoveryEnabled = false;
        Assert.Equal(website, service.GetInstallSource(skill)!.InstallSource);
        _backend.Installed.Add(WellKnownSkill("react:components", website));
        _backend.UpdateCheck = new([new() { Name = "react:components", Scope = SkillScope.Global,
            Source = website, CurrentHash = "1111111", LatestHash = "2222222" }], []);
        await service.PerformAsync(Assert.Single(service.CheckUpdates()), SkillAction.Update);
        Assert.Equal(website, Assert.Single(Assert.Single(_backend.AppliedUpdates)).Source);
    }
    [Fact]
    public async Task RemovingRetainedSourceRevokesOriginalRegistryAdmission()
    {
        var service = CreateService(new RecordingRegistry());
        service.SkillsShDiscoveryEnabled = true;
        var skill = Assert.Single(service.Search("review"));
        await service.PerformAsync(skill, SkillAction.Install);
        service.RemoveSource(skill.Source!);
        Assert.Null(service.GetInstallSource(skill));
        await Assert.ThrowsAsync<SkillsException>(() => service.PerformAsync(skill, SkillAction.Install));
        Assert.Single(_backend.Installs);
        Assert.Empty(AgentSkillsService.GetConfiguredSources());
    }
    [Fact]
    public void FailedPreferenceSaveRollsBackCacheAndAllowsRetry()
    {
        var registry = new RecordingRegistry();
        var service = CreateService(registry);
        string path = Path.Combine(CoreData.UniGetUIUserConfigurationDirectory, Settings.ResolveKey(Settings.K.SkillsShDiscoveryEnabled));
        Directory.CreateDirectory(path);
        Assert.Throws<IOException>(() => service.SkillsShDiscoveryEnabled = true);
        Assert.False(service.SkillsShDiscoveryEnabled);
        Assert.Empty(service.Search("review"));
        Assert.Empty(registry.Queries);
        Directory.Delete(path);
        service.SkillsShDiscoveryEnabled = true;
        Assert.True(File.Exists(path));
        Assert.Equal("review", Assert.Single(service.Search("review")).Id);
    }
}
