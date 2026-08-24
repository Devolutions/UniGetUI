using System.Globalization;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;

namespace UniGetUI.Core.Tools.Tests;

public class LocalBackupManagerTests : IDisposable
{
    private const string BaseName = "TESTPC installed packages";

    private readonly string _testRoot;
    private readonly string _backupDirectory;

    public LocalBackupManagerTests()
    {
        _testRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        _backupDirectory = Path.Combine(_testRoot, "Backups");
        Directory.CreateDirectory(_backupDirectory);
        CoreData.TEST_DataDirectoryOverride = Path.Combine(_testRoot, "Data");
        Directory.CreateDirectory(CoreData.UniGetUIUserConfigurationDirectory);
    }

    public void Dispose()
    {
        Settings.SetValue(Settings.K.ChangeBackupOutputDirectory, "");
        Settings.SetValue(Settings.K.MaxLocalBackupCount, "");
        Settings.SetValue(Settings.K.MaxLocalBackupCountCustom, "");
        Settings.SetValue(Settings.K.ChangeBackupFileName, "");
        Settings.Set(Settings.K.EnableBackupTimestamping, false);
        CoreData.TEST_DataDirectoryOverride = null;
        Directory.Delete(_testRoot, true);
        GC.SuppressFinalize(this);
    }

    private string CreateBackup(string fileName)
    {
        string path = Path.Combine(_backupDirectory, fileName);
        File.WriteAllText(path, "{}");
        return path;
    }

    private string CreateTimestampedBackup(DateTime timestamp, string baseName = BaseName)
        => CreateBackup(
            baseName
            + " "
            + timestamp.ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture)
            + ".ubundle");

    private IReadOnlyList<string> RemainingFiles() => Directory
        .GetFiles(_backupDirectory)
        .Select(Path.GetFileName)
        .Where(name => name is not null)
        .Select(name => name!)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToList();

    [Fact]
    public void OnlyTheMostRecentBackupsAreKept()
    {
        for (int day = 1; day <= 5; day++)
            CreateTimestampedBackup(new DateTime(2026, 8, day, 10, 0, 0));

        int deleted = LocalBackupManager.ApplyRetentionLimit(_backupDirectory, BaseName, 2);

        Assert.Equal(3, deleted);
        Assert.Equal(
            [
                $"{BaseName} 2026-08-04 10-00-00.ubundle",
                $"{BaseName} 2026-08-05 10-00-00.ubundle",
            ],
            RemainingFiles());
    }

    [Fact]
    public void NothingIsDeletedWhenTheLimitIsNotExceeded()
    {
        CreateTimestampedBackup(new DateTime(2026, 8, 1, 10, 0, 0));
        CreateTimestampedBackup(new DateTime(2026, 8, 2, 10, 0, 0));

        Assert.Equal(0, LocalBackupManager.ApplyRetentionLimit(_backupDirectory, BaseName, 2));
        Assert.Equal(2, RemainingFiles().Count);
    }

    [Fact]
    public void NothingIsDeletedWhenNoLimitIsSet()
    {
        for (int day = 1; day <= 4; day++)
            CreateTimestampedBackup(new DateTime(2026, 8, day, 10, 0, 0));

        Assert.Equal(0, LocalBackupManager.ApplyRetentionLimit(_backupDirectory, BaseName, 0));
        Assert.Equal(0, LocalBackupManager.ApplyRetentionLimit(_backupDirectory, BaseName, -1));
        Assert.Equal(4, RemainingFiles().Count);
    }

    [Fact]
    public void UnrelatedFilesAreNeverDeleted()
    {
        CreateTimestampedBackup(new DateTime(2026, 8, 1, 10, 0, 0));
        CreateTimestampedBackup(new DateTime(2026, 8, 2, 10, 0, 0));
        CreateBackup($"{BaseName}.ubundle");
        CreateBackup("Some other bundle.ubundle");
        CreateBackup($"{BaseName} not-a-timestamp.ubundle");
        CreateTimestampedBackup(new DateTime(2020, 1, 1, 10, 0, 0), "Another computer installed packages");
        CreateBackup($"{BaseName} 2026-08-03 10-00-00.txt");

        int deleted = LocalBackupManager.ApplyRetentionLimit(_backupDirectory, BaseName, 1);

        Assert.Equal(1, deleted);
        Assert.Equal(
            [
                "Another computer installed packages 2020-01-01 10-00-00.ubundle",
                "Some other bundle.ubundle",
                $"{BaseName} 2026-08-02 10-00-00.ubundle",
                $"{BaseName} 2026-08-03 10-00-00.txt",
                $"{BaseName} not-a-timestamp.ubundle",
                $"{BaseName}.ubundle",
            ],
            RemainingFiles());
    }

    [Fact]
    public void MissingDirectoriesAreIgnored()
    {
        Assert.Equal(
            0,
            LocalBackupManager.ApplyRetentionLimit(
                Path.Combine(_testRoot, "Missing"), BaseName, 1));
    }

    [Fact]
    public void TheRetentionLimitIsReadFromTheSettings()
    {
        Settings.SetValue(Settings.K.MaxLocalBackupCount, "");
        Assert.Equal(0, LocalBackupManager.GetRetentionLimit());

        Settings.SetValue(Settings.K.MaxLocalBackupCount, "0");
        Assert.Equal(0, LocalBackupManager.GetRetentionLimit());

        Settings.SetValue(Settings.K.MaxLocalBackupCount, "25");
        Assert.Equal(25, LocalBackupManager.GetRetentionLimit());

        Settings.SetValue(Settings.K.MaxLocalBackupCount, "custom");
        Settings.SetValue(Settings.K.MaxLocalBackupCountCustom, "7");
        Assert.Equal(7, LocalBackupManager.GetRetentionLimit());

        Settings.SetValue(Settings.K.MaxLocalBackupCountCustom, "");
        Assert.Equal(0, LocalBackupManager.GetRetentionLimit());

        Settings.SetValue(Settings.K.MaxLocalBackupCountCustom, "not a number");
        Assert.Equal(0, LocalBackupManager.GetRetentionLimit());

        Settings.SetValue(Settings.K.MaxLocalBackupCountCustom, "-3");
        Assert.Equal(0, LocalBackupManager.GetRetentionLimit());
    }

    [Fact]
    public void TheSettingsDrivenPruneUsesTheConfiguredDirectoryAndName()
    {
        Settings.SetValue(Settings.K.ChangeBackupOutputDirectory, _backupDirectory);
        Settings.SetValue(Settings.K.ChangeBackupFileName, BaseName);
        Settings.SetValue(Settings.K.MaxLocalBackupCount, "2");
        Settings.Set(Settings.K.EnableBackupTimestamping, true);
        for (int day = 1; day <= 5; day++)
            CreateTimestampedBackup(new DateTime(2026, 8, day, 10, 0, 0));

        Assert.Equal(3, LocalBackupManager.ApplyRetentionLimit());
        Assert.Equal(2, RemainingFiles().Count);
    }

    [Fact]
    public void NothingIsPrunedWhileSeparateFilesPerBackupAreDisabled()
    {
        Settings.SetValue(Settings.K.ChangeBackupOutputDirectory, _backupDirectory);
        Settings.SetValue(Settings.K.ChangeBackupFileName, BaseName);
        Settings.SetValue(Settings.K.MaxLocalBackupCount, "2");
        Settings.Set(Settings.K.EnableBackupTimestamping, false);
        for (int day = 1; day <= 5; day++)
            CreateTimestampedBackup(new DateTime(2026, 8, day, 10, 0, 0));

        Assert.Equal(0, LocalBackupManager.ApplyRetentionLimit());
        Assert.Equal(5, RemainingFiles().Count);
    }

    [Fact]
    public void BackupsNamedUnderANonGregorianCalendarKeepTheirRealChronology()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            Assert.Equal(
                new DateTime(2026, 8, 24, 10, 0, 0),
                LocalBackupManager.GetBackupTimestamp($"{BaseName} 2569-08-24 10-00-00.ubundle", BaseName));

            CreateBackup($"{BaseName} 2569-08-24 10-00-00.ubundle");
            CreateTimestampedBackup(new DateTime(2026, 8, 20, 10, 0, 0));
            CreateTimestampedBackup(new DateTime(2026, 8, 21, 10, 0, 0));

            Assert.Equal(1, LocalBackupManager.ApplyRetentionLimit(_backupDirectory, BaseName, 2));
            Assert.Equal(
                [
                    $"{BaseName} 2026-08-21 10-00-00.ubundle",
                    $"{BaseName} 2569-08-24 10-00-00.ubundle",
                ],
                RemainingFiles());
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public void ImplausibleDatesAreNotTreatedAsBackupTimestamps()
    {
        Assert.Null(LocalBackupManager.GetBackupTimestamp($"{BaseName} 1999-01-01 00-00-00.ubundle", BaseName));
        Assert.Null(LocalBackupManager.GetBackupTimestamp(
            $"{BaseName} " + DateTime.Now.AddYears(3).ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture) + ".ubundle", BaseName));
    }

    [Fact]
    public void TheBackupFileNameOnlyCarriesATimestampWhenTimestampingIsEnabled()
    {
        Settings.SetValue(Settings.K.ChangeBackupFileName, BaseName);

        Settings.Set(Settings.K.EnableBackupTimestamping, false);
        Assert.Equal($"{BaseName}.ubundle", LocalBackupManager.BuildFileName());

        Settings.Set(Settings.K.EnableBackupTimestamping, true);
        var timestamp = new DateTime(2026, 8, 24, 15, 30, 45);
        string fileName = LocalBackupManager.BuildFileName(timestamp);
        Assert.Equal($"{BaseName} 2026-08-24 15-30-45.ubundle", fileName);
        Assert.Equal(timestamp, LocalBackupManager.GetBackupTimestamp(fileName, BaseName));
    }
}
