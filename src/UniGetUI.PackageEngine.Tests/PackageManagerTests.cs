using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;
using UniGetUI.PackageEngine.Tests.Infrastructure.Fakes;

namespace UniGetUI.PackageEngine.Tests;

public sealed class PackageManagerTests : IDisposable
{
    private readonly string _testRoot;

    public PackageManagerTests()
    {
        _testRoot = Path.Combine(
            AppContext.BaseDirectory,
            nameof(PackageManagerTests),
            Guid.NewGuid().ToString("N")
        );
        var secureSettingsRoot = Path.Combine(_testRoot, "SecureSettings");

        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        SecureSettings.TEST_SecureSettingsRootOverride = secureSettingsRoot;

        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Directory.CreateDirectory(secureSettingsRoot);

        Settings.ResetSettings();
        Settings.SetDictionary(Settings.K.DisabledManagers, new Dictionary<string, bool>());
        Settings.SetDictionary(Settings.K.ManagerPaths, new Dictionary<string, string>());
        SecureSettings.ApplyForUser(
            Environment.UserName,
            SecureSettings.ResolveKey(SecureSettings.K.AllowCustomManagerPaths),
            false
        );
    }

    public void Dispose()
    {
        Settings.SetDictionary(Settings.K.DisabledManagers, new Dictionary<string, bool>());
        Settings.SetDictionary(Settings.K.ManagerPaths, new Dictionary<string, string>());
        SecureSettings.ApplyForUser(
            Environment.UserName,
            SecureSettings.ResolveKey(SecureSettings.K.AllowCustomManagerPaths),
            false
        );

        CoreData.TEST_DataDirectoryOverride = null;
        SecureSettings.TEST_SecureSettingsRootOverride = null;

        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, true);
        }
    }

    [Fact]
    public void InitializeDisabledManagerSkipsLoadingAndStaysNotReady()
    {
        var manager = CreateManager();
        Settings.SetDictionaryItem<string, bool>(Settings.K.DisabledManagers, manager.Name, true);

        manager.Initialize();

        Assert.False(manager.IsEnabled());
        Assert.False(manager.IsReady());
        Assert.False(manager.Status.Found);
        Assert.Equal(string.Empty, manager.Status.ExecutablePath);
        Assert.Equal(
            CoreTools.Translate("{0} is disabled", manager.DisplayName),
            manager.Status.Version
        );
    }

    [Fact]
    public void InitializeEnabledManagerWithoutExecutableStaysNotReady()
    {
        var manager = CreateManager();
        manager.ExecutableFound = false;
        manager.ExecutablePath = CreateExecutable("missing-manager.exe");

        manager.Initialize();

        Assert.True(manager.IsEnabled());
        Assert.False(manager.IsReady());
        Assert.False(manager.Status.Found);
        Assert.Equal(manager.ExecutablePath, manager.Status.ExecutablePath);
        Assert.Equal(
            CoreTools.Translate("{pm} was not found!").Replace("{pm}", manager.DisplayName).Trim('!'),
            manager.Status.Version
        );
    }

    [Fact]
    public void InitializeEnabledManagerWithExecutableBecomesReady()
    {
        var manager = CreateManager();
        manager.ExecutablePath = CreateExecutable("ready-manager.exe");
        manager.LoadedVersion = "9.9.9-test";

        manager.Initialize();

        Assert.True(manager.IsEnabled());
        Assert.True(manager.IsReady());
        Assert.True(manager.Status.Found);
        Assert.Equal(manager.ExecutablePath, manager.Status.ExecutablePath);
        Assert.Equal("9.9.9-test", manager.Status.Version);
    }

    [Fact]
    public void IsReadyTracksEnablementChangesAfterInitialization()
    {
        var manager = CreateManager();
        manager.ExecutablePath = CreateExecutable("toggle-manager.exe");

        Assert.True(manager.IsEnabled());
        Assert.False(manager.IsReady());

        manager.Initialize();

        Assert.True(manager.IsReady());

        Settings.SetDictionaryItem<string, bool>(Settings.K.DisabledManagers, manager.Name, true);

        Assert.False(manager.IsEnabled());
        Assert.False(manager.IsReady());

        Settings.SetDictionaryItem<string, bool>(Settings.K.DisabledManagers, manager.Name, false);

        Assert.True(manager.IsEnabled());
        Assert.True(manager.IsReady());
    }

    [Fact]
    public void GetExecutableFileReturnsFalseWhenNoCandidatesExist()
    {
        var manager = CreateManager();
        manager.SetCandidateExecutableFiles();

        var executable = manager.GetExecutableFile();

        Assert.False(executable.Item1);
        Assert.Equal(string.Empty, executable.Item2);
    }

    [Fact]
    public void GetExecutableFileIgnoresSavedPathWhenCustomPathsAreDisabled()
    {
        var manager = CreateManager();
        var first = CreateExecutable("candidate-a.exe");
        var second = CreateExecutable("candidate-b.exe");
        manager.SetCandidateExecutableFiles(first, second);
        Settings.SetDictionaryItem<string, string>(Settings.K.ManagerPaths, manager.Name, second);

        var executable = manager.GetExecutableFile();

        Assert.True(executable.Item1);
        Assert.Equal(first, executable.Item2);
    }

    [Fact]
    public void GetExecutableFileUsesSavedCandidateWhenCustomPathsAreEnabled()
    {
        var manager = CreateManager();
        var first = CreateExecutable("enabled-a.exe");
        var second = CreateExecutable("enabled-b.exe");
        manager.SetCandidateExecutableFiles(first, second);
        EnableCustomManagerPaths();
        Settings.SetDictionaryItem<string, string>(Settings.K.ManagerPaths, manager.Name, second);

        var executable = manager.GetExecutableFile();

        Assert.True(executable.Item1);
        Assert.Equal(second, executable.Item2);
    }

    [Fact]
    public void GetExecutableFileFallsBackWhenSavedPathDoesNotExist()
    {
        var manager = CreateManager();
        var first = CreateExecutable("fallback-a.exe");
        var second = CreateExecutable("fallback-b.exe");
        manager.SetCandidateExecutableFiles(first, second);
        EnableCustomManagerPaths();
        Settings.SetDictionaryItem<string, string>(
            Settings.K.ManagerPaths,
            manager.Name,
            Path.Combine(_testRoot, "missing.exe")
        );

        var executable = manager.GetExecutableFile();

        Assert.True(executable.Item1);
        Assert.Equal(first, executable.Item2);
    }

    [Fact]
    public void GetExecutableFileFallsBackWhenSavedPathIsNotACandidate()
    {
        var manager = CreateManager();
        var first = CreateExecutable("outside-a.exe");
        var second = CreateExecutable("outside-b.exe");
        var outsideCandidateList = CreateExecutable("outside-c.exe");
        manager.SetCandidateExecutableFiles(first, second);
        EnableCustomManagerPaths();
        Settings.SetDictionaryItem<string, string>(
            Settings.K.ManagerPaths,
            manager.Name,
            outsideCandidateList
        );

        var executable = manager.GetExecutableFile();

        Assert.True(executable.Item1);
        Assert.Equal(first, executable.Item2);
    }

    [Fact]
    public void FindPackagesReturnsEmptyWhenManagerIsNotReady()
    {
        var manager = CreateManager();

        var packages = manager.FindPackages("tool");

        Assert.Empty(packages);
        Assert.Null(manager.LastQuery);
        Assert.Equal(0, manager.AttemptFastRepairCalls);
    }

    [Fact]
    public void FindPackagesRetriesOnceAfterFailure()
    {
        var manager = CreateReadyManager();
        var attempts = 0;
        manager.SetFindPackages(query =>
        {
            attempts++;
            return attempts == 1
                ? throw new InvalidOperationException("search failed")
                : [CreatePackage(manager, "Contoso.Search", "Contoso Search")];
        });

        var packages = manager.FindPackages("Contoso");

        var package = Assert.Single(packages);
        Assert.Equal("Contoso.Search", package.Id);
        Assert.Equal("Contoso", manager.LastQuery);
        Assert.Equal(1, manager.AttemptFastRepairCalls);
    }

    [Fact]
    public void GetAvailableUpdatesRetriesOnceAndRefreshesIndexesPerAttempt()
    {
        var manager = CreateReadyManager();
        var attempts = 0;
        manager.SetAvailableUpdates(() =>
        {
            attempts++;
            return attempts == 1
                ? throw new InvalidOperationException("updates failed")
                : [CreatePackage(manager, "Contoso.Update", "Contoso Update", "1.0.0", "2.0.0")];
        });

        var packages = manager.GetAvailableUpdates();

        var package = Assert.Single(packages);
        Assert.Equal("Contoso.Update", package.Id);
        Assert.Equal(1, manager.AttemptFastRepairCalls);
        Assert.Equal(2, manager.RefreshPackageIndexesCalls);
    }

    [Fact]
    public void GetInstalledPackagesReturnsEmptyAfterSecondFailure()
    {
        var manager = CreateReadyManager();
        manager.SetInstalledPackages(() => throw new InvalidOperationException("installed failed"));

        var packages = manager.GetInstalledPackages();

        Assert.Empty(packages);
        Assert.Equal(1, manager.AttemptFastRepairCalls);
    }

    private static TestPackageManager CreateManager()
    {
        return new PackageManagerBuilder()
            .WithName($"TestManager-{Guid.NewGuid():N}")
            .WithDisplayName("Test Manager")
            .Build(initialize: false);
    }

    private TestPackageManager CreateReadyManager()
    {
        var manager = CreateManager();
        manager.ExecutablePath = CreateExecutable($"{manager.Name}.exe");
        manager.Initialize();
        Assert.True(manager.IsReady());
        return manager;
    }

    private static void EnableCustomManagerPaths()
    {
        Assert.Equal(
            0,
            SecureSettings.ApplyForUser(
                Environment.UserName,
                SecureSettings.ResolveKey(SecureSettings.K.AllowCustomManagerPaths),
                true
            )
        );
    }

    private string CreateExecutable(string fileName)
    {
        var path = Path.Combine(_testRoot, "Executables", fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private static Package CreatePackage(
        TestPackageManager manager,
        string id,
        string name,
        string version = "1.0.0",
        string? newVersion = null
    )
    {
        var builder = new PackageBuilder().WithManager(manager).WithId(id).WithName(name).WithVersion(version);
        return newVersion is null ? builder.Build() : builder.WithNewVersion(newVersion).Build();
    }
}
