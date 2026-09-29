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
        Assert.Equal("127.0.0.1", found.Source.Name);

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
}
