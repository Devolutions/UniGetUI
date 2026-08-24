using System.Globalization;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;

namespace UniGetUI.Core.Tools;

public static class LocalBackupManager
{
    public const string BackupExtension = ".ubundle";
    private const string TimestampFormat = "yyyy-MM-dd HH-mm-ss";

    public static string ResolveOutputDirectory()
    {
        string directory = Settings.GetValue(Settings.K.ChangeBackupOutputDirectory);
        return string.IsNullOrWhiteSpace(directory)
            ? CoreData.UniGetUI_DefaultBackupDirectory
            : directory;
    }

    public static string ResolveFileNameBase()
    {
        string fileName = Settings.GetValue(Settings.K.ChangeBackupFileName);
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = CoreTools.Translate(
                "{pcName} installed packages",
                new Dictionary<string, object?> { { "pcName", Environment.MachineName } }
            );

        return fileName;
    }

    public static string BuildFileName() => BuildFileName(DateTime.Now);

    public static string BuildFileName(DateTime timestamp)
    {
        string fileName = ResolveFileNameBase();
        if (Settings.Get(Settings.K.EnableBackupTimestamping))
            fileName += " " + timestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture);

        return fileName + BackupExtension;
    }

    public static int GetRetentionLimit()
    {
        string value = Settings.GetValue(Settings.K.MaxLocalBackupCount);
        if (value == "custom")
            value = Settings.GetValue(Settings.K.MaxLocalBackupCountCustom);

        return int.TryParse(value, CultureInfo.InvariantCulture, out int limit) && limit > 0
            ? limit
            : 0;
    }

    public static int ApplyRetentionLimit()
    {
        try
        {
            if (!Settings.Get(Settings.K.EnableBackupTimestamping))
                return 0;

            return ApplyRetentionLimit(
                ResolveOutputDirectory(),
                ResolveFileNameBase(),
                GetRetentionLimit()
            );
        }
        catch (Exception ex)
        {
            Logger.Error("An error occurred while applying the local backup retention limit:");
            Logger.Error(ex);
            return 0;
        }
    }

    public static int ApplyRetentionLimit(string directory, string fileNameBase, int keepCount)
    {
        if (keepCount <= 0 || string.IsNullOrWhiteSpace(fileNameBase) || !Directory.Exists(directory))
            return 0;

        List<(DateTime Timestamp, string Path)> backups = [];
        foreach (string path in Directory.EnumerateFiles(
            directory,
            "*" + BackupExtension,
            SearchOption.TopDirectoryOnly))
        {
            if (GetBackupTimestamp(Path.GetFileName(path), fileNameBase) is { } timestamp)
                backups.Add((timestamp, path));
        }

        int deletedCount = 0;
        foreach (var backup in backups
            .OrderByDescending(backup => backup.Timestamp)
            .ThenByDescending(backup => backup.Path, StringComparer.OrdinalIgnoreCase)
            .Skip(keepCount))
        {
            try
            {
                File.Delete(backup.Path);
                deletedCount++;
                Logger.Info(
                    $"Deleted the old local backup {backup.Path}, only the {keepCount} most recent"
                    + " backups are kept"
                );
            }
            catch (Exception ex)
            {
                Logger.Warn($"The old local backup {backup.Path} could not be deleted:");
                Logger.Warn(ex);
            }
        }

        return deletedCount;
    }

    public static DateTime? GetBackupTimestamp(string fileName, string fileNameBase)
    {
        if (!fileName.EndsWith(BackupExtension, StringComparison.OrdinalIgnoreCase))
            return null;

        string prefix = fileNameBase + " ";
        string name = fileName[..^BackupExtension.Length];
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        string value = name[prefix.Length..];
        if (TryParseTimestamp(value, CultureInfo.InvariantCulture, out DateTime timestamp))
            return timestamp;

        return TryParseTimestamp(value, CultureInfo.CurrentCulture, out timestamp)
            ? timestamp
            : null;
    }

    private static bool TryParseTimestamp(string value, CultureInfo culture, out DateTime timestamp)
        => DateTime.TryParseExact(
            value,
            TimestampFormat,
            culture,
            DateTimeStyles.None,
            out timestamp
        ) && timestamp.Year >= 2000 && timestamp <= DateTime.Now.AddYears(1);
}
