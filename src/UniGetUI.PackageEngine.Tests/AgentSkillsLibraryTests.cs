using System.Security.Cryptography;
using System.Text;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.PackageEngine.Classes.Manager;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Interfaces.ManagerProviders;
using UniGetUI.PackageEngine.Managers.SkillsManager;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;

namespace UniGetUI.PackageEngine.Tests;

/// <summary>
/// The Agent Skills manager over the bundled library, against a well-known
/// skills index served on localhost and a sandbox home folder: nothing touches the network or the
/// real agent folders.
/// </summary>
public sealed class AgentSkillsLibraryTests : IDisposable
{
    // Agent folders the library reads from the environment; cleared so installs stay in the sandbox
    private static readonly string[] AgentEnvironmentVariables =
    [
        "CLAUDE_CONFIG_DIR",
        "CODEX_HOME",
        "VIBE_HOME",
        "HERMES_HOME",
        "AUTOHAND_HOME",
        "GROK_HOME",
        "SARVAM_HOME",
        "XDG_CONFIG_HOME",
        "XDG_STATE_HOME",
        "GH_HOST",
    ];

    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        nameof(AgentSkillsLibraryTests),
        Guid.NewGuid().ToString("N")
    );

    private readonly Dictionary<string, string?> _savedEnvironment = [];
    private readonly string _home;
    private readonly TestHttpServer _server;
    private string _skillMarkdown = SkillMarkdown("Reviews code");

    public AgentSkillsLibraryTests()
    {
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        SecureSettings.TEST_SecureSettingsRootOverride = Path.Combine(_testRoot, "SecureSettings");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Settings.ResetSettings();

        foreach (string variable in AgentEnvironmentVariables)
        {
            _savedEnvironment[variable] = Environment.GetEnvironmentVariable(variable);
            Environment.SetEnvironmentVariable(variable, null);
        }

        // A detected agent (Claude Code) in the sandbox home
        _home = Path.Combine(_testRoot, "home");
        Directory.CreateDirectory(Path.Combine(_home, ".claude"));

        _server = new TestHttpServer(Serve);
    }

    public void Dispose()
    {
        _server.Dispose();
        foreach (var (variable, value) in _savedEnvironment)
            Environment.SetEnvironmentVariable(variable, value);

        Settings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        SecureSettings.TEST_SecureSettingsRootOverride = null;
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private static string SkillMarkdown(string description) =>
        $"---\nname: code-review\ndescription: {description}\n---\n\n# Code review\n\nReview the pending changes.\n";

    private (int, string, string) Serve(System.Net.HttpListenerRequest request)
    {
        string path = request.Url?.AbsolutePath ?? "";
        if (path == "/.well-known/agent-skills/index.json")
        {
            string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(_skillMarkdown))).ToLowerInvariant();
            string index = $$"""
                {
                  "$schema": "https://schemas.agentskills.io/discovery/0.2.0/schema.json",
                  "skills": [
                    {
                      "name": "code-review",
                      "description": "Reviews code",
                      "type": "skill-md",
                      "url": "/.well-known/agent-skills/code-review/SKILL.md",
                      "digest": "sha256:{{digest}}"
                    }
                  ]
                }
                """;
            return (200, index, "application/json");
        }

        if (path == "/.well-known/agent-skills/code-review/SKILL.md")
            return (200, _skillMarkdown, "text/markdown");

        return (404, "", "text/plain");
    }

    [Fact]
    public async Task SkillFromAWellKnownIndexIsInstalledUpdatedAndRemoved()
    {
        // Offline: the local index is the only source
        var manager = new AgentSkills(new AgentSkillsBackend(_home));
        manager.Initialize();
        Assert.True(manager.IsReady());
        var sources = (IInProcessSourceHelper)manager.SourcesHelper;
        var operations = (IInProcessPackageOperationHelper)manager.OperationHelper;

        // Add the index as a source: it is checked by listing its skills
        var index = new ManagerSource(manager, "Local index", _server.BaseUri);
        Assert.Equal(
            OperationVeredict.Success,
            await sources.AddSourceAsync(index, new Output(), CancellationToken.None)
        );

        var found = Assert.Single(manager.FindPackages("review"));
        Assert.Equal("code-review", found.Id);
        Assert.Equal($"127.0.0.1:{_server.BaseUri.Port}", found.Source.Name);

        // Install
        var output = new Output();
        Assert.Equal(OperationVeredict.Success, await Perform(operations, found, OperationType.Install, output));
        Assert.True(File.Exists(Path.Combine(_home, ".claude", "skills", "code-review", "SKILL.md")), string.Join('\n', output.Lines));

        var installed = Assert.Single(manager.GetInstalledPackages());
        Assert.True(installed.IsEquivalentTo(found));
        string installedVersion = installed.VersionString;
        Assert.Equal(7, installedVersion.Length);

        // Publishing a new SKILL.md changes the index digest, which is an update
        _skillMarkdown = SkillMarkdown("Reviews code, now with a checklist");
        var update = Assert.Single(manager.GetAvailableUpdates());
        Assert.Equal(installedVersion, update.VersionString);
        Assert.NotEqual(installedVersion, update.NewVersionString);

        Assert.Equal(OperationVeredict.Success, await Perform(operations, update, OperationType.Update, new Output()));
        Assert.Contains(
            "now with a checklist",
            File.ReadAllText(Path.Combine(_home, ".claude", "skills", "code-review", "SKILL.md"))
        );
        // The library does not report the new digest of a well-known skill before the update, so the
        // offered version reads "latest"; once updated, the installed version is the new digest
        Assert.NotEqual(installedVersion, Assert.Single(manager.GetInstalledPackages()).VersionString);

        // Uninstall
        Assert.Equal(OperationVeredict.Success, await Perform(operations, update, OperationType.Uninstall, new Output()));
        Assert.Empty(manager.GetInstalledPackages());
        Assert.False(Directory.Exists(Path.Combine(_home, ".claude", "skills", "code-review")));
    }

    private static Task<OperationVeredict> Perform(
        IInProcessPackageOperationHelper operations,
        IPackage package,
        OperationType operation,
        Output output
    ) => operations.PerformAsync(package, new InstallOptions(), operation, output, CancellationToken.None);

    private sealed class Output : IOperationOutput
    {
        public List<string> Lines { get; } = [];

        public void Info(string line) => Lines.Add(line);

        public void Error(string line) => Lines.Add("ERROR: " + line);

        public void Verbose(string line) => Lines.Add(line);

        public void SetFailureMessage(string message) => Lines.Add("FAILURE: " + message);
    }

    [Theory]
    [InlineData(Devolutions.AgentSkills.SkillScope.Global)]
    [InlineData(Devolutions.AgentSkills.SkillScope.Project)]
    public void InstallFailsWhenRequiredLockPathIsADirectory(Devolutions.AgentSkills.SkillScope scope)
    {
        var client = CreateLibraryManager();
        string lockPath = RequiredLockPath(scope);
        Directory.CreateDirectory(lockPath);

        var exception = Assert.Throws<Devolutions.AgentSkills.SkillsException>(() =>
        {
            client.Install(InstallRequest(_server.BaseUri.AbsoluteUri, scope));
        });

        Assert.True(
            exception.InnerException is IOException or UnauthorizedAccessException,
            $"Expected the underlying I/O or access failure, got {exception.InnerException?.GetType().FullName ?? "null"}"
        );
        AssertRequiredLockError(exception.Message, scope, lockPath);
    }

    [Theory]
    [InlineData(Devolutions.AgentSkills.SkillScope.Global)]
    [InlineData(Devolutions.AgentSkills.SkillScope.Project)]
    public void UpdateFailsWhenRequiredLockPathIsADirectory(Devolutions.AgentSkills.SkillScope scope)
    {
        var client = CreateLibraryManager();
        string originalDigest = WellKnownDigest(_skillMarkdown);
        var installation = client.Install(InstallRequest(_server.BaseUri.AbsoluteUri, scope));
        var installed = Assert.Single(installation.Skills);
        Assert.True(installation.Succeeded, installed.Error);
        Assert.Equal("code-review", installed.Name);
        Assert.Equal(scope, installed.Scope);
        Assert.Equal(originalDigest, installed.Hash);

        _skillMarkdown = SkillMarkdown("Reviews code, now with a required checklist");
        var check = client.CheckForUpdates(scope);
        Assert.Empty(check.Unchecked);
        var update = Assert.Single(check.Updates);
        Assert.Equal("code-review", update.Name);
        Assert.Equal(scope, update.Scope);
        Assert.Equal(_server.BaseUri.AbsoluteUri.TrimEnd('/'), update.Source.TrimEnd('/'));
        Assert.Equal(originalDigest, update.CurrentHash);
        // Well-known discovery reports a changed digest, but does not expose the latest
        // digest on its public token. Notion below does expose and assert both versions.
        Assert.Null(update.LatestHash);

        // Discovery must happen while the real tracking record is still readable.
        string lockPath = RequiredLockPath(scope);
        Assert.True(File.Exists(lockPath));
        File.Delete(lockPath);
        Directory.CreateDirectory(lockPath);

        var result = client.Update(check.Updates);
        var outcome = Assert.Single(result.Skills);
        Assert.Equal("code-review", outcome.Name);
        Assert.Equal(scope, outcome.Scope);
        Assert.Equal(Devolutions.AgentSkills.SkillOperationStatus.Failed, outcome.Status);
        AssertRequiredLockError(outcome.Error, scope, lockPath);
        Assert.False(result.Succeeded);
    }

    [Fact]
    public void ExplicitNotionPageRetainsSourceAcrossInstallAndUpdates()
    {
        AssertNotionLifecycle(
            $"https://www.notion.so/workspace/Code-Review-{NotionFixture.PageId}?view=skills",
            NotionFixture.PageSource,
            databaseSource: false
        );
    }

    [Fact]
    public void NotionDatabaseRetainsSourceAcrossInstallAndUpdates()
    {
        AssertNotionLifecycle(
            $"https://www.notion.so/workspace/Skills-{NotionFixture.DatabaseId}?view=skills",
            NotionFixture.DatabaseSource,
            databaseSource: true
        );
    }

    [Theory]
    [InlineData(OperationType.Install)]
    [InlineData(OperationType.Update)]
    public async Task ManagerReportsRequiredLockWriteFailure(OperationType operation)
    {
        var manager = new AgentSkills(new AgentSkillsBackend(_home));
        AgentSkills.TargetAgents = ["claude-code"];
        manager.Initialize();
        Assert.True(manager.IsReady());
        var sources = (IInProcessSourceHelper)manager.SourcesHelper;
        var operations = (IInProcessPackageOperationHelper)manager.OperationHelper;
        Assert.Equal(
            OperationVeredict.Success,
            await sources.AddSourceAsync(
                new ManagerSource(manager, "Local index", _server.BaseUri),
                new Output(),
                CancellationToken.None
            )
        );
        IPackage package = Assert.Single(manager.FindPackages("review"));
        Assert.Equal("code-review", package.Id);

        if (operation == OperationType.Update)
        {
            Assert.Equal(
                OperationVeredict.Success,
                await Perform(operations, package, OperationType.Install, new Output())
            );
            string oldVersion = Assert.Single(manager.GetInstalledPackages()).VersionString;
            _skillMarkdown = SkillMarkdown("Reviews code, with the next checklist");
            // Cache the real update before replacing its lock file. A fresh check afterwards
            // would see no tracking record and could incorrectly pass as an empty update.
            package = Assert.Single(manager.GetAvailableUpdates());
            Assert.Equal("code-review", package.Id);
            Assert.Equal(oldVersion, package.VersionString);
            Assert.NotEqual(oldVersion, package.NewVersionString);
        }

        string lockPath = RequiredLockPath(Devolutions.AgentSkills.SkillScope.Global);
        if (operation == OperationType.Update)
        {
            Assert.True(File.Exists(lockPath));
            File.Delete(lockPath);
        }
        Directory.CreateDirectory(lockPath);

        var output = new Output();
        Assert.Equal(OperationVeredict.Failure, await Perform(operations, package, operation, output));
        AssertRequiredLockError(
            Assert.Single(output.Lines.Where(line => line.StartsWith("ERROR: ", StringComparison.Ordinal))),
            Devolutions.AgentSkills.SkillScope.Global,
            lockPath
        );
        AssertRequiredLockError(
            Assert.Single(output.Lines.Where(line => line.StartsWith("FAILURE: ", StringComparison.Ordinal))),
            Devolutions.AgentSkills.SkillScope.Global,
            lockPath
        );
        Assert.DoesNotContain(output.Lines, line => line.StartsWith("Installed ", StringComparison.Ordinal));
    }

    private string ProjectRoot => Path.Combine(_testRoot, "project");

    private Devolutions.AgentSkills.SkillsManager CreateLibraryManager(NotionFixture? notion = null)
    {
        Directory.CreateDirectory(Path.Combine(ProjectRoot, ".claude"));
        return new Devolutions.AgentSkills.SkillsManager(new Devolutions.AgentSkills.SkillsManagerOptions
        {
            HomeDirectory = _home,
            ProjectDirectory = ProjectRoot,
            NotionRunner = notion is null ? null : notion.Run,
            NotionDownloader = notion is null ? null : notion.Download,
        });
    }

    private string RequiredLockPath(Devolutions.AgentSkills.SkillScope scope) =>
        scope == Devolutions.AgentSkills.SkillScope.Global
            ? Path.Combine(_home, ".agents", ".skill-lock.json")
            : Path.Combine(ProjectRoot, "skills-lock.json");

    private static Devolutions.AgentSkills.SkillInstallRequest InstallRequest(
        string source,
        Devolutions.AgentSkills.SkillScope scope
    ) => new()
    {
        Source = source,
        Skills = ["code-review"],
        Agents = ["claude-code"],
        Scope = scope,
        Mode = Devolutions.AgentSkills.SkillInstallMode.Copy,
    };

    private static string WellKnownDigest(string markdown) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(markdown))).ToLowerInvariant();

    private static void AssertRequiredLockError(
        string? error,
        Devolutions.AgentSkills.SkillScope scope,
        string lockPath
    )
    {
        Assert.NotNull(error);
        Assert.Contains("code-review", error, StringComparison.Ordinal);
        Assert.Contains(lockPath, error, StringComparison.Ordinal);
        Assert.Contains(scope.ToString(), error, StringComparison.OrdinalIgnoreCase);
    }

    private void AssertNotionLifecycle(string installSource, string expectedSource, bool databaseSource)
    {
        var notion = new NotionFixture(_testRoot, databaseSource);
        var client = CreateLibraryManager(notion);
        var installation = client.Install(InstallRequest(installSource, Devolutions.AgentSkills.SkillScope.Global));
        var installed = Assert.Single(installation.Skills);
        Assert.True(installation.Succeeded, installed.Error);
        Assert.Equal("code-review", installed.Name);
        Assert.Equal(Devolutions.AgentSkills.SkillOperationStatus.Succeeded, installed.Status);
        Assert.Equal(Devolutions.AgentSkills.SkillScope.Global, installed.Scope);
        Assert.Equal(NotionFixture.Version1, installed.Hash);
        Assert.Equal(new[] { "claude-code" }, installed.Agents);
        AssertNotionTracking(client, expectedSource, NotionFixture.Version1);

        notion.Version = NotionFixture.Version2;
        var check = client.CheckForUpdates(Devolutions.AgentSkills.SkillScope.Global);
        Assert.Empty(check.Unchecked);
        AssertNotionUpdate(Assert.Single(check.Updates), expectedSource, NotionFixture.Version1, NotionFixture.Version2);
        var result = client.Update(check.Updates);
        var outcome = Assert.Single(result.Skills);
        Assert.Equal("code-review", outcome.Name);
        Assert.Equal(Devolutions.AgentSkills.SkillScope.Global, outcome.Scope);
        Assert.Equal(Devolutions.AgentSkills.SkillOperationStatus.Succeeded, outcome.Status);
        Assert.Null(outcome.Error);
        Assert.True(result.Succeeded);
        AssertNotionTracking(client, expectedSource, NotionFixture.Version2);

        notion.Version = NotionFixture.Version3;
        var later = client.CheckForUpdates(Devolutions.AgentSkills.SkillScope.Global);
        Assert.Empty(later.Unchecked);
        AssertNotionUpdate(Assert.Single(later.Updates), expectedSource, NotionFixture.Version2, NotionFixture.Version3);

        // Use the persisted library inventory, not fake InstalledSkillInfo records. Initialize
        // before configuring the source so no asynchronous source-listing task is left running.
        var backend = new ForwardingSkillsBackend(client);
        var manager = new AgentSkills(backend);
        AgentSkills.TargetAgents = ["claude-code"];
        manager.Initialize();
        Assert.True(manager.IsReady());
        Settings.SetList(AgentSkills.SourcesListKey, new List<string> { installSource });
        var offered = Assert.Single(manager.GetAvailableUpdates());
        Assert.Equal("code-review", offered.Id);
        Assert.Equal("0222222", offered.VersionString);
        Assert.Equal("0333333", offered.NewVersionString);
        Assert.Equal(expectedSource["https://".Length..], offered.Source.Name);
        Assert.Equal(1, backend.UpdateChecks);
        Assert.Equal(new[] { "code-review" }, backend.CheckedSkills);
        var forwarded = Assert.IsType<Devolutions.AgentSkills.SkillUpdateCheckResult>(backend.LastCheck);
        Assert.Empty(forwarded.Unchecked);
        AssertNotionUpdate(Assert.Single(forwarded.Updates), expectedSource, NotionFixture.Version2, NotionFixture.Version3);

        Settings.SetList(AgentSkills.SourcesListKey, new List<string>());
        Assert.Empty(manager.GetAvailableUpdates());
        Assert.Equal(1, backend.UpdateChecks);
        AssertNotionTracking(client, expectedSource, NotionFixture.Version2);
    }

    private void AssertNotionTracking(
        Devolutions.AgentSkills.SkillsManager client,
        string expectedSource,
        string version
    )
    {
        using var document = System.Text.Json.JsonDocument.Parse(
            File.ReadAllText(RequiredLockPath(Devolutions.AgentSkills.SkillScope.Global))
        );
        var entries = document.RootElement.GetProperty("skills");
        Assert.Single(entries.EnumerateObject());
        var entry = entries.GetProperty("code-review");
        Assert.Equal(expectedSource, entry.GetProperty("source").GetString());
        Assert.Equal("notion", entry.GetProperty("sourceType").GetString());
        Assert.Equal(NotionFixture.PageSource, entry.GetProperty("sourceUrl").GetString());
        Assert.Equal(NotionFixture.PageId, entry.GetProperty("notionPageId").GetString());
        Assert.Equal(version, entry.GetProperty("notionVersionId").GetString());

        var installed = Assert.Single(client.GetInstalledSkills(Devolutions.AgentSkills.SkillScope.Global));
        Assert.Equal("code-review", installed.Name);
        Assert.Equal(Devolutions.AgentSkills.SkillScope.Global, installed.Scope);
        Assert.True(installed.IsTracked);
        Assert.Equal(expectedSource, installed.Source);
        Assert.Equal("notion", installed.SourceType);
        Assert.Equal(NotionFixture.PageSource, installed.SourceUrl);
        Assert.Equal(version, installed.Hash);
        Assert.Contains("claude-code", installed.Agents);
        Assert.Equal(
            SkillMarkdown($"Reviews code at {version}"),
            File.ReadAllText(Path.Combine(_home, ".claude", "skills", "code-review", "SKILL.md"))
        );
    }

    private static void AssertNotionUpdate(
        Devolutions.AgentSkills.SkillUpdate update,
        string source,
        string current,
        string latest
    )
    {
        Assert.Equal("code-review", update.Name);
        Assert.Equal(Devolutions.AgentSkills.SkillScope.Global, update.Scope);
        Assert.Equal(source, update.Source);
        Assert.Equal(current, update.CurrentHash);
        Assert.Equal(latest, update.LatestHash);
    }

    private sealed class NotionFixture(string testRoot, bool databaseSource)
    {
        public const string PageId = "11111111-2222-4333-8444-555555555555";
        public const string DatabaseId = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
        private const string DataSourceId = "12345678-90ab-4cde-8fab-1234567890ab";
        public const string PageSource = "https://app.notion.com/p/11111111222243338444555555555555";
        public const string DatabaseSource = "https://app.notion.com/p/aaaaaaaabbbb4ccc8dddeeeeeeeeeeee";
        public const string Version1 = "01111111-1111-4111-8111-111111111111";
        public const string Version2 = "02222222-2222-4222-8222-222222222222";
        public const string Version3 = "03333333-3333-4333-8333-333333333333";
        private const string DownloadUrl = "https://notion-download.invalid/code-review.zip";

        public string Version { get; set; } = Version1;

        // Both source forms resolve to this page, which deliberately names a DIFFERENT parent
        // database. Replacing an explicit page's source with that parent must fail the tests.
        private static string PageJson => $$"""
            {
              "object": "page",
              "id": "{{PageId}}",
              "parent": { "type": "database_id", "database_id": "{{DatabaseId}}" },
              "properties": {
                "Name": { "type": "title", "title": [{ "plain_text": "Code Review" }] },
                "Description": { "type": "rich_text", "rich_text": [{ "plain_text": "Reviews code" }] }
              }
            }
            """;

        public global::Skills.ProcOutput Run(IReadOnlyList<string> args)
        {
            Assert.True(args.Count >= 4, $"Unexpected Notion CLI arguments: {string.Join(" ", args)}");
            Assert.Equal("api", args[0]);
            string? method = null;
            string? body = null;
            string? apiVersion = null;
            for (int index = 2; index < args.Count; index += 2)
            {
                Assert.True(index + 1 < args.Count, "Notion CLI option has no value");
                switch (args[index])
                {
                    case "-X":
                        Assert.Null(method);
                        method = args[index + 1];
                        break;
                    case "-d":
                        Assert.Null(body);
                        body = args[index + 1];
                        break;
                    case "--notion-version":
                        Assert.Null(apiVersion);
                        apiVersion = args[index + 1];
                        break;
                    default:
                        throw new InvalidOperationException($"Unexpected Notion CLI option: {args[index]}");
                }
            }
            Assert.Equal("2026-03-11", apiVersion);

            string path = args[1];
            if (path == $"v1/data_sources/{DataSourceId}/query")
            {
                Assert.True(databaseSource, "An explicit page must not query its parent database");
                Assert.Equal("POST", method);
                Assert.NotNull(body);
                using var query = System.Text.Json.JsonDocument.Parse(body);
                Assert.Equal(100, query.RootElement.GetProperty("page_size").GetInt32());
                Assert.False(query.RootElement.TryGetProperty("start_cursor", out _));
                return JsonOutput($$"""{"results":[{{PageJson}}],"has_more":false}""");
            }

            Assert.Null(method);
            Assert.Null(body);
            if (path == $"v1/databases/{PageId}")
            {
                Assert.False(databaseSource, "A database update must retain its original database source");
                return JsonOutput(
                    """{"object":"error","status":400,"code":"validation_error","message":"is a page, not a database"}""",
                    status: 1
                );
            }
            if (path == $"v1/databases/{DatabaseId}")
            {
                Assert.True(databaseSource, "An explicit page must not be replaced by its parent database");
                return JsonOutput($$"""{"id":"{{DatabaseId}}","data_sources":[{"id":"{{DataSourceId}}"}]}""");
            }
            if (path == $"v1/pages/{PageId}")
            {
                Assert.False(databaseSource, "A database source must be enumerated through its data source");
                return JsonOutput(PageJson);
            }
            if (path == $"v1/ai/skills/{PageId}")
                return JsonOutput($$"""{"version_id":"{{Version}}","url":"{{DownloadUrl}}"}""");

            throw new InvalidOperationException($"Unexpected Notion API path: {path}");
        }

        public global::Skills.DownloadedSource Download(string url)
        {
            Assert.Equal(DownloadUrl, url);
            string temp = Path.Combine(testRoot, "notion-downloads", Guid.NewGuid().ToString("N"));
            string skillDirectory = Path.Combine(temp, "code-review");
            Directory.CreateDirectory(skillDirectory);
            File.WriteAllText(Path.Combine(skillDirectory, "SKILL.md"), SkillMarkdown($"Reviews code at {Version}"));
            return new global::Skills.DownloadedSource(skillDirectory, temp, global::Skills.DownloadKind.Archive);
        }

        private static global::Skills.ProcOutput JsonOutput(string json, int status = 0) =>
            new(status, Encoding.UTF8.GetBytes(json), []);
    }

    // The production backend creates its own options. This test-only adapter keeps the same
    // injected public library client, including its deterministic Notion hooks, across calls.
    private sealed class ForwardingSkillsBackend(Devolutions.AgentSkills.SkillsManager client) : ISkillsBackend
    {
        public int UpdateChecks { get; private set; }
        public IReadOnlyList<string>? CheckedSkills { get; private set; }
        public Devolutions.AgentSkills.SkillUpdateCheckResult? LastCheck { get; private set; }
        public string Version => Devolutions.AgentSkills.SkillsManager.Version;

        public IReadOnlyList<Devolutions.AgentSkills.AgentInfo> GetAgents() => client.GetAgents();

        public IReadOnlyList<Devolutions.AgentSkills.InstalledSkillInfo> GetInstalledSkills(CancellationToken cancellationToken) =>
            client.GetInstalledSkills(Devolutions.AgentSkills.SkillScope.Global, cancellationToken);

        public IReadOnlyList<Devolutions.AgentSkills.AvailableSkill> GetAvailableSkills(
            string source,
            IProgress<string>? progress,
            CancellationToken cancellationToken
        ) => client.GetAvailableSkills(source, false, progress, cancellationToken);

        public Devolutions.AgentSkills.SkillInstallResult Install(
            string source,
            string skill,
            IReadOnlyList<string>? agents,
            IProgress<string>? progress,
            CancellationToken cancellationToken
        ) => client.Install(new Devolutions.AgentSkills.SkillInstallRequest
        {
            Source = source,
            Skills = [skill],
            Agents = agents,
            Scope = Devolutions.AgentSkills.SkillScope.Global,
            Mode = Devolutions.AgentSkills.SkillInstallMode.Copy,
        }, progress, cancellationToken);

        public Devolutions.AgentSkills.SkillUpdateCheckResult CheckForUpdates(
            IReadOnlyList<string>? skills,
            IProgress<string>? progress,
            CancellationToken cancellationToken
        )
        {
            UpdateChecks++;
            CheckedSkills = skills?.ToArray();
            var result = client.CheckForUpdates(Devolutions.AgentSkills.SkillScope.Global, skills, progress, cancellationToken);
            LastCheck = result;
            return result;
        }

        public Devolutions.AgentSkills.SkillUpdateResult Update(
            IReadOnlyList<Devolutions.AgentSkills.SkillUpdate> updates,
            IProgress<string>? progress,
            CancellationToken cancellationToken
        ) => client.Update(updates, progress, cancellationToken);

        public Devolutions.AgentSkills.SkillRemoveResult Remove(
            string skill,
            IProgress<string>? progress,
            CancellationToken cancellationToken
        ) => client.Remove([skill], Devolutions.AgentSkills.SkillScope.Global, null, progress, cancellationToken);

        public Devolutions.AgentSkills.NotionStatus GetNotionStatus(CancellationToken cancellationToken) =>
            client.GetNotionStatus(cancellationToken);

        public Devolutions.AgentSkills.NotionSignIn BeginNotionSignIn(CancellationToken cancellationToken) =>
            client.BeginNotionSignIn(cancellationToken);

        public void CompleteNotionSignIn(CancellationToken cancellationToken) =>
            client.CompleteNotionSignIn(cancellationToken);

        public void SignOutOfNotion(CancellationToken cancellationToken) => client.SignOutOfNotion(cancellationToken);
    }
}
