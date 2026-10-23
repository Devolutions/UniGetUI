using System.Text.Json;
using Devolutions.AgentSkills;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.Managers.SkillsManager;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

namespace UniGetUI.PackageEngine.Tests;

public sealed partial class AgentSkillsManagerTests
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

    private static Package RegistryPackage(AgentSkills manager, string skill, string owner = "contoso", string repo = "skills") =>
        new(skill, skill, AgentSkills.LatestVersion,
            manager.SourceFactory.GetOrCreate(SkillSourceLocator.GitHub(owner, repo)), manager);

    [Fact]
    public void RegistryDiscovery_DefaultAndExplicitOffNeverCallRegistry()
    {
        var registry = new RecordingRegistry();
        var manager = CreateManager(registry);

        Assert.False(manager.SkillsShDiscoveryEnabled);
        Assert.False(Settings.Get(Settings.K.SkillsShDiscoveryEnabled));
        Assert.Empty(manager.FindPackages("review"));
        manager.SkillsShDiscoveryEnabled = false;
        Assert.Empty(manager.FindPackages("review"));
        Assert.Empty(registry.Queries);
        Assert.Empty(manager.Dependencies);
        Assert.Empty(_backend.ListedSources);
    }

    [Fact]
    public void RegistryDiscovery_EnabledWithoutConfiguredSourcesKeepsFuzzyResultAndExactIdentity()
    {
        var registry = new RecordingRegistry { Results = [RegistryResult("react-components")] };
        var manager = CreateManager(registry);
        manager.SkillsShDiscoveryEnabled = true;

        var package = Assert.Single(manager.FindPackages("  UI widgets  "));

        Assert.Equal("react-components", package.Id); // No local substring match to the query.
        Assert.Equal("contoso/skills", package.Source.Name);
        Assert.Equal("https://github.com/contoso/skills", package.Source.Url.AbsoluteUri);
        Assert.Equal(["  UI widgets  "], registry.Queries);
        Assert.Empty(manager.GetBrowsableSources());
        Assert.Empty(AgentSkills.GetConfiguredSources());
        Assert.Empty(_backend.ListedSources);
        Assert.Empty(_backend.Installs);
        Assert.Equal("Git", Assert.Single(manager.Dependencies).Name);
        Assert.Equal("https://github.com/contoso/skills", manager.GetInstallSource(package)?.InstallSource);
    }

    [Fact]
    public void RegistryDiscovery_EmptyBrowsingAndOneCharacterStayLocalButTwoCharactersSearch()
    {
        const string local = "https://skills.contoso.com";
        AddSourceSetting(local);
        _backend.SourceSkills[local] = [new AvailableSkill("abacus", "Local", null)];
        var registry = new RecordingRegistry();
        var manager = CreateManager(registry);
        manager.SkillsShDiscoveryEnabled = true;

        Assert.Equal("abacus", Assert.Single(manager.FindPackages("")).Id);
        Assert.Empty(manager.FindPackages(" \t "));
        Assert.Equal("abacus", Assert.Single(manager.FindPackages("a")).Id);
        Assert.Empty(manager.FindPackages(" a "));
        Assert.Empty(registry.Queries);
        Assert.Equal(["abacus", "review"], manager.FindPackages("ab").Select(package => package.Id));
        Assert.Equal(["ab"], registry.Queries);
    }

    [Fact]
    public void RegistryDiscovery_UnionDeduplicatesOnlyMatchingSourceAndSkill()
    {
        const string configured = "https://github.com/contoso/skills";
        AddSourceSetting(configured);
        _backend.SourceSkills[configured] =
        [
            new AvailableSkill("review", "review", null),
            new AvailableSkill("local-review", "review", null),
            new AvailableSkill("not-matching", "different", null),
        ];
        var registry = new RecordingRegistry
        {
            Results = [RegistryResult(), RegistryResult(), RegistryResult("review", "fabrikam"),
                RegistryResult("unrelated-fuzzy")],
        };
        var manager = CreateManager(registry);
        manager.SkillsShDiscoveryEnabled = true;

        var packages = manager.FindPackages("review");

        Assert.Equal(["contoso/skills:review", "contoso/skills:local-review",
            "fabrikam/skills:review", "contoso/skills:unrelated-fuzzy"],
            packages.Select(package => $"{package.Source.Name}:{package.Id}"));
        Assert.Equal(["contoso/skills"], manager.GetBrowsableSources().Select(source => source.Name));
        Assert.Equal([configured], _backend.ListedSources);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void RegistryDiscovery_FailurePreservesConfiguredResults(int failure)
    {
        const string local = "https://skills.contoso.com";
        AddSourceSetting(local);
        _backend.SourceSkills[local] = [new AvailableSkill("local-review", "Review", null)];
        var registry = new RecordingRegistry
        {
            SearchResult = () => throw (failure switch
            {
                0 => new HttpRequestException("offline"),
                1 => new JsonException("bad response"),
                3 => new TaskCanceledException("cancelled task"),
                _ => new OperationCanceledException("cancelled"),
            }),
        };
        var manager = CreateManager(registry);
        manager.SkillsShDiscoveryEnabled = true;

        var package = Assert.Single(manager.FindPackages("review"));

        Assert.Equal("local-review", package.Id);
        Assert.Equal("skills.contoso.com", package.Source.Name);
        Assert.Equal(["review"], registry.Queries);
        Assert.Null(manager.GetInstallSource(RegistryPackage(manager, "review")));
        Assert.Equal([local], Settings.GetList<string>(AgentSkills.SourcesListKey));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RegistryDiscovery_ChangingPreferenceRevokesPreviouslyAdmittedSkill(bool stayEnabled)
    {
        var registry = new RecordingRegistry();
        var manager = CreateManager(registry);
        manager.SkillsShDiscoveryEnabled = true;
        var package = Assert.Single(manager.FindPackages("review"));
        Assert.Equal("https://github.com/contoso/skills", manager.GetInstallSource(package)?.InstallSource);

        manager.SkillsShDiscoveryEnabled = stayEnabled; // Even true -> true changes generation.

        Assert.Null(manager.GetInstallSource(package));
        var output = new RecordingOutput();
        Assert.Equal(OperationVeredict.Failure, await Perform(manager, package, OperationType.Install, output));
        Assert.Contains("Add this source first", output.FailureMessage);
        Assert.Empty(_backend.Installs);
        Assert.Empty(AgentSkills.GetConfiguredSources());
        if (!stayEnabled)
        {
            Assert.Empty(manager.FindPackages("review"));
            Assert.Equal(["review"], registry.Queries);
        }
    }

    public enum RegistryInvalidation { Disable, DisableAndReenable, SetEnabledAgain, RemoveSource }

    [Theory]
    [InlineData(RegistryInvalidation.Disable)]
    [InlineData(RegistryInvalidation.DisableAndReenable)]
    [InlineData(RegistryInvalidation.SetEnabledAgain)]
    [InlineData(RegistryInvalidation.RemoveSource)]
    public async Task RegistryDiscovery_InFlightResultsCannotCrossGenerationBoundary(RegistryInvalidation change)
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
            },
        };
        var manager = CreateManager(registry);
        manager.SkillsShDiscoveryEnabled = true;
        Task<IReadOnlyList<IPackage>> pending = Task.Run(() => manager.FindPackages("review"));
        try
        {
            // A timeout is only a deadlock guard: no assertion depends on elapsed time.
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            switch (change)
            {
                case RegistryInvalidation.Disable:
                    manager.SkillsShDiscoveryEnabled = false;
                    break;
                case RegistryInvalidation.DisableAndReenable:
                    manager.SkillsShDiscoveryEnabled = false;
                    manager.SkillsShDiscoveryEnabled = true;
                    break;
                case RegistryInvalidation.SetEnabledAgain:
                    manager.SkillsShDiscoveryEnabled = true;
                    break;
                case RegistryInvalidation.RemoveSource:
                    Assert.Equal(OperationVeredict.Success, await ((IInProcessSourceHelper)manager.SourcesHelper)
                        .RemoveSourceAsync(RegistryPackage(manager, "review").Source, new RecordingOutput(), CancellationToken.None));
                    break;
            }
        }
        finally
        {
            release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(30));
        }

        Assert.Empty(await pending);
        var stale = RegistryPackage(manager, "review");
        Assert.Null(manager.GetInstallSource(stale));
        Assert.Equal(OperationVeredict.Failure, await Perform(manager, stale, OperationType.Install, new RecordingOutput()));
        Assert.Empty(_backend.Installs);
        Assert.Empty(AgentSkills.GetConfiguredSources());

        // Fresh results in the new generation can be admitted, but never resurrect the old task.
        if (manager.SkillsShDiscoveryEnabled)
        {
            registry.SearchResult = null;
            Assert.Equal("review", Assert.Single(manager.FindPackages("review")).Id);
            Assert.Equal("https://github.com/contoso/skills", manager.GetInstallSource(stale)?.InstallSource);
        }
    }

    [Fact]
    public async Task RegistryInstall_AdmitsExactSkillAndRepoWithoutTrustingBundlesOrSiblings()
    {
        var manager = CreateManager(new RecordingRegistry());
        manager.SkillsShDiscoveryEnabled = true;
        var package = Assert.Single(manager.FindPackages("review"));

        Assert.Null(manager.SourceFactory.GetSourceIfExists("contoso/skills"));
        Assert.Null(manager.SourceFactory.GetSourceIfExists("https://github.com/contoso/skills"));
        Assert.Empty(manager.SourceFactory.GetAvailableSources());
        Assert.Null(manager.GetInstallSource(package.Source)); // Source-only authorization stays configured-only.
        foreach (var stranger in new[]
        {
            RegistryPackage(manager, "sibling"),
            RegistryPackage(manager, "review", "fabrikam"),
            RegistryPackage(manager, "review", repo: "other"),
        })
        {
            var output = new RecordingOutput();
            Assert.Null(manager.GetInstallSource(stranger));
            Assert.Equal(OperationVeredict.Failure, await Perform(manager, stranger, OperationType.Install, output));
            Assert.Contains("Add this source first", output.FailureMessage);
        }
        Assert.Empty(_backend.Installs);
        Assert.Empty(AgentSkills.GetConfiguredSources());
        Settings.SetList(AgentSkills.TargetAgentsListKey, ["claude-code", "codex"]);
        var installedOutput = new RecordingOutput();

        Assert.Equal(OperationVeredict.Success, await Perform(manager, package, OperationType.Install, installedOutput));

        var install = Assert.Single(_backend.Installs);
        Assert.Equal("https://github.com/contoso/skills", install.Source);
        Assert.Equal("review", install.Skill);
        Assert.Equal(["claude-code", "codex"], install.Agents);
        Assert.Contains("Installed review for claude-code", installedOutput.Info);
        Assert.Equal(["https://github.com/contoso/skills"], Settings.GetList<string>(AgentSkills.SourcesListKey));
        Assert.Equal("contoso/skills", manager.SourceFactory.GetSourceIfExists("contoso/skills")?.Name);
    }

    [Fact]
    public async Task RegistryInstall_SuccessRetainsSourceForUpdatesAndUninstallAfterDiscoveryOff()
    {
        var registry = new RecordingRegistry();
        var manager = CreateManager(registry);
        manager.SkillsShDiscoveryEnabled = true;
        Assert.Empty(manager.SourcesHelper.GetSources()); // Prime cache before persistence.
        var package = Assert.Single(manager.FindPackages("review"));
        Assert.Equal(OperationVeredict.Success, await Perform(manager, package, OperationType.Install, new RecordingOutput()));
        Assert.Null(Assert.Single(_backend.Installs).Agents); // Automatic targets stay automatic.
        Assert.Equal("contoso/skills", Assert.Single(manager.SourcesHelper.GetSources()).Name);
        _backend.SourceSkills["https://github.com/contoso/skills"] = [new AvailableSkill("review", "Review", null)];
        _backend.Installed.Add(FakeSkillsBackend.TrackedSkill("review", "contoso/skills", "1111111aaaa"));
        var expectedUpdate = new SkillUpdate
        {
            Name = "review",
            Scope = SkillScope.Global,
            Source = "contoso/skills",
            CurrentHash = "1111111aaaa",
            LatestHash = "2222222bbbb",
        };
        _backend.UpdateCheck = new([expectedUpdate], []);

        manager.SkillsShDiscoveryEnabled = false;

        Assert.Equal(["https://github.com/contoso/skills"], Settings.GetList<string>(AgentSkills.SourcesListKey));
        Assert.Equal("Git", Assert.Single(manager.Dependencies).Name);
        Assert.Equal("review", Assert.Single(manager.FindPackages("review")).Id);
        Assert.Equal(["review"], registry.Queries);
        var update = Assert.Single(manager.GetAvailableUpdates());
        Assert.Equal("review", update.Id);
        Assert.Equal("1111111", update.VersionString);
        Assert.Equal("2222222", update.NewVersionString);
        Assert.Equal(["review"], Assert.Single(_backend.CheckedSkills));
        Assert.Equal(OperationVeredict.Success, await Perform(manager, update, OperationType.Update, new RecordingOutput()));
        Assert.Same(expectedUpdate, Assert.Single(Assert.Single(_backend.AppliedUpdates)));
        Assert.Equal(OperationVeredict.Success, await Perform(manager, package, OperationType.Uninstall, new RecordingOutput()));
        Assert.Equal(["review"], _backend.Removed);
    }

    [Theory]
    [InlineData(SkillOperationStatus.Failed, false)]
    [InlineData(SkillOperationStatus.NotFound, false)]
    [InlineData(SkillOperationStatus.Succeeded, true)]
    public async Task RegistryInstall_UnsuccessfulOrMismatchedOutcomeNeverSavesSource(SkillOperationStatus status, bool mismatch)
    {
        _backend.InstallStatus = status;
        _backend.InstallOutcomeName = mismatch ? "sibling" : null;
        var manager = CreateManager(new RecordingRegistry());
        manager.SkillsShDiscoveryEnabled = true;
        var package = Assert.Single(manager.FindPackages("review"));
        var output = new RecordingOutput();

        Assert.Equal(OperationVeredict.Failure, await Perform(manager, package, OperationType.Install, output));

        Assert.Equal("review", Assert.Single(_backend.Installs).Skill);
        Assert.Contains(mismatch ? "could not be installed" : "The install failed", output.FailureMessage);
        Assert.Empty(AgentSkills.GetConfiguredSources());
        Assert.Empty(manager.SourcesHelper.GetSources());
        Assert.Null(manager.SourceFactory.GetSourceIfExists("contoso/skills"));
        manager.SkillsShDiscoveryEnabled = false;
        Assert.Empty(manager.Dependencies);
    }

    [Fact]
    public async Task RegistryInstall_ConfiguredFolderUrlRemainsAuthoritativeWithoutBroadeningSavedSource()
    {
        const string folder = "https://github.com/contoso/skills/tree/main/curated";
        AddSourceSetting(folder);
        _backend.SourceSkills[folder] = [new AvailableSkill("review", "Curated review", null)];
        var manager = CreateManager(new RecordingRegistry());
        manager.SkillsShDiscoveryEnabled = true;
        var package = Assert.Single(manager.FindPackages("review"));

        Assert.Equal(folder, package.Source.Url.AbsoluteUri);
        Assert.Equal(folder, manager.GetInstallSource(package)?.InstallSource);
        Assert.Equal(OperationVeredict.Success, await Perform(manager, package, OperationType.Install, new RecordingOutput()));
        Assert.Equal(folder, Assert.Single(_backend.Installs).Source);
        Assert.Equal([folder], Settings.GetList<string>(AgentSkills.SourcesListKey));
        manager.SkillsShDiscoveryEnabled = false;
        Assert.Equal(folder, manager.GetInstallSource(package)?.InstallSource);
        Assert.Equal(folder, Assert.Single(manager.GetBrowsableSources()).Url.AbsoluteUri);
    }

    [Fact]
    public async Task RegistryInstall_WebsiteRetainsHttpsSourceAndActualSelectorWithoutRequiringGitWhenOff()
    {
        const string website = "https://uizze.sh";
        const string selector = "react:components";
        var registry = new RecordingRegistry
        {
            Results = [new SkillsShResult(selector, SkillSourceLocator.Parse(website)!)],
        };
        var manager = CreateManager(registry);
        manager.SkillsShDiscoveryEnabled = true;
        Settings.SetList(AgentSkills.TargetAgentsListKey, ["codex"]);
        var package = Assert.Single(manager.FindPackages("UI"));
        Assert.Equal(selector, package.Id);
        Assert.Equal("uizze.sh", package.Source.Name);
        Assert.Equal("https://uizze.sh/", package.Source.Url.AbsoluteUri);
        Assert.Null(manager.SourceFactory.GetSourceIfExists("uizze.sh"));
        var slugInsteadOfSelector = new Package("components", "reactcomponents", "latest", package.Source, manager);
        Assert.Equal(OperationVeredict.Failure,
            await Perform(manager, slugInsteadOfSelector, OperationType.Install, new RecordingOutput()));
        Assert.Empty(_backend.Installs);

        Assert.Equal(OperationVeredict.Success, await Perform(manager, package, OperationType.Install, new RecordingOutput()));

        var install = Assert.Single(_backend.Installs);
        Assert.Equal(website, install.Source);
        Assert.Equal(selector, install.Skill);
        Assert.Equal(["codex"], install.Agents);
        Assert.Equal([website], Settings.GetList<string>(AgentSkills.SourcesListKey));
        _backend.SourceSkills[website] = [new AvailableSkill(selector, "UI widgets", null)];
        _backend.Installed.Add(WellKnownSkill(selector, "uizze.sh", "https://uizze.sh/components/SKILL.md", website));
        _backend.UpdateCheck = new(
            [new SkillUpdate
            {
                Name = selector, Scope = SkillScope.Global, Source = website,
                CurrentHash = "1111111aaaa", LatestHash = "2222222bbbb",
            }], []);

        manager.SkillsShDiscoveryEnabled = false;

        Assert.Empty(manager.Dependencies);
        Assert.Equal(website, manager.GetInstallSource(package)?.InstallSource);
        Assert.Equal("uizze.sh", Assert.Single(manager.GetBrowsableSources()).Name);
        Assert.Equal(selector, Assert.Single(manager.FindPackages("UI")).Id);
        var update = Assert.Single(manager.GetAvailableUpdates());
        Assert.Equal(selector, update.Id);
        Assert.Equal("2222222", update.NewVersionString);
        Assert.Equal(OperationVeredict.Success, await Perform(manager, update, OperationType.Update, new RecordingOutput()));
        Assert.Equal(website, Assert.Single(Assert.Single(_backend.AppliedUpdates)).Source);
        Assert.Equal(["UI"], registry.Queries);
    }

    [Fact]
    public async Task RegistryInstall_RemovingRetainedSourceRevokesOldAdmissionAndGitDependencyWhenOff()
    {
        var manager = CreateManager(new RecordingRegistry());
        manager.SkillsShDiscoveryEnabled = true;
        var package = Assert.Single(manager.FindPackages("review"));
        Assert.Equal(OperationVeredict.Success, await Perform(manager, package, OperationType.Install, new RecordingOutput()));

        Assert.Equal(OperationVeredict.Success, await ((IInProcessSourceHelper)manager.SourcesHelper)
            .RemoveSourceAsync(package.Source, new RecordingOutput(), CancellationToken.None));

        Assert.True(manager.SkillsShDiscoveryEnabled); // Revocation must not rely on turning discovery off.
        Assert.Null(manager.GetInstallSource(package));
        Assert.Null(manager.SourceFactory.GetSourceIfExists("contoso/skills"));
        Assert.Empty(manager.SourcesHelper.GetSources());
        Assert.Equal(OperationVeredict.Failure, await Perform(manager, package, OperationType.Install, new RecordingOutput()));
        Assert.Single(_backend.Installs);
        Assert.Empty(AgentSkills.GetConfiguredSources());
        Assert.Equal("Git", Assert.Single(manager.Dependencies).Name);
        manager.SkillsShDiscoveryEnabled = false;
        Assert.Empty(manager.Dependencies);
    }

    [Fact]
    public void RegistryDiscovery_GitDependencyTracksOptInWithoutRetainedRepository()
    {
        var manager = CreateManager(new RecordingRegistry());
        Assert.Empty(manager.Dependencies);
        manager.SkillsShDiscoveryEnabled = true;
        Assert.Equal("Git", Assert.Single(manager.Dependencies).Name);
        manager.SkillsShDiscoveryEnabled = false;
        Assert.Empty(manager.Dependencies);
    }

    [Fact]
    public void RegistryPreference_FailedSaveRollsBackCachedOptInAndCanRetry()
    {
        var registry = new RecordingRegistry();
        var manager = CreateManager(registry);
        string settingFile = Path.Combine(CoreData.UniGetUIUserConfigurationDirectory,
            Settings.ResolveKey(Settings.K.SkillsShDiscoveryEnabled));
        Directory.CreateDirectory(settingFile); // A directory cannot be replaced with the bool marker file.

        var error = Assert.Throws<IOException>(() => manager.SkillsShDiscoveryEnabled = true);

        Assert.Equal("The skills.sh discovery preference could not be saved. Check your settings folder permissions and try again.",
            error.Message);
        Assert.False(File.Exists(settingFile));
        Assert.False(manager.SkillsShDiscoveryEnabled);
        Assert.False(Settings.Get(Settings.K.SkillsShDiscoveryEnabled));
        Assert.Empty(manager.Dependencies);
        Assert.Empty(manager.FindPackages("review"));
        Assert.Empty(registry.Queries);

        Directory.Delete(settingFile); // Only this test's sandbox obstruction.
        manager.SkillsShDiscoveryEnabled = true;

        Assert.True(File.Exists(settingFile));
        Assert.True(manager.SkillsShDiscoveryEnabled);
        Assert.Equal("Git", Assert.Single(manager.Dependencies).Name);
        Assert.Equal("review", Assert.Single(manager.FindPackages("review")).Id);
        Assert.Equal(["review"], registry.Queries);
    }

    [Fact]
    public void RegistryDetails_ShowActualSourceAndReviewWarningWithoutInstallingOrConfiguring()
    {
        var manager = CreateManager(new RecordingRegistry());
        manager.SkillsShDiscoveryEnabled = true;
        var package = Assert.Single(manager.FindPackages("review"));
        var details = new PackageDetails(package);

        manager.DetailsHelper.GetDetails(details);

        Assert.Equal("https://github.com/contoso/skills", details.HomepageUrl?.AbsoluteUri);
        Assert.Equal("https://github.com/contoso/skills", details.InstallerUrl?.AbsoluteUri);
        Assert.Equal("contoso", details.Publisher);
        Assert.Equal("Discovered on skills.sh. Installs from the repository or website shown as its source. Review the skill before installing it.",
            details.Description);
        Assert.Empty(_backend.Installs);
        Assert.Empty(AgentSkills.GetConfiguredSources());
    }
}
