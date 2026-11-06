using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UniGetUI.Extensions.AgentSkills;

namespace UniGetUI.Extensions.AgentSkills.Tests;

public sealed class CursorPluginsServiceTests
{
    [Fact]
    public async Task AddAndInstall_ClonesHttpsSourceAndCopiesTheCompleteBundleIntoManagedDirectory()
    {
        using var fixture = new CursorFixture();
        using var cancel = new CancellationTokenSource();
        const string source = "https://github.com/owner/repo.git";
        fixture.Git.Repositories[source] = BundleRepository("design-market", "copilot", "1.2", "./plugins/copilot");
        string unrelated = Path.Combine(fixture.CursorPlugins, "local", "unrelated");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "plugin.json"), PortableManifest("unrelated", "1"));
        File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "not owned by UniGetUI");
        var service = fixture.Service;

        Assert.Equal("Cursor", service.Name);
        Assert.True(service.SupportsUpdate);
        Assert.False(service.SupportsToggle);
        Assert.Contains("Cursor local imports", service.Guidance);
        Assert.Contains("other local plugins", service.RemovalWarning);
        Assert.Contains("Restart Cursor", service.RestartNotice);
        string added = await service.AddMarketplaceAsync("/plugin marketplace add owner/repo", cancel.Token);

        Assert.Contains("added", added, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(["clone", "--depth", "1", "--", source], fixture.Git.Calls.Single().Take(5));
        Assert.Equal(cancel.Token, fixture.Git.Tokens.Single());
        AgentPluginCatalog available = await service.GetCatalogAsync();
        AgentPluginItem plugin = Assert.Single(available.Plugins);
        Assert.Equal("copilot@design-market", plugin.Id);
        Assert.Equal("Fixture description", plugin.Description);
        Assert.Equal("1.2", plugin.Version);
        Assert.False(plugin.IsInstalled);
        Assert.True(plugin.CanInstall);

        await service.PerformAsync("/plugin install copilot@design-market", AgentPluginAction.Install, cancel.Token);

        string imported = fixture.ManagedPath("copilot@design-market");
        AgentPluginItem installed = Assert.Single((await service.GetCatalogAsync()).Plugins);
        Assert.Equal("1.2", installed.Version);
        Assert.True(installed.IsInstalled);
        Assert.Equal("skill payload", File.ReadAllText(Path.Combine(imported, "cursor-skills", "custom", "SKILL.md")));
        Assert.Equal("command payload", File.ReadAllText(Path.Combine(imported, "commands", "deploy.md")));
        Assert.Equal("script payload", File.ReadAllText(Path.Combine(imported, "scripts", "deploy.ps1")));
        Assert.True(File.Exists(Path.Combine(imported, ".cursor-plugin", "plugin.json")));
        Assert.False(Directory.Exists(Path.Combine(imported, ".git")));
        Assert.Equal("not owned by UniGetUI", File.ReadAllText(Path.Combine(unrelated, "keep.txt")));
        Assert.Equal(cancel.Token, fixture.Git.Tokens.Single());
    }

    [Theory]
    [InlineData(".agents/plugins/marketplace.json")]
    [InlineData(".claude-plugin/marketplace.json")]
    public async Task AddMarketplace_AcceptsPortableLocalSourceShapeAndFallbackMarketplaceManifests(string manifestPath)
    {
        using var fixture = new CursorFixture();
        const string source = "https://example.test/portable.git";
        fixture.Git.Repositories[source] = root =>
        {
            Directory.CreateDirectory(Path.Combine(root, Path.GetDirectoryName(manifestPath)!));
            string manifest = JsonSerializer.Serialize(new
            {
                name = "portable-market",
                plugins = new[] { new { name = "portable", source = new { source = "local", path = "./plugins/portable" } } },
            });
            File.WriteAllText(Path.Combine(root, manifestPath), manifest);
            WriteBundle(root, "./plugins/portable", "portable", "4.0", "Portable description");
        };

        await fixture.Service.AddMarketplaceAsync(source);
        AgentPluginItem plugin = Assert.Single((await fixture.Service.GetCatalogAsync()).Plugins);

        Assert.Equal("portable@portable-market", plugin.Id);
        Assert.Equal("4.0", plugin.Version);
        Assert.Equal("Portable description", plugin.Description);
        Assert.True(plugin.CanInstall);
    }

    [Fact]
    public async Task AddMarketplace_PrefersCursorMarketplaceManifestAndAcceptsPortableRootPluginManifest()
    {
        using var fixture = new CursorFixture();
        const string source = "https://example.test/manifest-priority.git";
        fixture.Git.Repositories[source] = root =>
        {
            WriteMarketplace(root, ".cursor-plugin/marketplace.json", "cursor-first",
                [new("portable", "Cursor source", "./plugins/portable")]);
            WriteMarketplace(root, ".agents/plugins/marketplace.json", "fallback-second",
                [new("ignored", "Lower priority", "./plugins/ignored")]);
            string bundle = Path.Combine(root, "plugins", "portable");
            Directory.CreateDirectory(bundle);
            File.WriteAllText(Path.Combine(bundle, "plugin.json"), PortableManifest("portable", "5"));
            File.WriteAllText(Path.Combine(bundle, "portable-skill.md"), "portable bundle content");
        };

        await fixture.Service.AddMarketplaceAsync(source);
        AgentPluginItem plugin = Assert.Single((await fixture.Service.GetCatalogAsync()).Plugins);

        Assert.Equal("portable@cursor-first", plugin.Id);
        Assert.Equal("5", plugin.Version);
        Assert.True(plugin.CanInstall);
        await fixture.Service.PerformAsync(plugin.Id, AgentPluginAction.Install);
        Assert.Equal("portable bundle content",
            File.ReadAllText(Path.Combine(fixture.ManagedPath(plugin.Id), "portable-skill.md")));
        Assert.Contains("\"$schema\":\"https://agent-plugins.org/schemas/1.0.0/plugin.schema.json\"",
            File.ReadAllText(Path.Combine(fixture.ManagedPath(plugin.Id), "plugin.json")));
    }

    [Fact]
    public async Task Catalog_ExposesRemoteAndNonCursorBundlesWithClearNonInstallableNotices()
    {
        using var fixture = new CursorFixture();
        const string source = "https://example.test/mixed.git";
        fixture.Git.Repositories[source] = root =>
        {
            WriteMarketplace(root, ".cursor-plugin/marketplace.json", "mixed-market",
            [
                new("remote", "Remote entry", new { source = "github", repo = "owner/remote" }),
                new("portable-only", "No Cursor metadata", "./plugins/no-cursor-plugin"),
            ]);
            Directory.CreateDirectory(Path.Combine(root, "plugins", "no-cursor-plugin"));
            File.WriteAllText(Path.Combine(root, "plugins", "no-cursor-plugin", "README.md"), "not a plugin manifest");
        };
        await fixture.Service.AddMarketplaceAsync(source);

        AgentPluginItem[] plugins = (await fixture.Service.GetCatalogAsync()).Plugins.ToArray();

        Assert.Equal(["portable-only@mixed-market", "remote@mixed-market"], plugins.Select(item => item.Id));
        Assert.All(plugins, item => Assert.False(item.CanInstall));
        Assert.Contains("no Cursor", plugins[0].Notice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Remote plugin sources", plugins[1].Notice);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.PerformAsync("remote@mixed-market", AgentPluginAction.Install));
        Assert.False(Directory.Exists(fixture.ManagedPath("remote@mixed-market")));
    }

    [Fact]
    public async Task RefreshMarketplace_UpdatesOnlySourceSnapshotUntilPluginUpdateIsExplicit()
    {
        using var fixture = new CursorFixture();
        const string source = "https://example.test/updates.git";
        fixture.Git.Repositories[source] = BundleRepository("updates", "plugin", "1", "./plugins/plugin");
        await fixture.Service.AddMarketplaceAsync(source);
        await fixture.Service.PerformAsync("plugin@updates", AgentPluginAction.Install);
        string imported = fixture.ManagedPath("plugin@updates");

        fixture.Git.Repositories[source] = BundleRepository("updates", "plugin", "2", "./plugins/plugin", "new skill payload");
        string refresh = await fixture.Service.UpdateMarketplaceAsync("updates");
        AgentPluginItem afterRefresh = Assert.Single((await fixture.Service.GetCatalogAsync()).Plugins);

        Assert.Contains("explicitly", refresh);
        Assert.Equal("1", afterRefresh.Version);
        Assert.Equal("skill payload", File.ReadAllText(Path.Combine(imported, "cursor-skills", "custom", "SKILL.md")));

        await fixture.Service.PerformAsync("plugin@updates", AgentPluginAction.Update);

        AgentPluginItem afterUpdate = Assert.Single((await fixture.Service.GetCatalogAsync()).Plugins);
        Assert.Equal("2", afterUpdate.Version);
        Assert.Equal("new skill payload", File.ReadAllText(Path.Combine(imported, "cursor-skills", "custom", "SKILL.md")));
    }

    [Fact]
    public async Task MissingManagedFiles_RemainVisibleAndCanBeRestoredOrUninstalledToUnblockSourceRemoval()
    {
        using var fixture = new CursorFixture();
        const string source = "https://example.test/missing-files.git";
        const string id = "plugin@missing-files";
        fixture.Git.Repositories[source] = BundleRepository("missing-files", "plugin", "1", "./plugins/plugin");
        await fixture.Service.AddMarketplaceAsync(source);
        await fixture.Service.PerformAsync(id, AgentPluginAction.Install);
        string managedPath = fixture.ManagedPath(id);

        Directory.Delete(managedPath, recursive: true);
        AgentPluginItem missing = Assert.Single((await fixture.Service.GetCatalogAsync()).Plugins);

        Assert.True(missing.IsInstalled);
        Assert.Equal("Managed plugin files missing", missing.Status);
        Assert.Contains("Update to restore", missing.Notice);
        Assert.False(missing.CanInstall);
        await fixture.Service.PerformAsync(id, AgentPluginAction.Update);
        Assert.True(Directory.Exists(managedPath));
        Assert.Equal("skill payload", File.ReadAllText(Path.Combine(managedPath, "cursor-skills", "custom", "SKILL.md")));
        AgentPluginItem restored = Assert.Single((await fixture.Service.GetCatalogAsync()).Plugins);
        Assert.True(restored.IsInstalled);
        Assert.Equal("Imported locally; reload Cursor to activate", restored.Status);

        Directory.Delete(managedPath, recursive: true);
        Assert.True(Assert.Single((await fixture.Service.GetCatalogAsync()).Plugins).IsInstalled);
        await fixture.Service.PerformAsync(id, AgentPluginAction.Uninstall);
        Assert.False(Directory.Exists(managedPath));
        await fixture.Service.RemoveMarketplaceAsync("missing-files");
        Assert.Empty((await fixture.Service.GetCatalogAsync()).Marketplaces);
    }

    public enum InvalidRefresh { ChangedMarketplaceName, RemovedInstalledPlugin }

    [Theory]
    [InlineData(InvalidRefresh.ChangedMarketplaceName)]
    [InlineData(InvalidRefresh.RemovedInstalledPlugin)]
    public async Task RefreshMarketplace_RejectsIncompatibleSnapshotAndPreservesCurrentSourceAndInstall(InvalidRefresh kind)
    {
        using var fixture = new CursorFixture();
        const string source = "https://example.test/refresh-validation.git";
        fixture.Git.Repositories[source] = BundleRepository("original", "plugin", "1", "./plugins/plugin");
        await fixture.Service.AddMarketplaceAsync(source);
        await fixture.Service.PerformAsync("plugin@original", AgentPluginAction.Install);
        string installedPath = fixture.ManagedPath("plugin@original");
        fixture.Git.Repositories[source] = kind == InvalidRefresh.ChangedMarketplaceName
            ? BundleRepository("renamed", "plugin", "2", "./plugins/plugin")
            : root => WriteMarketplace(root, ".cursor-plugin/marketplace.json", "original", []);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.UpdateMarketplaceAsync("original"));

        Assert.Equal(new AgentPluginMarketplace("original", source),
            Assert.Single((await fixture.Service.GetCatalogAsync()).Marketplaces));
        AgentPluginItem installed = Assert.Single((await fixture.Service.GetCatalogAsync()).Plugins);
        Assert.Equal("plugin@original", installed.Id);
        Assert.Equal("1", installed.Version);
        Assert.Equal("skill payload", File.ReadAllText(Path.Combine(installedPath, "cursor-skills", "custom", "SKILL.md")));
    }

    [Fact]
    public async Task Uninstall_RemovesOnlyManagedBundleAndAllowsSourceRemovalEvenWhenSnapshotIsMissing()
    {
        using var fixture = new CursorFixture();
        const string source = "https://example.test/removal.git";
        fixture.Git.Repositories[source] = BundleRepository("removal", "plugin", "1", "./plugins/plugin");
        await fixture.Service.AddMarketplaceAsync(source);
        await fixture.Service.PerformAsync("plugin@removal", AgentPluginAction.Install);
        string sourcePath = Directory.EnumerateDirectories(Path.Combine(fixture.Store, "sources")).Single();
        string imported = fixture.ManagedPath("plugin@removal");
        string unrelated = Path.Combine(fixture.CursorPlugins, "local", "unrelated-plugin");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "plugin.json"), PortableManifest("unrelated", "1"));
        File.WriteAllText(Path.Combine(unrelated, "preserve.txt"), "keep");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.RemoveMarketplaceAsync("removal"));
        Directory.Delete(sourcePath, recursive: true);
        await fixture.Service.PerformAsync("plugin@removal", AgentPluginAction.Uninstall);

        Assert.False(Directory.Exists(imported));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(unrelated, "preserve.txt")));
        await fixture.Service.RemoveMarketplaceAsync("removal");
        Assert.Empty((await fixture.Service.GetCatalogAsync()).Marketplaces);
    }

    [Fact]
    public async Task FailedRefresh_PreservesTheLastGoodSourceAndInstalledBundle()
    {
        using var fixture = new CursorFixture();
        const string source = "https://example.test/stable.git";
        fixture.Git.Repositories[source] = BundleRepository("stable", "plugin", "1", "./plugins/plugin");
        await fixture.Service.AddMarketplaceAsync(source);
        await fixture.Service.PerformAsync("plugin@stable", AgentPluginAction.Install);
        string imported = fixture.ManagedPath("plugin@stable");
        fixture.Git.Repositories[source] = _ => throw new InvalidOperationException("fixture clone failure");

        InvalidOperationException failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.UpdateMarketplaceAsync("stable"));

        Assert.Equal("fixture clone failure", failure.Message);
        AgentPluginCatalog catalog = await fixture.Service.GetCatalogAsync();
        Assert.Equal(new AgentPluginMarketplace("stable", source), Assert.Single(catalog.Marketplaces));
        Assert.Equal("1", Assert.Single(catalog.Plugins).Version);
        Assert.Equal("skill payload", File.ReadAllText(Path.Combine(imported, "cursor-skills", "custom", "SKILL.md")));
    }

    public enum InvalidSource
    {
        Http, Credentials, Query, Fragment, Option, WrongSegments, Traversal, UnsupportedCursorCommand,
    }

    [Theory]
    [InlineData(InvalidSource.Http)]
    [InlineData(InvalidSource.Credentials)]
    [InlineData(InvalidSource.Query)]
    [InlineData(InvalidSource.Fragment)]
    [InlineData(InvalidSource.Option)]
    [InlineData(InvalidSource.WrongSegments)]
    [InlineData(InvalidSource.Traversal)]
    [InlineData(InvalidSource.UnsupportedCursorCommand)]
    public async Task AddMarketplace_RejectsUnsafeSourceBeforeGit(InvalidSource kind)
    {
        string input = kind switch
        {
            InvalidSource.Http => "http://example.test/repo.git",
            InvalidSource.Credentials => "https://user@example.test/repo.git",
            InvalidSource.Query => "https://example.test/repo.git?token=secret",
            InvalidSource.Fragment => "https://example.test/repo.git#fragment",
            InvalidSource.Option => "--upload-pack=bad",
            InvalidSource.WrongSegments => "owner/repo/extra",
            InvalidSource.Traversal => "owner/../repo",
            _ => "cursor plugin marketplace add owner/repo",
        };
        using var fixture = new CursorFixture();

        await Assert.ThrowsAsync<ArgumentException>(() => fixture.Service.AddMarketplaceAsync(input));

        Assert.Empty(fixture.Git.Calls);
        Assert.Empty((await fixture.Service.GetCatalogAsync()).Marketplaces);
    }

    public enum InvalidManifest
    {
        Traversal, DuplicateNames, NameMismatch, UnsupportedPortableSchema,
    }

    [Theory]
    [InlineData(InvalidManifest.Traversal)]
    [InlineData(InvalidManifest.DuplicateNames)]
    [InlineData(InvalidManifest.NameMismatch)]
    [InlineData(InvalidManifest.UnsupportedPortableSchema)]
    public async Task AddMarketplace_InvalidBundleMetadataFailsAndLeavesNoConfiguredSource(InvalidManifest kind)
    {
        using var fixture = new CursorFixture();
        const string source = "https://example.test/invalid.git";
        fixture.Git.Repositories[source] = root =>
        {
            switch (kind)
            {
                case InvalidManifest.Traversal:
                    WriteMarketplace(root, ".cursor-plugin/marketplace.json", "invalid",
                        [new("plugin", "Description", "../outside")]);
                    break;
                case InvalidManifest.DuplicateNames:
                    WriteMarketplace(root, ".cursor-plugin/marketplace.json", "invalid",
                        [new("Plugin", "First", "./one"), new("plugin", "Second", "./two")]);
                    break;
                case InvalidManifest.NameMismatch:
                    WriteMarketplace(root, ".cursor-plugin/marketplace.json", "invalid",
                        [new("listed", "Description", "./plugin")]);
                    WriteBundle(root, "./plugin", "different", "1");
                    break;
                default:
                    WriteMarketplace(root, ".cursor-plugin/marketplace.json", "invalid",
                        [new("portable", "Description", "./plugin")]);
                    Directory.CreateDirectory(Path.Combine(root, "plugin"));
                    File.WriteAllText(Path.Combine(root, "plugin", "plugin.json"),
                        JsonSerializer.Serialize(new { schema = "https://wrong.test/schema.json", name = "portable" }));
                    break;
            }
        };

        if (kind == InvalidManifest.Traversal)
            await Assert.ThrowsAsync<IOException>(() => fixture.Service.AddMarketplaceAsync(source));
        else
            await Assert.ThrowsAsync<JsonException>(() => fixture.Service.AddMarketplaceAsync(source));

        Assert.Empty((await fixture.Service.GetCatalogAsync()).Marketplaces);
        Assert.Empty(Directory.Exists(Path.Combine(fixture.Store, "sources"))
            ? Directory.EnumerateFileSystemEntries(Path.Combine(fixture.Store, "sources"))
            : []);
    }

    [Fact]
    public async Task AddMarketplace_DuplicateCanonicalSourceAndMarketplaceNameAreRejected()
    {
        using var fixture = new CursorFixture();
        fixture.Git.Repositories["https://github.com/owner/first.git"] =
            BundleRepository("same-name", "first", "1", "./plugins/first");
        fixture.Git.Repositories["https://github.com/owner/second.git"] =
            BundleRepository("same-name", "second", "1", "./plugins/second");
        await fixture.Service.AddMarketplaceAsync("owner/first");

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.AddMarketplaceAsync(
            "https://github.com/owner/first.git"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.AddMarketplaceAsync("owner/second"));

        Assert.Equal(2, fixture.Git.Calls.Count);
        Assert.Equal("same-name", Assert.Single((await fixture.Service.GetCatalogAsync()).Marketplaces).Name);
    }

    [Theory]
    [InlineData(AgentPluginAction.Enable)]
    [InlineData(AgentPluginAction.Disable)]
    public async Task Perform_ToggleActionsAreUnsupportedAndNonManagedMutationsAreRejected(AgentPluginAction action)
    {
        using var fixture = new CursorFixture();

        await Assert.ThrowsAsync<NotSupportedException>(() =>
            fixture.Service.PerformAsync("plugin@market", action));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.PerformAsync("plugin@market", AgentPluginAction.Update));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.PerformAsync("plugin@market", AgentPluginAction.Uninstall));

        Assert.Empty(fixture.Git.Calls);
        Assert.False(Directory.Exists(fixture.ManagedPath("plugin@market")));
    }

    [Fact]
    public async Task LocalNameCollision_DisablesImportAndNeverOverwritesExistingCursorPlugin()
    {
        using var fixture = new CursorFixture();
        string collision = Path.Combine(fixture.CursorPlugins, "local", "existing");
        Directory.CreateDirectory(collision);
        File.WriteAllText(Path.Combine(collision, "plugin.json"), PortableManifest("same", "local"));
        File.WriteAllText(Path.Combine(collision, "local.txt"), "preserve");
        const string source = "https://example.test/collision.git";
        fixture.Git.Repositories[source] = BundleRepository("collision", "same", "1", "./plugins/same");
        await fixture.Service.AddMarketplaceAsync(source);

        AgentPluginItem item = Assert.Single((await fixture.Service.GetCatalogAsync()).Plugins);

        Assert.False(item.CanInstall);
        Assert.Contains("already exists", item.Notice);
        await Assert.ThrowsAsync<IOException>(() => fixture.Service.PerformAsync(item.Id, AgentPluginAction.Install));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(collision, "local.txt")));
        Assert.False(Directory.Exists(fixture.ManagedPath(item.Id)));
    }

    [Fact]
    public async Task AddMarketplace_CloneFailureAndCancellationArePropagatedAndDoNotPersistState()
    {
        using var fixture = new CursorFixture();
        const string source = "https://example.test/cancel.git";
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Git.Override = async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return "unreachable";
        };
        using var cancel = new CancellationTokenSource();
        Task<string> pending = fixture.Service.AddMarketplaceAsync(source, cancel.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Equal(cancel.Token, Assert.Single(fixture.Git.Tokens));
        Assert.Empty((await fixture.Service.GetCatalogAsync()).Marketplaces);
    }

    private static Action<string> BundleRepository(
        string marketplace,
        string plugin,
        string version,
        string sourcePath,
        string skillContent = "skill payload") => root =>
    {
        WriteMarketplace(root, ".cursor-plugin/marketplace.json", marketplace,
            [new(plugin, "Fixture description", sourcePath)]);
        WriteBundle(root, sourcePath, plugin, version, "Fixture description", skillContent);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, ".git", "config"), "local git metadata");
    };

    private static void WriteMarketplace(string root, string manifestPath, string name, PluginEntry[] plugins)
    {
        string path = Path.Combine(root, manifestPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            name,
            plugins = plugins.Select(plugin => new
            {
                name = plugin.Name,
                description = plugin.Description,
                source = plugin.Source,
            }),
        }));
    }

    private static void WriteBundle(
        string root,
        string relativePath,
        string name,
        string version,
        string description = "Fixture description",
        string skillContent = "skill payload")
    {
        string bundle = Path.Combine(root, relativePath.Replace("./", "", StringComparison.Ordinal)
            .Replace('/', Path.DirectorySeparatorChar));
        string metadata = Path.Combine(bundle, ".cursor-plugin");
        Directory.CreateDirectory(metadata);
        File.WriteAllText(Path.Combine(metadata, "plugin.json"), JsonSerializer.Serialize(new { name, version, description }));
        string skill = Path.Combine(bundle, "cursor-skills", "custom");
        string commands = Path.Combine(bundle, "commands");
        string scripts = Path.Combine(bundle, "scripts");
        Directory.CreateDirectory(skill);
        Directory.CreateDirectory(commands);
        Directory.CreateDirectory(scripts);
        File.WriteAllText(Path.Combine(skill, "SKILL.md"), skillContent);
        File.WriteAllText(Path.Combine(commands, "deploy.md"), "command payload");
        File.WriteAllText(Path.Combine(scripts, "deploy.ps1"), "script payload");
    }

    private static string PortableManifest(string name, string version) =>
        JsonSerializer.Serialize(new
        {
            schema = "https://agent-plugins.org/schemas/1.0.0/plugin.schema.json",
            name,
            version,
        }).Replace("\"schema\"", "\"$schema\"", StringComparison.Ordinal);

    private sealed record PluginEntry(string Name, string Description, object Source);

    private sealed class CursorFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "UniGetUI-CursorPlugins-" + Guid.NewGuid().ToString("N"));
        public string Store => Path.Combine(_root, "store");
        public string CursorPlugins => Path.Combine(_root, "cursor-plugins");
        public FakeGit Git { get; } = new();
        public CursorPluginService Service => new(Store, CursorPlugins, Git);

        public CursorFixture()
        {
            Directory.CreateDirectory(Store);
            Directory.CreateDirectory(CursorPlugins);
        }

        public string ManagedPath(string id) =>
            Path.Combine(CursorPlugins, "local", "unigetui-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id))));

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeGit : IAgentPluginRunner
    {
        public List<string[]> Calls { get; } = [];
        public List<CancellationToken> Tokens { get; } = [];
        public Dictionary<string, Action<string>> Repositories { get; } = new(StringComparer.Ordinal);
        public Func<IReadOnlyList<string>, CancellationToken, Task<string>>? Override { get; set; }

        public Task<string> RunAsync(IReadOnlyList<string> arguments, CancellationToken token)
        {
            Calls.Add(arguments.ToArray());
            Tokens.Add(token);
            if (Override is not null) return Override(arguments, token);
            token.ThrowIfCancellationRequested();
            if (arguments.Count != 6 || arguments[0] != "clone" || arguments[1] != "--depth"
                || arguments[2] != "1" || arguments[3] != "--")
                throw new Xunit.Sdk.XunitException("Cursor source must be cloned with the fixed noninteractive argument list.");
            string source = arguments[4];
            string destination = arguments[5];
            Directory.CreateDirectory(destination);
            Repositories[source](destination);
            return Task.FromResult("fixture Git clone");
        }
    }
}
