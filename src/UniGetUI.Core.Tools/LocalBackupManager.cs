using System.Globalization;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;

namespace UniGetUI.Core.Tools;

public static class LocalBackupManager
{
    public const string BackupExtension = ".ubundle";
    private const string TimestampFormat = "yyyy-MM-dd HH-mm-ss";
    private const int MaxSequence = 100;

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

    public static IReadOnlyList<string> ResolveKnownFileNameBases()
    {
        List<string> names = [ResolveFileNameBase()];
        var remembered = Settings.GetDictionary<string, bool>(Settings.K.KnownLocalBackupNames);
        if (remembered is not null)
            names.AddRange(remembered.Keys);

        return names
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string BuildFileName(DateTime timestamp)
    {
        string fileName = ResolveFileNameBase();
        if (Settings.Get(Settings.K.EnableBackupTimestamping))
            fileName += " " + timestamp.ToString(TimestampFormat, CultureInfo.InvariantCulture);

        return fileName + BackupExtension;
    }

    public static Task<string> SaveBackupAsync(string contents)
        => SaveBackupAsync(contents, DateTime.Now);

    public static async Task<string> SaveBackupAsync(string contents, DateTime timestamp)
    {
        string directory = ResolveOutputDirectory();
        Directory.CreateDirectory(directory);
        RememberFileNameBase();

        string fileName = BuildFileName(timestamp);
        if (!Settings.Get(Settings.K.EnableBackupTimestamping))
        {
            string singleFilePath = Path.Combine(directory, fileName);
            await File.WriteAllTextAsync(singleFilePath, contents);
            return singleFilePath;
        }

        string stem = fileName[..^BackupExtension.Length];
        for (int sequence = 1; sequence <= MaxSequence; sequence++)
        {
            string path = Path.Combine(
                directory,
                sequence == 1
                    ? stem + BackupExtension
                    : $"{stem} ({sequence}){BackupExtension}"
            );

            FileStream stream;
            try
            {
                stream = new FileStream(
                    path,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    useAsync: true
                );
            }
            catch (IOException) when (File.Exists(path))
            {
                continue;
            }

            try
            {
                await using (StreamWriter writer = new(stream))
                {
                    await writer.WriteAsync(contents);
                }

                return path;
            }
            catch
            {
                DeleteIncompleteBackup(path);
                throw;
            }
        }

        throw new IOException(
            $"No unused backup file name was available for \"{stem}\" in {directory}"
        );
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
                ResolveKnownFileNameBases(),
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

    public static int ApplyRetentionLimit(
        string directory,
        IReadOnlyCollection<string> fileNameBases,
        int keepCount)
    {
        if (keepCount <= 0 || fileNameBases.Count == 0 || !Directory.Exists(directory))
            return 0;

        List<(DateTime Timestamp, int Sequence, string Path)> backups = [];
        foreach (string path in Directory.EnumerateFiles(
            directory,
            "*" + BackupExtension,
            SearchOption.TopDirectoryOnly))
        {
            string fileName = Path.GetFileName(path);
            foreach (string fileNameBase in fileNameBases)
            {
                if (GetBackupIdentity(fileName, fileNameBase) is { } identity)
                {
                    backups.Add((
                        identity.Timestamp ?? File.GetLastWriteTime(path),
                        identity.Sequence,
                        path
                    ));
                    break;
                }
            }
        }

        int deletedCount = 0;
        foreach (var backup in backups
            .OrderByDescending(backup => backup.Timestamp)
            .ThenByDescending(backup => backup.Sequence)
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

    public static (DateTime? Timestamp, int Sequence)? GetBackupIdentity(
        string fileName,
        string fileNameBase)
    {
        if (!fileName.EndsWith(BackupExtension, StringComparison.OrdinalIgnoreCase))
            return null;

        string prefix = fileNameBase + " ";
        string name = fileName[..^BackupExtension.Length];
        if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        string value = name[prefix.Length..];
        int sequence = 1;
        if (value.EndsWith(')'))
        {
            int separator = value.LastIndexOf(" (", StringComparison.Ordinal);
            if (separator < 0
                || !int.TryParse(
                    value[(separator + 2)..^1],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out sequence)
                || sequence < 2)
                return null;

            value = value[..separator];
        }

        if (!HasTimestampShape(value))
            return null;

        if (TryParseTimestamp(value, CultureInfo.InvariantCulture, out DateTime timestamp)
            || TryParseTimestamp(value, CultureInfo.CurrentCulture, out timestamp))
            return (timestamp, sequence);

        return (null, sequence);
    }

    private static bool HasTimestampShape(string value)
    {
        const string shape = "dddd-dd-dd dd-dd-dd";
        if (value.Length != shape.Length)
            return false;

        for (int index = 0; index < shape.Length; index++)
        {
            bool matches = shape[index] == 'd'
                ? char.IsAsciiDigit(value[index])
                : value[index] == shape[index];
            if (!matches)
                return false;
        }

        return true;
    }

    private static void DeleteIncompleteBackup(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            Logger.Warn($"The incomplete local backup {path} could not be removed:");
            Logger.Warn(ex);
        }
    }

    private static void RememberFileNameBase()
    {
        string fileNameBase = ResolveFileNameBase();
        if (string.IsNullOrWhiteSpace(fileNameBase)
            || Settings.DictionaryContainsKey<string, bool>(
                Settings.K.KnownLocalBackupNames,
                fileNameBase))
            return;

        Settings.SetDictionaryItem(Settings.K.KnownLocalBackupNames, fileNameBase, true);
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
