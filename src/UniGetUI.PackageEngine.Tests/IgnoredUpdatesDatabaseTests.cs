using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

namespace UniGetUI.PackageEngine.Tests;

public sealed class IgnoredUpdatesDatabaseTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        nameof(IgnoredUpdatesDatabaseTests),
        Guid.NewGuid().ToString("N")
    );

    public IgnoredUpdatesDatabaseTests()
    {
        Directory.CreateDirectory(_testRoot);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Settings.ResetSettings();
    }

    public void Dispose()
    {
        Settings.ResetSettings();
        CoreData.TEST_DataDirectoryOverride = null;
        if (Directory.Exists(_testRoot))
        {
            Directory.Delete(_testRoot, recursive: true);
        }
    }

    [Fact]
    public void AddGetAndRemoveRoundTripForSpecificVersions()
    {
        var manager = new PackageManagerBuilder().Build();
        var package = new PackageBuilder().WithManager(manager).WithId("Contoso.Tool").Build();
        string ignoredId = IgnoredUpdatesDatabase.GetIgnoredIdForPackage(package);

        IgnoredUpdatesDatabase.Add(ignoredId, "2.0.0");

        Assert.Equal("2.0.0", IgnoredUpdatesDatabase.GetIgnoredVersion(ignoredId));
        Assert.True(IgnoredUpdatesDatabase.HasUpdatesIgnored(ignoredId, "2.0.0"));
        Assert.False(IgnoredUpdatesDatabase.HasUpdatesIgnored(ignoredId, "3.0.0"));
        Assert.True(IgnoredUpdatesDatabase.Remove(ignoredId));
        Assert.Null(IgnoredUpdatesDatabase.GetIgnoredVersion(ignoredId));
    }

    [Fact]
    public void HasUpdatesIgnored_HonorsWildcardAndExpiresPastDateEntries()
    {
        const string ignoredId = "manager\\contoso.tool";
        string futureDate = $"<{DateTime.Today.AddDays(5):yyyy-MM-dd}";
        string pastDate = $"<{DateTime.Today.AddDays(-5):yyyy-MM-dd}";

        IgnoredUpdatesDatabase.Add(ignoredId, "*");
        Assert.True(IgnoredUpdatesDatabase.HasUpdatesIgnored(ignoredId, "9.9.9"));

        Settings.SetDictionary(Settings.K.IgnoredPackageUpdates, new Dictionary<string, string> { [ignoredId] = futureDate });
        Assert.True(IgnoredUpdatesDatabase.HasUpdatesIgnored(ignoredId, "1.0.0"));

        Settings.SetDictionary(Settings.K.IgnoredPackageUpdates, new Dictionary<string, string> { [ignoredId] = pastDate });
        Assert.False(IgnoredUpdatesDatabase.HasUpdatesIgnored(ignoredId, "1.0.0"));
        Assert.Null(IgnoredUpdatesDatabase.GetIgnoredVersion(ignoredId));
    }

    [Theory]
    [InlineData(14)]
    [InlineData(30)]
    [InlineData(365)]
    public void PauseTime_StringRepresentationUsesLargestFriendlyUnit(int days)
    {
        var pauseTime = new IgnoredUpdatesDatabase.PauseTime { Days = days };

        string expected = days switch
        {
            14 => CoreTools.Translate("{0} weeks", 2),
            30 => CoreTools.Translate("1 month"),
            365 => CoreTools.Translate("{0} months", 13),
            _ => throw new InvalidOperationException("Unexpected test input"),
        };

        Assert.Equal(expected, pauseTime.StringRepresentation());
    }
}
