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
        Settings.ClearDictionary(Settings.K.KnownLocalBackupNames);
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

    private static DateTime? GetTimestamp(string fileName, string fileNameBase)
        => LocalBackupManager.GetBackupIdentity(fileName, fileNameBase)?.Timestamp;

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

        int deleted = LocalBackupManager.ApplyRetentionLimit(_backupDirectory, [BaseName], 2);

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

        Assert.Equal(0, LocalBackupManager.ApplyRetentionLimit(_backupDirectory, [BaseName], 2));
        Assert.Equal(2, RemainingFiles().Count);
    }

    [Fact]
    public void NothingIsDeletedWhenNoLimitIsSet()
    {
        for (int day = 1; day <= 4; day++)
            CreateTimestampedBackup(new DateTime(2026, 8, day, 10, 0, 0));

        Assert.Equal(0, LocalBackupManager.ApplyRetentionLimit(_backupDirectory, [BaseName], 0));
        Assert.Equal(0, LocalBackupManager.ApplyRetentionLimit(_backupDirectory, [BaseName], -1));
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

        int deleted = LocalBackupManager.ApplyRetentionLimit(_backupDirectory, [BaseName], 1);

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
                Path.Combine(_testRoot, "Missing"), [BaseName], 1));
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
                GetTimestamp($"{BaseName} 2569-08-24 10-00-00.ubundle", BaseName));

            CreateBackup($"{BaseName} 2569-08-24 10-00-00.ubundle");
            CreateTimestampedBackup(new DateTime(2026, 8, 20, 10, 0, 0));
            CreateTimestampedBackup(new DateTime(2026, 8, 21, 10, 0, 0));

            Assert.Equal(1, LocalBackupManager.ApplyRetentionLimit(_backupDirectory, [BaseName], 2));
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
    public void NamesThatDoNotCarryOurTimestampShapeAreNotBackups()
    {
        Assert.Null(LocalBackupManager.GetBackupIdentity($"{BaseName} 2026-8-4 10-00-00.ubundle", BaseName));
        Assert.Null(LocalBackupManager.GetBackupIdentity($"{BaseName} 20260824 100000.ubundle", BaseName));
        Assert.Null(LocalBackupManager.GetBackupIdentity($"{BaseName} 2026-08-24T10-00-00.ubundle", BaseName));
        Assert.Null(LocalBackupManager.GetBackupIdentity($"{BaseName} not-a-timestamp.ubundle", BaseName));
    }

    [Fact]
    public void UntrustworthyDatesKeepTheFileAsABackupWithoutATimestamp()
    {
        Assert.Equal(
            (null, 1),
            LocalBackupManager.GetBackupIdentity($"{BaseName} 1999-01-01 00-00-00.ubundle", BaseName));
        Assert.Equal(
            (null, 2),
            LocalBackupManager.GetBackupIdentity(
                $"{BaseName} " + DateTime.Now.AddYears(3).ToString("yyyy-MM-dd HH-mm-ss", CultureInfo.InvariantCulture) + " (2).ubundle",
                BaseName));
    }

    [Fact]
    public void BackupsNamedUnderACalendarNoLongerInUseArePrunedByTheirWriteTime()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            string legacy = CreateBackup($"{BaseName} 2569-08-19 10-00-00.ubundle");
            File.SetLastWriteTime(legacy, new DateTime(2026, 8, 19, 10, 0, 0));
            CreateTimestampedBackup(new DateTime(2026, 8, 20, 10, 0, 0));
            CreateTimestampedBackup(new DateTime(2026, 8, 21, 10, 0, 0));

            Assert.Equal(
                (null, 1),
                LocalBackupManager.GetBackupIdentity(Path.GetFileName(legacy), BaseName));
            Assert.Equal(1, LocalBackupManager.ApplyRetentionLimit(_backupDirectory, [BaseName], 2));
            Assert.Equal(
                [
                    $"{BaseName} 2026-08-20 10-00-00.ubundle",
                    $"{BaseName} 2026-08-21 10-00-00.ubundle",
                ],
                RemainingFiles());
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
        }
    }

    [Fact]
    public async Task BackupsTakenWithinTheSameSecondGetDistinctFiles()
    {
        Settings.SetValue(Settings.K.ChangeBackupOutputDirectory, _backupDirectory);
        Settings.SetValue(Settings.K.ChangeBackupFileName, BaseName);
        Settings.Set(Settings.K.EnableBackupTimestamping, true);
        var timestamp = new DateTime(2026, 8, 24, 15, 30, 45);

        string first = await LocalBackupManager.SaveBackupAsync("first", timestamp);
        string second = await LocalBackupManager.SaveBackupAsync("second", timestamp);
        string third = await LocalBackupManager.SaveBackupAsync("third", timestamp);

        Assert.Equal($"{BaseName} 2026-08-24 15-30-45.ubundle", Path.GetFileName(first));
        Assert.Equal($"{BaseName} 2026-08-24 15-30-45 (2).ubundle", Path.GetFileName(second));
        Assert.Equal($"{BaseName} 2026-08-24 15-30-45 (3).ubundle", Path.GetFileName(third));
        Assert.Equal("first", await File.ReadAllTextAsync(first));
        Assert.Equal("second", await File.ReadAllTextAsync(second));
        Assert.Equal("first"u8.ToArray(), await File.ReadAllBytesAsync(first));
        Assert.Equal(3, RemainingFiles().Count);
    }

    [Fact]
    public async Task TheSingleBackupFileIsStillOverwrittenWhenTimestampingIsDisabled()
    {
        Settings.SetValue(Settings.K.ChangeBackupOutputDirectory, _backupDirectory);
        Settings.SetValue(Settings.K.ChangeBackupFileName, BaseName);
        Settings.Set(Settings.K.EnableBackupTimestamping, false);

        string first = await LocalBackupManager.SaveBackupAsync("first");
        string second = await LocalBackupManager.SaveBackupAsync("second");

        Assert.Equal(first, second);
        Assert.Equal([$"{BaseName}.ubundle"], RemainingFiles());
        Assert.Equal("second", await File.ReadAllTextAsync(second));
    }

    [Fact]
    public void SameSecondBackupsAreCountedAndOrderedBySequence()
    {
        CreateTimestampedBackup(new DateTime(2026, 8, 24, 10, 0, 0));
        CreateBackup($"{BaseName} 2026-08-24 10-00-00 (2).ubundle");
        CreateBackup($"{BaseName} 2026-08-24 10-00-00 (3).ubundle");

        Assert.Equal(
            (new DateTime(2026, 8, 24, 10, 0, 0), 3),
            LocalBackupManager.GetBackupIdentity($"{BaseName} 2026-08-24 10-00-00 (3).ubundle", BaseName));
        Assert.Equal(2, LocalBackupManager.ApplyRetentionLimit(_backupDirectory, [BaseName], 1));
        Assert.Equal([$"{BaseName} 2026-08-24 10-00-00 (3).ubundle"], RemainingFiles());
    }

    [Fact]
    public void ParenthesesThatAreNotASequenceAreNotBackupNames()
    {
        Assert.Null(LocalBackupManager.GetBackupIdentity($"{BaseName} 2026-08-24 10-00-00 (1).ubundle", BaseName));
        Assert.Null(LocalBackupManager.GetBackupIdentity($"{BaseName} 2026-08-24 10-00-00 (0).ubundle", BaseName));
        Assert.Null(LocalBackupManager.GetBackupIdentity($"{BaseName} 2026-08-24 10-00-00 (-2).ubundle", BaseName));
        Assert.Null(LocalBackupManager.GetBackupIdentity($"{BaseName} 2026-08-24 10-00-00 (copy).ubundle", BaseName));
        Assert.Null(LocalBackupManager.GetBackupIdentity($"{BaseName} (2).ubundle", BaseName));
    }

    [Fact]
    public async Task BackupsWrittenUnderAnEarlierNameStillCountTowardsTheLimit()
    {
        Settings.SetValue(Settings.K.ChangeBackupOutputDirectory, _backupDirectory);
        Settings.SetValue(Settings.K.ChangeBackupFileName, "Old name");
        Settings.SetValue(Settings.K.MaxLocalBackupCount, "2");
        Settings.Set(Settings.K.EnableBackupTimestamping, true);
        await LocalBackupManager.SaveBackupAsync("old", new DateTime(2026, 8, 1, 10, 0, 0));
        await LocalBackupManager.SaveBackupAsync("old", new DateTime(2026, 8, 2, 10, 0, 0));

        Settings.SetValue(Settings.K.ChangeBackupFileName, "New name");
        await LocalBackupManager.SaveBackupAsync("new", new DateTime(2026, 8, 3, 10, 0, 0));

        Assert.Contains("Old name", LocalBackupManager.ResolveKnownFileNameBases());
        Assert.Contains("New name", LocalBackupManager.ResolveKnownFileNameBases());
        Assert.Equal(1, LocalBackupManager.ApplyRetentionLimit());
        Assert.Equal(
            [
                "New name 2026-08-03 10-00-00.ubundle",
                "Old name 2026-08-02 10-00-00.ubundle",
            ],
            RemainingFiles());
    }

    [Fact]
    public async Task BackupsFromANameThisInstallNeverUsedAreLeftAlone()
    {
        Settings.SetValue(Settings.K.ChangeBackupOutputDirectory, _backupDirectory);
        Settings.SetValue(Settings.K.ChangeBackupFileName, BaseName);
        Settings.SetValue(Settings.K.MaxLocalBackupCount, "1");
        Settings.Set(Settings.K.EnableBackupTimestamping, true);
        CreateTimestampedBackup(new DateTime(2026, 8, 1, 10, 0, 0), "Another computer installed packages");
        CreateTimestampedBackup(new DateTime(2026, 8, 2, 10, 0, 0), "Another computer installed packages");

        await LocalBackupManager.SaveBackupAsync("mine", new DateTime(2026, 8, 3, 10, 0, 0));

        Assert.Equal(0, LocalBackupManager.ApplyRetentionLimit());
        Assert.Equal(3, RemainingFiles().Count);
    }

    [Fact]
    public void TheBackupFileNameOnlyCarriesATimestampWhenTimestampingIsEnabled()
    {
        Settings.SetValue(Settings.K.ChangeBackupFileName, BaseName);

        Settings.Set(Settings.K.EnableBackupTimestamping, false);
        Assert.Equal($"{BaseName}.ubundle", LocalBackupManager.BuildFileName(new DateTime(2026, 8, 24, 15, 30, 45)));

        Settings.Set(Settings.K.EnableBackupTimestamping, true);
        var timestamp = new DateTime(2026, 8, 24, 15, 30, 45);
        string fileName = LocalBackupManager.BuildFileName(timestamp);
        Assert.Equal($"{BaseName} 2026-08-24 15-30-45.ubundle", fileName);
        Assert.Equal(timestamp, GetTimestamp(fileName, BaseName));
    }
}
