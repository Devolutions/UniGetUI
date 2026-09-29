using Devolutions.AgentSkills;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.Managers.SkillsManager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

namespace UniGetUI.PackageEngine.Tests;

public sealed class AgentSkillsManagerTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        nameof(AgentSkillsManagerTests),
        Guid.NewGuid().ToString("N")
    );

    private readonly FakeSkillsBackend _backend = new();

    public AgentSkillsManagerTests()
    {
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        SecureSettings.TEST_SecureSettingsRootOverride = Path.Combine(_testRoot, "SecureSettings");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Settings.ResetSettings();
    }

    public void Dispose()
    {
        Settings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        SecureSettings.TEST_SecureSettingsRootOverride = null;
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private AgentSkills CreateManager()
    {
        var manager = new AgentSkills(_backend);
        manager.Initialize();
        Assert.True(manager.IsReady());
        return manager;
    }

    private static void AddSourceSetting(string source) =>
        Settings.AddToList(AgentSkills.SourcesListKey, source);

    [Fact]
    public void SearchFiltersTheSkillsOfAddedSources()
    {
        AddSourceSetting("https://skills.contoso.com");
        _backend.SourceSkills["https://skills.contoso.com"] =
        [
            new AvailableSkill("code-review", "Reviews pull requests", null),
            new AvailableSkill("release-notes", "Writes release notes", null),
        ];
        var manager = CreateManager();

        var packages = manager.FindPackages("review");

        var package = Assert.Single(packages);
        Assert.Equal("code-review", package.Id);
        Assert.Equal("skills.contoso.com", package.Source.Name);
    }

    [Fact]
    public void BrowsingListsEverySkillOfTheAddedSources()
    {
        AddSourceSetting("https://skills.contoso.com");
        _backend.SourceSkills["https://skills.contoso.com"] =
        [
            new AvailableSkill("code-review", "Reviews pull requests", null),
            new AvailableSkill("release-notes", "Writes release notes", null),
        ];
        var manager = CreateManager();

        var packages = manager.FindPackages("");

        Assert.True(manager.Capabilities.CanListAllPackages);
        Assert.Equal(["code-review", "release-notes"], packages.Select(package => package.Id).Order());
        Assert.Equal(["skills.contoso.com"], manager.GetBrowsableSources().Select(source => source.Name));
    }

    [Fact]
    public void WithoutConfiguredSourcesSearchReturnsNoSkills()
    {
        var manager = CreateManager();

        Assert.Empty(manager.FindPackages("anything"));
        Assert.Empty(manager.GetBrowsableSources());
        Assert.Empty(_backend.ListedSources);
    }

    [Fact]
    public void InstalledSkillsShowTheirSourceAndShortHash()
    {
        _backend.Installed.Add(FakeSkillsBackend.TrackedSkill("deploy", "contoso/agent-skills", "0123456789abcdef0123"));
        _backend.Installed.Add(
            new InstalledSkillInfo
            {
                Name = "handmade",
                Description = "",
                Path = "/home/test/.claude/skills/handmade",
                Scope = SkillScope.Global,
                Agents = ["claude-code"],
            }
        );
        var manager = CreateManager();

        var packages = manager.GetInstalledPackages();

        var tracked = Assert.Single(packages, p => p.Id == "deploy");
        Assert.Equal("0123456", tracked.VersionString);
        Assert.Equal("contoso/agent-skills", tracked.Source.Name);
        var untracked = Assert.Single(packages, p => p.Id == "handmade");
        Assert.Equal(AgentSkills.UntrackedVersion, untracked.VersionString);
        Assert.True(untracked.Source.IsVirtualManager);
    }

    [Fact]
    public void ADiscoveredSkillIsRecognizedOnceItIsInstalled()
    {
        AddSourceSetting("contoso/agent-skills");
        _backend.SourceSkills["https://github.com/contoso/agent-skills"] = [new AvailableSkill("deploy", "Deploys", null)];
        _backend.Installed.Add(FakeSkillsBackend.TrackedSkill("deploy", "Contoso/Agent-Skills", "abcdef1234"));
        var manager = CreateManager();

        var found = Assert.Single(manager.FindPackages("deploy"));
        var installed = Assert.Single(manager.GetInstalledPackages());

        Assert.True(found.IsEquivalentTo(installed));
        Assert.Same(found.Source, installed.Source);
    }

    [Fact]
    public void UpdatesAreOfferedOnlyForAllowedSources()
    {
        AddSourceSetting("contoso/agent-skills");
        _backend.Installed.Add(FakeSkillsBackend.TrackedSkill("allowed", "contoso/agent-skills", "1111111aaaa"));
        _backend.Installed.Add(FakeSkillsBackend.TrackedSkill("stranger", "someone/else", "2222222bbbb"));
        _backend.UpdateCheck = new SkillUpdateCheckResult(
            [
                new SkillUpdate { Name = "allowed", Scope = SkillScope.Global, Source = "contoso/agent-skills", CurrentHash = "1111111aaaa", LatestHash = "3333333cccc" },
                new SkillUpdate { Name = "stranger", Scope = SkillScope.Global, Source = "someone/else", CurrentHash = "2222222bbbb", LatestHash = "4444444dddd" },
            ],
            []
        );
        var manager = CreateManager();

        var update = Assert.Single(manager.GetAvailableUpdates());

        Assert.Equal("allowed", update.Id);
        Assert.Equal("1111111", update.VersionString);
        Assert.Equal("3333333", update.NewVersionString);
        Assert.True(update.IsUpgradable);
        Assert.Equal(["allowed"], Assert.Single(_backend.CheckedSkills));
    }

    [Fact]
    public void UpdateCheckDoesNotContactAnUnconfiguredInstalledSource()
    {
        _backend.Installed.Add(FakeSkillsBackend.TrackedSkill("stranger", "someone/else", "2222222bbbb"));
        var manager = CreateManager();

        Assert.Empty(manager.GetAvailableUpdates());
        Assert.Empty(_backend.CheckedSkills);
    }

    [Fact]
    public void HashVersionsAreEqualOrIncomparable()
    {
        var manager = CreateManager();

        Assert.Equal(0, manager.CompareVersions("abc1234", "ABC1234"));
        Assert.Null(manager.CompareVersions("abc1234", "0000000"));
        Assert.Null(manager.CompareVersions("fffffff", "0000001"));
    }

    [Fact]
    public void SourceFactoryResolvesOnlySourcesSkillsMayComeFrom()
    {
        AddSourceSetting("https://skills.contoso.com");
        var manager = CreateManager();
        var factory = manager.SourcesHelper.Factory;

        Assert.Null(factory.GetSourceIfExists("contoso/agent-skills"));
        Assert.NotNull(factory.GetSourceIfExists("skills.contoso.com"));
        Assert.Null(factory.GetSourceIfExists("skills.fabrikam.com"));

        AddSourceSetting("contoso/agent-skills");
        Assert.NotNull(factory.GetSourceIfExists("contoso/agent-skills"));
        Assert.NotNull(factory.GetSourceIfExists("skills.contoso.com"));
    }

    [Fact]
    public async Task InstallPassesTheSourceSkillAndTargetAgentsToTheLibrary()
    {
        Settings.SetList(AgentSkills.TargetAgentsListKey, ["claude-code", "codex"]);
        AddSourceSetting("contoso/agent-skills");
        var manager = CreateManager();
        var package = new Package(
            "Deploy",
            "deploy",
            AgentSkills.LatestVersion,
            manager.SourcesHelper.Factory.GetSourceOrDefault("contoso/agent-skills"),
            manager
        );
        var output = new RecordingOutput();

        var veredict = await Perform(manager, package, OperationType.Install, output);

        Assert.Equal(OperationVeredict.Success, veredict);
        var install = Assert.Single(_backend.Installs);
        // A full github.com address, which GH_HOST cannot point at a GitHub Enterprise server
        Assert.Equal("https://github.com/contoso/agent-skills", install.Source);
        Assert.Equal("deploy", install.Skill);
        Assert.Equal(["claude-code", "codex"], install.Agents);
        Assert.Contains("Installing deploy", output.Info);
    }

    [Fact]
    public async Task InstallFromASourceThatIsNotAllowedIsRefused()
    {
        var manager = CreateManager();
        var package = new Package(
            "Deploy",
            "deploy",
            AgentSkills.LatestVersion,
            new ManagerSource(manager, "contoso/agent-skills", new Uri("https://github.com/contoso/agent-skills")),
            manager
        );
        var output = new RecordingOutput();

        var veredict = await Perform(manager, package, OperationType.Install, output);

        Assert.Equal(OperationVeredict.Failure, veredict);
        Assert.Empty(_backend.Installs);
        Assert.Contains("Add this source first", output.FailureMessage);
    }

    [Fact]
    public async Task FailedInstallReportsTheLibraryError()
    {
        _backend.InstallStatus = SkillOperationStatus.Failed;
        AddSourceSetting("contoso/agent-skills");
        var manager = CreateManager();
        var package = new Package(
            "Deploy",
            "deploy",
            AgentSkills.LatestVersion,
            manager.SourcesHelper.Factory.GetSourceOrDefault("contoso/agent-skills"),
            manager
        );
        var output = new RecordingOutput();

        var veredict = await Perform(manager, package, OperationType.Install, output);

        Assert.Equal(OperationVeredict.Failure, veredict);
        Assert.Equal("The install failed", output.FailureMessage);
    }

    [Fact]
    public async Task UpdateAppliesTheUpdateFoundByTheLastCheck()
    {
        AddSourceSetting("contoso/agent-skills");
        _backend.Installed.Add(FakeSkillsBackend.TrackedSkill("deploy", "contoso/agent-skills", "1111111aaaa"));
        var found = new SkillUpdate
        {
            Name = "deploy",
            Scope = SkillScope.Global,
            Source = "contoso/agent-skills",
            CurrentHash = "1111111aaaa",
            LatestHash = "2222222bbbb",
        };
        _backend.UpdateCheck = new SkillUpdateCheckResult([found], []);
        var manager = CreateManager();
        var update = Assert.Single(manager.GetAvailableUpdates());

        // The first update applies what the check found, without checking again
        Assert.Equal(OperationVeredict.Success, await Perform(manager, update, OperationType.Update, new RecordingOutput()));
        Assert.Same(found, Assert.Single(Assert.Single(_backend.AppliedUpdates)));
        Assert.Equal(["deploy"], Assert.Single(_backend.CheckedSkills));

        // A later one checks the skill again
        Assert.Equal(OperationVeredict.Success, await Perform(manager, update, OperationType.Update, new RecordingOutput()));
        Assert.Equal(2, _backend.AppliedUpdates.Count);
        Assert.Equal(["deploy"], _backend.CheckedSkills[^1]);
    }

    [Fact]
    public async Task AnUpdateIsHeldToTheSourceTheSkillCameFromWhateverSourceThePackageNames()
    {
        // The library updates a skill from the source it was installed from, so naming an added
        // source for the package must not let an update from another source through
        AddSourceSetting("contoso/agent-skills");
        _backend.Installed.Add(FakeSkillsBackend.TrackedSkill("stranger", "someone/else", "2222222bbbb"));
        _backend.UpdateCheck = new SkillUpdateCheckResult(
            [
                new SkillUpdate { Name = "stranger", Scope = SkillScope.Global, Source = "someone/else", CurrentHash = "2222222bbbb", LatestHash = "4444444dddd" },
            ],
            []
        );
        var manager = CreateManager();
        var package = new Package(
            "Stranger",
            "stranger",
            "2222222",
            manager.SourcesHelper.Factory.GetSourceOrDefault("contoso/agent-skills"),
            manager
        );
        var output = new RecordingOutput();

        var veredict = await Perform(manager, package, OperationType.Update, output);

        Assert.Equal(OperationVeredict.Failure, veredict);
        Assert.Empty(_backend.AppliedUpdates);
        Assert.Contains("someone/else", output.FailureMessage);
    }

    [Fact]
    public async Task AnUpdateFoundBeforeItsSourceWasRemovedIsNotApplied()
    {
        AddSourceSetting("contoso/agent-skills");
        _backend.Installed.Add(FakeSkillsBackend.TrackedSkill("allowed", "contoso/agent-skills", "1111111aaaa"));
        _backend.UpdateCheck = new SkillUpdateCheckResult(
            [
                new SkillUpdate { Name = "allowed", Scope = SkillScope.Global, Source = "contoso/agent-skills", CurrentHash = "1111111aaaa", LatestHash = "3333333cccc" },
            ],
            []
        );
        var manager = CreateManager();
        var update = Assert.Single(manager.GetAvailableUpdates());

        Settings.SetList(AgentSkills.SourcesListKey, new List<string>());
        var veredict = await Perform(manager, update, OperationType.Update, new RecordingOutput());

        Assert.Equal(OperationVeredict.Failure, veredict);
        Assert.Empty(_backend.AppliedUpdates);
    }

    [Fact]
    public void AnIndexUpdateIsCheckedAgainstTheIndexItComesFromNotTheAddressOfItsFile()
    {
        AddSourceSetting("https://skills.contoso.com");
        // Installed from another index whose skill file sits on the added host: an index picks where
        // its files live, so that address says nothing about where the update comes from
        _backend.Installed.Add(
            WellKnownSkill("helper", "skills.fabrikam.com", "https://skills.contoso.com/.well-known/agent-skills/helper/SKILL.md")
        );
        _backend.UpdateCheck = new SkillUpdateCheckResult(
            [
                new SkillUpdate { Name = "helper", Scope = SkillScope.Global, Source = "https://skills.fabrikam.com", CurrentHash = "sha256:1111111aaaa" },
            ],
            []
        );
        var manager = CreateManager();

        Assert.Empty(manager.GetAvailableUpdates());
        Assert.Empty(_backend.CheckedSkills);
    }

    [Fact]
    public void AnIndexUpdateIsOfferedOnlyFromWithinTheAddedIndexAddress()
    {
        AddSourceSetting("https://storage.contoso.com/team");
        _backend.Installed.Add(WellKnownSkill("helper", "storage.contoso.com", "https://cdn.contoso.net/helper/SKILL.md", "https://storage.contoso.com/team"));
        _backend.Installed.Add(WellKnownSkill("other", "storage.contoso.com", "https://storage.contoso.com/other-team/other/SKILL.md", "https://storage.contoso.com/other-team"));
        _backend.UpdateCheck = new SkillUpdateCheckResult(
            [
                new SkillUpdate { Name = "helper", Scope = SkillScope.Global, Source = "https://storage.contoso.com/team", CurrentHash = "sha256:1111111aaaa" },
                new SkillUpdate { Name = "other", Scope = SkillScope.Global, Source = "https://storage.contoso.com/other-team", CurrentHash = "sha256:2222222bbbb" },
            ],
            []
        );
        var manager = CreateManager();

        var update = Assert.Single(manager.GetAvailableUpdates());

        Assert.Equal("helper", update.Id);
        Assert.Equal("storage.contoso.com/team", update.Source.Name);
        Assert.Equal(["helper"], Assert.Single(_backend.CheckedSkills));
    }

    [Fact]
    public async Task ASourceNameResolvesToTheAddedSourceItFallsWithin()
    {
        AddSourceSetting("https://storage.contoso.com/team");
        var manager = CreateManager();
        var factory = manager.SourcesHelper.Factory;

        Assert.Null(factory.GetSourceIfExists("https://storage.contoso.com/elsewhere"));
        var source = factory.GetSourceIfExists("https://storage.contoso.com/team/archive");
        Assert.NotNull(source);

        // Installed from the index as it was added, not from the address the name brought along
        var package = new Package("Review", "review", AgentSkills.LatestVersion, source!, manager);
        Assert.Equal(OperationVeredict.Success, await Perform(manager, package, OperationType.Install, new RecordingOutput()));
        Assert.Equal("https://storage.contoso.com/team", Assert.Single(_backend.Installs).Source);
    }

    [Fact]
    public async Task IndexesOnTheSameHostAreDistinctAndRemovingOneKeepsTheOther()
    {
        const string first = "https://storage.contoso.com/team";
        const string second = "https://storage.contoso.com/other";
        _backend.SourceSkills[first] = [new AvailableSkill("review", "Reviews code", null)];
        _backend.SourceSkills[second] = [new AvailableSkill("format", "Formats code", null)];
        var manager = CreateManager();
        var helper = (IInProcessSourceHelper)manager.SourcesHelper;

        foreach (var url in new[] { first, second })
            Assert.Equal(OperationVeredict.Success, await helper.AddSourceAsync(
                new ManagerSource(manager, url, new Uri(url)), new RecordingOutput(), CancellationToken.None));

        Assert.Equal(["storage.contoso.com/team", "storage.contoso.com/other"],
            manager.GetBrowsableSources().Select(source => source.Name));
        Assert.Equal(["format", "review"], manager.FindPackages("").Select(package => package.Id).Order());
        Assert.Equal(2, _backend.ListedSources.Count);
        _backend.Installed.Add(WellKnownSkill("review", "storage.contoso.com", "https://cdn.contoso.net/review/SKILL.md", first));
        _backend.Installed.Add(WellKnownSkill("format", "storage.contoso.com", "https://cdn.contoso.net/format/SKILL.md", second));
        Assert.Equal(["storage.contoso.com/other", "storage.contoso.com/team"],
            manager.GetInstalledPackages().Select(package => package.Source.Name).Order());
        _backend.UpdateCheck = new(
            [
                new SkillUpdate { Name = "review", Scope = SkillScope.Global, Source = first, CurrentHash = "sha256:1111111aaaa", LatestHash = "sha256:3333333cccc" },
                new SkillUpdate { Name = "format", Scope = SkillScope.Global, Source = second, CurrentHash = "sha256:2222222bbbb", LatestHash = "sha256:4444444dddd" },
            ],
            []
        );
        Assert.Equal(["format", "review"], manager.GetAvailableUpdates().Select(package => package.Id).Order());

        await helper.RemoveSourceAsync(
            manager.SourcesHelper.Factory.GetSourceOrDefault("storage.contoso.com/team"),
            new RecordingOutput(), CancellationToken.None);

        Assert.Equal(["storage.contoso.com/other"], manager.GetBrowsableSources().Select(source => source.Name));
        Assert.Equal("format", Assert.Single(manager.FindPackages("")).Id);
        Assert.Equal("format", Assert.Single(manager.GetAvailableUpdates()).Id);
        Assert.Equal(["format"], _backend.CheckedSkills[^1]);
        Assert.Equal([second], Settings.GetList<string>(AgentSkills.SourcesListKey));
    }

    [Fact]
    public void IndexPathsDifferingOnlyInCaseDoNotShareListings()
    {
        const string upper = "https://storage.contoso.com/Team";
        const string lower = "https://storage.contoso.com/team";
        AddSourceSetting(upper);
        AddSourceSetting(lower);
        _backend.SourceSkills[upper] = [new AvailableSkill("review", "Review from Team", null)];
        _backend.SourceSkills[lower] = [new AvailableSkill("review", "Review from team", null)];
        var manager = CreateManager();

        Assert.Equal(["storage.contoso.com/Team", "storage.contoso.com/team"],
            manager.FindPackages("review").Select(package => package.Source.Name).Order(StringComparer.Ordinal));
        Assert.Equal(2, _backend.ListedSources.Count);
    }

    [Fact]
    public async Task SourcesWithoutAnAddressOfTheirOwnAreNotInstalledFrom()
    {
        // The local source and placeholder sources carry a stand-in address, which must not be taken
        // for an index, even when that address was added as one
        AddSourceSetting("https://agentskills.io");
        var manager = CreateManager();
        IManagerSource[] sources = [manager.LocalSource, manager.SourcesHelper.Factory.GetSourceOrDefault("No such source")];

        foreach (var source in sources)
        {
            var package = new Package("Review", "review", "1111111", source, manager);
            Assert.Equal(OperationVeredict.Failure, await Perform(manager, package, OperationType.Install, new RecordingOutput()));
        }

        Assert.Empty(_backend.Installs);
    }

    [Fact]
    public async Task UninstallingASkillThatIsAlreadyGoneSucceeds()
    {
        _backend.RemoveStatus = SkillOperationStatus.NotFound;
        var manager = CreateManager();
        var package = new Package("Deploy", "deploy", "1111111", manager.LocalSource, manager);

        var veredict = await Perform(manager, package, OperationType.Uninstall, new RecordingOutput());

        Assert.Equal(OperationVeredict.Success, veredict);
        Assert.Equal(["deploy"], _backend.Removed);
    }

    [Fact]
    public async Task AddingASourceChecksItOffersSkillsAndRemembersIt()
    {
        _backend.SourceSkills["https://skills.contoso.com"] = [new AvailableSkill("code-review", "Reviews code", null)];
        var manager = CreateManager();
        var helper = (IInProcessSourceHelper)manager.SourcesHelper;
        var source = new ManagerSource(manager, "Contoso", new Uri("https://skills.contoso.com"));

        var veredict = await helper.AddSourceAsync(source, new RecordingOutput(), CancellationToken.None);

        Assert.Equal(OperationVeredict.Success, veredict);
        Assert.Equal(["https://skills.contoso.com"], Settings.GetList<string>(AgentSkills.SourcesListKey));
        Assert.Contains(manager.SourcesHelper.GetSources(), s => s.Name == "skills.contoso.com");
        // The listing fetched to validate the source serves the next search
        Assert.Single(manager.FindPackages("review"));
        Assert.Single(_backend.ListedSources);

        veredict = await helper.RemoveSourceAsync(
            manager.SourcesHelper.Factory.GetSourceOrDefault("skills.contoso.com"),
            new RecordingOutput(),
            CancellationToken.None
        );

        Assert.Equal(OperationVeredict.Success, veredict);
        Assert.Empty(Settings.GetList<string>(AgentSkills.SourcesListKey) ?? []);
    }

    [Fact]
    public async Task AddingAndRemovingARepositoryRefreshesGitDependency()
    {
        const string url = "https://github.com/contoso/agent-skills";
        _backend.SourceSkills[url] = [new AvailableSkill("review", "Reviews code", null)];
        var manager = CreateManager();
        var helper = (IInProcessSourceHelper)manager.SourcesHelper;
        Assert.Empty(manager.Dependencies);

        var source = new ManagerSource(manager, "Contoso", new Uri(url));
        Assert.Equal(OperationVeredict.Success,
            await helper.AddSourceAsync(source, new RecordingOutput(), CancellationToken.None));
        Assert.Equal("Git", Assert.Single(manager.Dependencies).Name);

        Assert.Equal(OperationVeredict.Success,
            await helper.RemoveSourceAsync(
                manager.SourcesHelper.Factory.GetSourceOrDefault("contoso/agent-skills"),
                new RecordingOutput(), CancellationToken.None));
        Assert.Empty(manager.Dependencies);
    }

    [Fact]
    public void SourceDependencyCanBeCheckedBeforeAddingTheSource()
    {
        var manager = CreateManager();

        Assert.Equal("Git", manager.GetDependencyForSource(new Uri("https://github.com/contoso/agent-skills"))?.Name);
        Assert.Equal(NotionCliTool.Name, manager.GetDependencyForSource(new Uri(NotionDatabase))?.Name);
        Assert.Null(manager.GetDependencyForSource(new Uri("https://skills.contoso.com/team")));
        Assert.Empty(manager.Dependencies);
    }

    [Fact]
    public async Task AddingASourceWithoutSkillsFails()
    {
        var manager = CreateManager();
        var helper = (IInProcessSourceHelper)manager.SourcesHelper;
        var output = new RecordingOutput();

        var veredict = await helper.AddSourceAsync(
            new ManagerSource(manager, "Typo", new Uri("https://github.com/owner/no-such-repo")),
            output,
            CancellationToken.None
        );

        Assert.Equal(OperationVeredict.Failure, veredict);
        Assert.Equal("No valid skills found.", output.FailureMessage);
        Assert.Empty(Settings.GetList<string>(AgentSkills.SourcesListKey) ?? []);
    }

    [Fact]
    public void AddedSourcesAndTargetAgentsSurviveASettingsExportAndImport()
    {
        // Importing settings clears them and only restores the names Settings.K knows
        AddSourceSetting("https://skills.contoso.com");
        AgentSkills.TargetAgents = ["claude-code"];

        Settings.ImportFromString_JSON(Settings.ExportToString_JSON());

        Assert.Contains(AgentSkills.GetConfiguredSources(), source => source.Name == "skills.contoso.com");
        Assert.Equal(["claude-code"], AgentSkills.TargetAgents);
    }

    [Fact]
    public void ManagerIsDisabledByDefaultWhenNoAgentIsFound()
    {
        _backend.Agents.Clear();
        var manager = new AgentSkills(_backend);

        manager.Initialize();

        Assert.False(manager.IsEnabled());
        Assert.True(Settings.Get(Settings.K.SkillsDefaultEnablementApplied));
    }

    [Fact]
    public void DefaultEnablementNeverOverridesTheUser()
    {
        _backend.Agents.Clear();
        Settings.SetDictionaryItem(Settings.K.DisabledManagers, "Skills", false);
        var manager = new AgentSkills(_backend);

        manager.Initialize();

        Assert.True(manager.IsEnabled());
    }

    [Fact]
    public void GitIsOnlyADependencyWithARepositorySource()
    {
        var manager = CreateManager();
        Assert.Empty(manager.Dependencies);

        _backend.SourceSkills["owner/repo"] = [];
        AddSourceSetting("owner/repo");
        manager.Initialize();

        Assert.Equal("Git", Assert.Single(manager.Dependencies).Name);
    }

    [Fact]
    public void RepositorySkillsLinkOnlyToTheirConfiguredRepository()
    {
        _backend.SourceSkills["https://github.com/contoso/private-skills"] =
            [new AvailableSkill("review-ui-content", "Reviews UI content", null)];
        AddSourceSetting("https://github.com/contoso/private-skills");
        var manager = CreateManager();
        var package = Assert.Single(manager.FindPackages("review"));
        var details = new PackageDetails(package);

        manager.DetailsHelper.GetDetails(details);

        Assert.Null(details.ManifestUrl);
        Assert.Equal("contoso", details.Publisher);
        Assert.Equal("https://github.com/contoso/private-skills", details.HomepageUrl?.ToString().TrimEnd('/'));
    }

    private const string NotionDatabase = "https://app.notion.com/p/1a2b3c4d5e6f4a7b8c9d0e1f2a3b4c5d";

    [Fact]
    public async Task ANotionDatabaseCanBeAddedBeforeSigningIn()
    {
        _backend.SourceFailures[NotionDatabase] = new SkillsException(
            "Sign in to Notion to use Notion sources.",
            SkillsFailure.NotionSignedOut
        );
        var manager = CreateManager();
        var helper = (IInProcessSourceHelper)manager.SourcesHelper;
        var output = new RecordingOutput();

        var veredict = await helper.AddSourceAsync(
            new ManagerSource(
                manager,
                "Team skills",
                new Uri("https://app.notion.com/p/1a2b3c4d5e6f4a7b8c9d0e1f2a3b4c5d?v=6f7a8b9c0d1e4f2a8b3c4d5e6f7a8b9c&source=copy_link")
            ),
            output,
            CancellationToken.None
        );

        Assert.Equal(OperationVeredict.Success, veredict);
        Assert.Equal([NotionDatabase], Settings.GetList<string>(AgentSkills.SourcesListKey));
        Assert.Contains("Sign in to Notion to use Notion sources.", output.Info);
        Assert.True(manager.HasNotionSources);
        Assert.Equal(NotionCliTool.Name, Assert.Single(manager.Dependencies).Name);
    }

    [Fact]
    public async Task ANotionDatabaseThatIsNotSharedIsNotAdded()
    {
        _backend.SourceFailures[NotionDatabase] = new SkillsException(
            "Notion could not find the Notion database",
            SkillsFailure.NotionNotFound
        );
        var manager = CreateManager();
        var helper = (IInProcessSourceHelper)manager.SourcesHelper;

        var veredict = await helper.AddSourceAsync(
            new ManagerSource(manager, "Team skills", new Uri(NotionDatabase)),
            new RecordingOutput(),
            CancellationToken.None
        );

        Assert.Equal(OperationVeredict.Failure, veredict);
        Assert.Empty(Settings.GetList<string>(AgentSkills.SourcesListKey) ?? []);
    }

    [Fact]
    public void NotionSkillsAreListedAndInstalledFromTheirDatabase()
    {
        _backend.SourceSkills[NotionDatabase] = [new AvailableSkill("proofreader-copyeditor", "Proofreads marketing copy", null)];
        AddSourceSetting(NotionDatabase);
        var manager = CreateManager();

        var found = Assert.Single(manager.FindPackages(""));
        Assert.Equal("proofreader-copyeditor", found.Id);
        Assert.Equal("app.notion.com/p/1a2b3c4d5e6f4a7b8c9d0e1f2a3b4c5d", found.Source.Name);
        Assert.Equal(NotionDatabase, manager.GetInstallSource(found.Source)?.InstallSource);
    }

    [Fact]
    public void UpdatesOfNotionSkillsComeFromTheirDatabase()
    {
        AddSourceSetting(NotionDatabase);
        _backend.SourceSkills[NotionDatabase] = [];
        _backend.Installed.Add(NotionSkill("tone-reviewer", "v1-aaaaaaaa"));
        _backend.UpdateCheck = new(
            [
                new SkillUpdate
                {
                    Name = "tone-reviewer",
                    Scope = SkillScope.Global,
                    Source = NotionDatabase,
                    CurrentHash = "v1-aaaaaaaa",
                    LatestHash = "v2-bbbbbbbb",
                },
            ],
            []
        );
        var manager = CreateManager();

        var update = Assert.Single(manager.GetAvailableUpdates());
        Assert.Equal("app.notion.com/p/1a2b3c4d5e6f4a7b8c9d0e1f2a3b4c5d", update.Source.Name);
        Assert.Equal("v2-bbbb", update.NewVersionString);

        // Not offered once the database is no longer a source
        Settings.SetList(AgentSkills.SourcesListKey, new List<string>());
        Assert.Empty(manager.GetAvailableUpdates());
    }

    [Fact]
    public void TheNotionCliIsOnlyADependencyWithANotionSource()
    {
        var manager = CreateManager();
        Assert.DoesNotContain(manager.Dependencies, d => d.Name == NotionCliTool.Name);

        _backend.SourceSkills[NotionDatabase] = [];
        AddSourceSetting(NotionDatabase);
        manager.Initialize();

        Assert.Equal(NotionCliTool.Name, Assert.Single(manager.Dependencies).Name);
    }

    [Theory]
    [InlineData("0.23.10", true)]
    [InlineData(" 0.23.9 ", true)]
    [InlineData("1.0.0-beta.2", true)]
    [InlineData("", true)]
    [InlineData("latest", false)]
    [InlineData("0.23.10; calc", false)]
    [InlineData("0.23.10}\" & calc & \"{", false)]
    public void OnlyPlainVersionsOfTheNotionCliCanBePinned(string version, bool accepted)
    {
        Assert.Equal(accepted, NotionCliTool.TrySetPinnedVersion(version));
        Assert.Equal(accepted && version.Trim().Length > 0 ? version.Trim() : null, NotionCliTool.PinnedVersion);
    }

    [Fact]
    public void APinnedNotionCliIsInstalledAtThatVersionAndNotUpdated()
    {
        Assert.True(NotionCliTool.TrySetPinnedVersion("0.23.9"));

        Assert.Contains("--version 0.23.9", NotionCliTool.CreateDependency().FancyInstallCommand);
        Assert.True(UniGetUI.PackageEngine.Classes.Packages.Classes.IgnoredUpdatesDatabase.HasUpdatesIgnored("winget\\Notion.ntn"));

        Assert.True(NotionCliTool.TrySetPinnedVersion(""));
        Assert.DoesNotContain("--version", NotionCliTool.CreateDependency().FancyInstallCommand);
        Assert.False(UniGetUI.PackageEngine.Classes.Packages.Classes.IgnoredUpdatesDatabase.HasUpdatesIgnored("winget\\Notion.ntn"));
    }

    [Fact]
    public async Task SigningInToNotionListsTheNotionSourcesAgain()
    {
        _backend.NotionStatus = new(NotionCliState.SignedOut, "0.23.10", null, null);
        _backend.SourceSkills[NotionDatabase] = [];
        AddSourceSetting(NotionDatabase);
        var manager = CreateManager();
        // The listing started when the manager loaded
        await manager.Listings.Refresh(SkillSourceLocator.Parse(NotionDatabase)!);
        int listed = _backend.ListedSources.Count;

        var signIn = await manager.BeginNotionSignInAsync(CancellationToken.None);
        await manager.CompleteNotionSignInAsync(CancellationToken.None);

        Assert.Equal("K7Q-2MX", signIn.VerificationCode);
        Assert.Equal(NotionCliState.SignedIn, (await manager.GetNotionStatusAsync()).State);
        Assert.True(SpinWait.SpinUntil(() => _backend.ListedSources.Count > listed, TimeSpan.FromSeconds(10)));
    }

    private static InstalledSkillInfo NotionSkill(string name, string version) =>
        new()
        {
            Name = name,
            Description = "",
            Path = $"/home/test/.agents/skills/{name}",
            Scope = SkillScope.Global,
            Agents = ["claude-code"],
            Source = NotionDatabase,
            SourceType = "notion",
            SourceUrl = "https://app.notion.com/p/4d5e6f7a8b9c4d0e9f1a3b4c5d6e7f80",
            Hash = version,
        };

    private static InstalledSkillInfo WellKnownSkill(string name, string host, string skillFileUrl, string? baseUrl = null) =>
        new()
        {
            Name = name,
            Description = "",
            Path = $"/home/test/.agents/skills/{name}",
            Scope = SkillScope.Global,
            Agents = ["claude-code"],
            Source = host,
            SourceType = "well-known",
            SourceUrl = skillFileUrl,
            SourceBaseUrl = baseUrl,
            Hash = "sha256:1111111aaaa",
        };

    private static Task<OperationVeredict> Perform(
        AgentSkills manager,
        IPackage package,
        OperationType operation,
        RecordingOutput output
    ) =>
        ((IInProcessPackageOperationHelper)manager.OperationHelper).PerformAsync(
            package,
            new InstallOptions(),
            operation,
            output,
            CancellationToken.None
        );

    private sealed class RecordingOutput : IOperationOutput
    {
        public List<string> Info { get; } = [];
        public List<string> Errors { get; } = [];
        public string FailureMessage { get; private set; } = "";

        void IOperationOutput.Info(string line) => Info.Add(line);

        public void Error(string line) => Errors.Add(line);

        public void Verbose(string line) { }

        public void SetFailureMessage(string message) => FailureMessage = message;
    }
}
