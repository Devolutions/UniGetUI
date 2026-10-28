using Devolutions.AgentSkills;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Extensions.AgentSkills;
using UniGetUI.Extensions.AgentSkills.Tests.Infrastructure.Fakes;

namespace UniGetUI.Extensions.AgentSkills.Tests;

public sealed partial class AgentSkillsServiceTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(AppContext.BaseDirectory, "TestArtifacts", nameof(AgentSkillsServiceTests), Guid.NewGuid().ToString("N"));
    private readonly FakeSkillsBackend _backend = new();
    public AgentSkillsServiceTests()
    {
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        SecureSettings.TEST_SecureSettingsRootOverride = Path.Combine(_testRoot, "Secure");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Settings.ResetSettings();
    }
    public void Dispose()
    {
        Settings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        SecureSettings.TEST_SecureSettingsRootOverride = null;
        if (Directory.Exists(_testRoot)) Directory.Delete(_testRoot, true);
    }
    private AgentSkillsService CreateService(ISkillsShClient? registry = null) => new(_backend, registry);
    private static void AddSourceSetting(string source) => Settings.AddToList(AgentSkillsService.SourcesListKey, source);
    private static SkillItem Skill(string id = "deploy", string? source = "contoso/skills") =>
        new(id, "", SkillSourceLocator.Parse(source ?? ""));

    [Fact]
    public void SearchFiltersConfiguredSourcesAndBrowsingDoesNotRequireQuery()
    {
        AddSourceSetting("https://skills.contoso.com");
        _backend.SourceSkills["https://skills.contoso.com"] =
        [
            new("code-review", "Reviews pull requests", null),
            new("release-notes", "Writes releases", null),
        ];
        var service = CreateService();
        var found = Assert.Single(service.Search("review"));
        Assert.Equal("code-review", found.Id);
        Assert.Equal("skills.contoso.com", found.SourceName);
        Assert.Equal(["code-review", "release-notes"], service.Search("").Select(skill => skill.Id));
        Assert.Single(_backend.ListedSources);
    }
    [Fact]
    public void NoSourcesMeansNoNetworkOrAgentDetection()
    {
        var service = CreateService();
        Assert.Empty(service.Search("anything"));
        Assert.Empty(_backend.ListedSources);
        Assert.Equal(0, _backend.AgentQueries);
        Assert.False(Settings.Get(Settings.K.EnableAgentSkills));
        Assert.False(Settings.Get(Settings.K.SkillsDefaultEnablementApplied));
    }
    [Fact]
    public void InstalledSkillsExposeProvenanceTargetsAndShortHashes()
    {
        _backend.Installed.Add(FakeSkillsBackend.TrackedSkill("deploy", "contoso/skills", "0123456789abcdef"));
        _backend.Installed.Add(new() { Name = "handmade", Description = "", Path = "unused", Scope = SkillScope.Global, Agents = ["codex"] });
        var installed = CreateService().ListInstalled();
        var tracked = Assert.Single(installed, skill => skill.Id == "deploy");
        Assert.Equal("0123456", tracked.Version);
        Assert.Equal("contoso/skills", tracked.SourceName);
        var local = Assert.Single(installed, skill => skill.Id == "handmade");
        Assert.Null(local.Source);
        Assert.Equal("local", local.Version);
        Assert.Contains("codex", local.Provenance);
    }
    [Fact]
    public async Task InstallUsesConfiguredFolderAndTargetAgents()
    {
        const string folder = "https://github.com/contoso/skills/tree/main/curated";
        AddSourceSetting(folder);
        AgentSkillsService.TargetAgents = ["claude-code", "codex", "codex"];
        await CreateService().PerformAsync(Skill(), SkillAction.Install);
        var call = Assert.Single(_backend.Installs);
        Assert.Equal(folder, call.Source);
        Assert.Equal("deploy", call.Skill);
        Assert.Equal(["claude-code", "codex"], call.Agents);
        Assert.Equal([folder], Settings.GetList<string>(AgentSkillsService.SourcesListKey));
    }
    [Fact]
    public async Task UnconfiguredAndLocalInstallsAreRefusedBeforeBackend()
    {
        var service = CreateService();
        foreach (var skill in new[] { Skill(), Skill(source: null) })
            Assert.Contains("Add this source first", (await Assert.ThrowsAsync<SkillsException>(() =>
                service.PerformAsync(skill, SkillAction.Install))).Message);
        Assert.Empty(_backend.Installs);
    }
    [Fact]
    public async Task FailedInstallPreservesBackendErrorAndDoesNotSaveSource()
    {
        AddSourceSetting("contoso/skills");
        _backend.InstallStatus = SkillOperationStatus.Failed;
        var error = await Assert.ThrowsAsync<SkillsException>(() => CreateService().PerformAsync(Skill(), SkillAction.Install));
        Assert.Contains("The install failed", error.Message);
        Assert.Equal(["contoso/skills"], Settings.GetList<string>(AgentSkillsService.SourcesListKey));
    }
    private void SeedUpdate(string source = "contoso/skills")
    {
        _backend.Installed.Add(FakeSkillsBackend.TrackedSkill("deploy", source, "1111111aaaa"));
        _backend.UpdateCheck = new([new()
        {
            Name = "deploy", Scope = SkillScope.Global, Source = source,
            CurrentHash = "1111111aaaa", LatestHash = "2222222bbbb",
        }], []);
    }
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task UpdateRequiresSuccessfulMatchingOutcome(int outcome)
    {
        AddSourceSetting("contoso/skills");
        SeedUpdate();
        _backend.UpdateResult = new(outcome switch
        {
            0 => [],
            1 => [new("sibling", SkillScope.Global, SkillOperationStatus.Succeeded)],
            _ => [new("deploy", SkillScope.Global, SkillOperationStatus.Failed)],
        });
        var service = CreateService();
        var update = Assert.Single(service.CheckUpdates());
        await Assert.ThrowsAsync<SkillsException>(() => service.PerformAsync(update, SkillAction.Update));
        Assert.Equal("deploy", Assert.Single(Assert.Single(_backend.AppliedUpdates)).Name);
    }
    [Fact]
    public async Task UpdatesAreCheckedOnlyForAllowedSourcesAndApplyExactToken()
    {
        AddSourceSetting("contoso/skills");
        SeedUpdate();
        _backend.Installed.Add(FakeSkillsBackend.TrackedSkill("stranger", "someone/else", "abc"));
        var service = CreateService();
        var update = Assert.Single(service.CheckUpdates());
        Assert.Equal(["deploy"], Assert.Single(_backend.CheckedSkills));
        Assert.Equal("1111111", update.Version);
        Assert.Equal("2222222", update.NewVersion);
        await service.PerformAsync(update, SkillAction.Update);
        Assert.Same(Assert.Single(_backend.UpdateCheck.Updates), Assert.Single(Assert.Single(_backend.AppliedUpdates)));
    }
    [Fact]
    public void UnconfiguredInstalledSourceIsNeverContactedForUpdateCheck()
    {
        SeedUpdate();
        Assert.Empty(CreateService().CheckUpdates());
        Assert.Empty(_backend.CheckedSkills);
    }
    [Fact]
    public async Task RemovingSourceRevokesPendingUpdatesWithoutContactingRemovedSource()
    {
        AddSourceSetting("contoso/skills");
        SeedUpdate();
        var service = CreateService();
        var update = Assert.Single(service.CheckUpdates());
        service.RemoveSource(update.Source!);
        await Assert.ThrowsAsync<SkillsException>(() => service.PerformAsync(update, SkillAction.Update));
        Assert.Empty(_backend.AppliedUpdates);
        Assert.Single(_backend.CheckedSkills);
    }
    [Fact]
    public async Task UpdateCannotSubstituteUnconfiguredInstalledProvenanceWithAllowedDisplaySource()
    {
        AddSourceSetting("contoso/skills");
        SeedUpdate("stranger/skills");
        await Assert.ThrowsAsync<SkillsException>(() => CreateService().PerformAsync(Skill(), SkillAction.Update));
        Assert.Empty(_backend.CheckedSkills);
        Assert.Empty(_backend.AppliedUpdates);
    }
    [Fact]
    public void IndexUpdatesMustStayInsideConfiguredIndexPath()
    {
        AddSourceSetting("https://skills.contoso.com/team");
        _backend.Installed.Add(WellKnownSkill("deploy", "https://skills.contoso.com/team"));
        _backend.UpdateCheck = new([new()
        {
            Name = "deploy", Scope = SkillScope.Global, Source = "https://skills.contoso.com/other",
            CurrentHash = "1111111", LatestHash = "2222222",
        }], []);
        Assert.Empty(CreateService().CheckUpdates());
        Assert.Equal(["deploy"], Assert.Single(_backend.CheckedSkills));
    }
    [Fact]
    public async Task IndependentCaseSensitiveIndexPathsDoNotShareListingsOrRemoval()
    {
        var service = CreateService();
        foreach (string path in new[] { "Team", "team" })
        {
            string source = $"https://skills.contoso.com/{path}";
            _backend.SourceSkills[source] = [new(path, "Listed", null)];
            await service.AddSourceAsync(source);
        }
        Assert.Equal(["Team", "team"], service.Search("").Select(skill => skill.Id));
        service.RemoveSource(SkillSourceLocator.Parse("https://skills.contoso.com/Team")!);
        Assert.Equal("team", Assert.Single(service.Search("")).Id);
    }
    [Fact]
    public async Task AddSourceValidatesAndRemovalKeepsOtherSources()
    {
        var service = CreateService();
        _backend.SourceSkills["https://github.com/contoso/skills"] = [new("deploy", "Deploys", null)];
        await service.AddSourceAsync("contoso/skills");
        await service.AddSourceAsync("contoso/skills");
        Assert.Single(AgentSkillsService.GetConfiguredSources());
        await Assert.ThrowsAsync<SkillsException>(() => service.AddSourceAsync("https://empty.contoso.com"));
        await Assert.ThrowsAsync<ArgumentException>(() => service.AddSourceAsync("https://user:pass@example.com"));
        Assert.Single(AgentSkillsService.GetConfiguredSources());
        service.RemoveSource(SkillSourceLocator.Parse("contoso/skills")!);
        Assert.Empty(AgentSkillsService.GetConfiguredSources());
    }
    [Fact]
    public async Task MissingNotionCliAllowsSourceToBeSavedForSetup()
    {
        const string notion = "https://www.notion.so/aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
        _backend.SourceFailures[SkillSourceLocator.Parse(notion)!.InstallSource] = new SkillsException("Not installed", SkillsFailure.NotionCliMissing);
        var service = CreateService();
        await service.AddSourceAsync(notion);
        Assert.True(service.HasNotionSources);
        Assert.Equal(SkillSourceKind.Notion, Assert.Single(AgentSkillsService.GetConfiguredSources()).Kind);
    }
    [Fact]
    public async Task RemoveAlreadyMissingSkillSucceedsAndUsesSkillName()
    {
        _backend.RemoveStatus = SkillOperationStatus.NotFound;
        await CreateService().PerformAsync(Skill(), SkillAction.Remove);
        Assert.Equal(["deploy"], _backend.Removed);
    }
    [Fact]
    public async Task CancelledOperationDoesNotReachBackend()
    {
        AddSourceSetting("contoso/skills");
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateService().PerformAsync(Skill(), SkillAction.Install, cancellationToken: cancel.Token));
        Assert.Empty(_backend.Installs);
    }
    [Fact]
    public void SourcesTargetsDiscoveryAndNotionPinSurviveSettingsExportImport()
    {
        AddSourceSetting("contoso/skills");
        AgentSkillsService.TargetAgents = ["codex"];
        CreateService().SkillsShDiscoveryEnabled = true;
        Assert.True(NotionCliTool.TrySetPinnedVersion("0.23.9"));
        Settings.ImportFromString_JSON(Settings.ExportToString_JSON());
        Assert.Equal("contoso/skills", Assert.Single(AgentSkillsService.GetConfiguredSources()).Name);
        Assert.Equal(["codex"], AgentSkillsService.TargetAgents);
        Assert.True(CreateService().SkillsShDiscoveryEnabled);
        Assert.Equal("0.23.9", NotionCliTool.PinnedVersion);
    }
    private static InstalledSkillInfo WellKnownSkill(string name, string source) => new()
    {
        Name = name,
        Description = "",
        Path = "unused",
        Scope = SkillScope.Global,
        Agents = ["claude-code"],
        SourceType = "well-known",
        Source = new Uri(source).Host,
        SourceBaseUrl = source,
        SourceUrl = "https://downloads.contoso.com/SKILL.md",
        Hash = "1111111aaaa",
    };
}
