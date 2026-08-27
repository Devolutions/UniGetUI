using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools.Scheduling;
using UniGetUI.PackageEngine.Classes.Packages.Classes;
using UniGetUI.PackageEngine.PackageClasses;
using UniGetUI.PackageEngine.Serializable;
using UniGetUI.PackageEngine.Tests.Infrastructure.Builders;

namespace UniGetUI.PackageEngine.Tests;

public sealed class AutoUpdatesDatabaseTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        nameof(AutoUpdatesDatabaseTests),
        Guid.NewGuid().ToString("N")
    );

    public AutoUpdatesDatabaseTests()
    {
        Directory.CreateDirectory(_testRoot);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
        Directory.CreateDirectory(CoreData.UniGetUIInstallationOptionsDirectory);
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
    public void AddGetAndRemoveRoundTrip()
    {
        var manager = new PackageManagerBuilder().Build();
        var package = new PackageBuilder().WithManager(manager).WithId("Contoso.Tool").Build();
        string id = AutoUpdatesDatabase.GetIdForPackage(package);

        Assert.False(AutoUpdatesDatabase.IsAutoUpdated(package));

        AutoUpdatesDatabase.Add(id);

        Assert.True(AutoUpdatesDatabase.IsAutoUpdated(package));
        Assert.Equal(1, AutoUpdatesDatabase.Count);
        Assert.True(AutoUpdatesDatabase.Remove(id));
        Assert.False(AutoUpdatesDatabase.IsAutoUpdated(id));
        Assert.False(AutoUpdatesDatabase.Remove(id));
    }

    [Fact]
    public void TheIdentifierMatchesTheIgnoredUpdatesOne()
    {
        var manager = new PackageManagerBuilder().WithName("Contoso Pkg.Manager").Build();
        var package = new PackageBuilder().WithManager(manager).WithId("Contoso.Tool").Build();

        Assert.Equal(
            IgnoredUpdatesDatabase.GetIgnoredIdForPackage(package),
            AutoUpdatesDatabase.GetIdForPackage(package));
    }

    [Fact]
    public void TheIdentifiersAreMatchedCaseSensitively()
    {
        AutoUpdatesDatabase.Add("winget\\Contoso.Tool");

        Assert.True(AutoUpdatesDatabase.IsAutoUpdated("winget\\Contoso.Tool"));
        Assert.False(AutoUpdatesDatabase.IsAutoUpdated("winget\\contoso.tool"));
    }

    [Fact]
    public void AddRangeAndRemoveRangeKeepTheUntouchedEntries()
    {
        AutoUpdatesDatabase.Add("winget\\Kept.Tool");

        AutoUpdatesDatabase.AddRange(["winget\\First", "winget\\Second", "winget\\Kept.Tool"]);
        Assert.Equal(3, AutoUpdatesDatabase.Count);

        AutoUpdatesDatabase.RemoveRange(["winget\\First", "winget\\Missing"]);

        Assert.Equal(2, AutoUpdatesDatabase.Count);
        Assert.False(AutoUpdatesDatabase.IsAutoUpdated("winget\\First"));
        Assert.True(AutoUpdatesDatabase.IsAutoUpdated("winget\\Second"));
        Assert.True(AutoUpdatesDatabase.IsAutoUpdated("winget\\Kept.Tool"));
    }

    [Fact]
    public void TheMigrationTellsSimilarlyNamedManagersApart()
    {
        var powerShell = new PackageManagerBuilder().WithName("PowerShell").Build();
        var powerShell7 = new PackageManagerBuilder().WithName("PowerShell7").Build();

        WriteOptions(powerShell.Name, "7.Zip", autoUpdate: true);
        WriteOptions(powerShell7.Name, "Contoso.Tool", autoUpdate: true);

        AutoUpdatesMigration.RunOnce([powerShell, powerShell7]);

        Assert.True(AutoUpdatesDatabase.IsAutoUpdated("powershell\\7.Zip"));
        Assert.True(AutoUpdatesDatabase.IsAutoUpdated("powershell7\\Contoso.Tool"));
        Assert.Equal(2, AutoUpdatesDatabase.Count);
    }

    [Fact]
    public void TheMigrationImportsTheLegacyPerPackageOption()
    {
        var manager = new PackageManagerBuilder().WithName("Contoso Pkg.Manager").Build();

        WriteOptions(manager.Name, "Contoso.Tool", autoUpdate: true);
        WriteOptions(manager.Name, "Contoso.Other", autoUpdate: false);
        WriteManagerDefaults(manager.Name);

        AutoUpdatesMigration.RunOnce([manager]);

        Assert.True(AutoUpdatesDatabase.IsAutoUpdated("contoso pkg.manager\\Contoso.Tool"));
        Assert.False(AutoUpdatesDatabase.IsAutoUpdated("contoso pkg.manager\\Contoso.Other"));
        Assert.Equal(1, AutoUpdatesDatabase.Count);
    }

    [Fact]
    public void TheMigrationKeepsTheLegacyInstallsRunningAndOnlyRunsOnce()
    {
        var manager = new PackageManagerBuilder().WithName("TestManager").Build();
        WriteOptions(manager.Name, "Contoso.Tool", autoUpdate: true);

        Assert.False(MaintenanceScheduleStore.IsEnabled(MaintenanceTaskKind.InstallUpdates));

        AutoUpdatesMigration.RunOnce([manager]);

        var schedule = MaintenanceScheduleStore.Get(MaintenanceTaskKind.InstallUpdates);
        Assert.True(schedule.Enabled);
        Assert.Equal(ScheduleFrequency.AfterEveryUpdateCheck, schedule.Frequency);
        Assert.Equal(ScheduleInstallTargets.MarkedPackagesOnly, schedule.InstallTargets);

        AutoUpdatesDatabase.Clear();
        AutoUpdatesMigration.RunOnce([manager]);
        Assert.Equal(0, AutoUpdatesDatabase.Count);
    }

    [Fact]
    public void TheMigrationIsOnlyMarkedDoneOnceTheScheduleIsInPlace()
    {
        var manager = new PackageManagerBuilder().WithName("TestManager").Build();
        WriteOptions(manager.Name, "Contoso.Tool", autoUpdate: true);

        AutoUpdatesMigration.RunOnce([manager]);

        Assert.True(Settings.Get(Settings.K.AutoUpdatedPackagesImported));
        Assert.True(MaintenanceScheduleStore.IsEnabled(MaintenanceTaskKind.InstallUpdates));
        Assert.Equal(
            ScheduleInstallTargets.MarkedPackagesOnly,
            MaintenanceScheduleStore.Get(MaintenanceTaskKind.InstallUpdates).InstallTargets);
    }

    [Fact]
    public void TheMigrationCompletesWithoutTouchingTheScheduleWhenNothingIsMarked()
    {
        var manager = new PackageManagerBuilder().WithName("TestManager").Build();

        AutoUpdatesMigration.RunOnce([manager]);
        Assert.True(Settings.Get(Settings.K.AutoUpdatedPackagesImported));
        Assert.Equal(0, AutoUpdatesDatabase.Count);
        Assert.False(MaintenanceScheduleStore.IsEnabled(MaintenanceTaskKind.InstallUpdates));
    }

    [Fact]
    public void TheMigrationLeavesAnAlreadyEnabledTaskAlone()
    {
        var manager = new PackageManagerBuilder().WithName("TestManager").Build();
        WriteOptions(manager.Name, "Contoso.Tool", autoUpdate: true);
        Settings.Set(Settings.K.AutomaticallyUpdatePackages, true);

        AutoUpdatesMigration.RunOnce([manager]);

        var schedule = MaintenanceScheduleStore.Get(MaintenanceTaskKind.InstallUpdates);
        Assert.True(schedule.Enabled);
        Assert.Equal(ScheduleInstallTargets.AllPackages, schedule.InstallTargets);
    }

    private static void WriteOptions(string managerName, string packageId, bool autoUpdate)
    {
        var options = new InstallOptions { AutoUpdatePackage = autoUpdate, OverridesNextLevelOpts = true };
        string fileName = managerName.Replace(" ", "").Replace(".", "") + "." + packageId + ".json";
        File.WriteAllText(
            Path.Join(CoreData.UniGetUIInstallationOptionsDirectory, fileName),
            options.AsJsonString());
    }

    private static void WriteManagerDefaults(string managerName)
    {
        var options = new InstallOptions { AutoUpdatePackage = true };
        string fileName = "GlobalValues." + managerName.Replace(" ", "").Replace(".", "") + ".json";
        File.WriteAllText(
            Path.Join(CoreData.UniGetUIInstallationOptionsDirectory, fileName),
            options.AsJsonString());
    }
}
