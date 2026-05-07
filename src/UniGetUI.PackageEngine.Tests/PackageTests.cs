using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.SettingsEngine.SecureSettings;
using UniGetUI.PackageEngine.PackageLoader;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

namespace UniGetUI.PackageEngine.Tests;

public sealed class NewerVersionIsInstalledTests : IDisposable
{
    private readonly string _testRoot;

    public NewerVersionIsInstalledTests()
    {
        _testRoot = Path.Combine(
            Path.GetTempPath(),
            nameof(NewerVersionIsInstalledTests),
            Guid.NewGuid().ToString("N")
        );
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

    [Theory]
    [InlineData("1.0.0", "2.0.0", false)]
    [InlineData("2.0.0", "2.0.0", true)]
    [InlineData("3.0.0", "2.0.0", true)]
    [InlineData("10c8e557", "8b640eef", false)]
    [InlineData("10c8e557", "2.0.0", false)]
    [InlineData("2.0.0", "8b640eef", false)]
    [InlineData("", "2.0.0", false)]
    [InlineData("1.0.0;3.0.0", "2.0.0", true)]
    public async Task NewerVersionIsInstalled_ReturnsExpectedResult(string installedVersions, string newVersion, bool expected)
    {
        var manager = new PackageManagerBuilder().Build();
        InitializeLoaders();

        foreach (var v in installedVersions.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            await InstalledPackagesLoader.Instance.AddForeign(
                new PackageBuilder().WithManager(manager).WithId("Contoso.Tool").WithVersion(v.Trim()).Build()
            );
        }

        var update = new PackageBuilder()
            .WithManager(manager).WithId("Contoso.Tool").WithVersion("1.0.0").WithNewVersion(newVersion).Build();

        Assert.Equal(expected, update.NewerVersionIsInstalled());
    }

    private static void InitializeLoaders()
    {
        _ = new DiscoverablePackagesLoader([]);
        _ = new UpgradablePackagesLoader([]);
        _ = new InstalledPackagesLoader([]);
    }
}
