using System.Text.Json;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;

namespace UniGetUI.Core.SettingsEngine;

public partial class Settings
{
    private static readonly string[] _nonPortableSettings =
    [
        "OperationHistory",
        "OperationHistory.json",
        "WinGetAlreadyUpgradedPackages.json",
        "WinGetUpgradeAttempts.json",
        "MaintenanceTaskLastRun.json",
        "MaintenanceTaskLastFailure.json",
        "MaintenanceSchedules.invalid",
        "KnownLocalBackupNames.json",
        "TelemetryClientToken",
        "CurrentSessionToken",
        "PendingDesktopShortcuts.json",
        "PendingStartMenuShortcuts.json",
    ];

    private static readonly StringComparison _pathComparison = OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    private static readonly char[] _rejectedKeyChars = ['/', '\\', ':', '~'];

    private static readonly Lazy<HashSet<string>> _importableSettingNames = new(() =>
    {
        HashSet<string> names = new(StringComparer.Ordinal);
        foreach (K key in Enum.GetValues<K>())
        {
            if (key is K.Unset)
                continue;

            string resolved = ResolveKey(key);
            names.Add(resolved);
            names.Add($"{resolved}.json");
        }

        return names;
    });

    private static readonly string[] _reservedDeviceNames =
    [
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM0",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT0",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9",
    ];

    public static void ExportToFile_JSON(string path)
    {
        File.WriteAllText(path, ExportToString_JSON());
    }

    public static void ImportFromFile_JSON(string path)
    {
        if (Path.GetDirectoryName(path) == CoreData.UniGetUIUserConfigurationDirectory)
        {
            var tempLocation = Directory.CreateTempSubdirectory();
            var newPath = Path.Join(tempLocation.FullName, Path.GetFileName(path));
            File.Copy(path, newPath);
            path = newPath;
        }
        ImportFromString_JSON(File.ReadAllText(path));
    }

    public static string ExportToString_JSON()
    {
        Dictionary<string, string> settings = [];
        foreach (
            string entry in Directory.EnumerateFiles(CoreData.UniGetUIUserConfigurationDirectory)
        )
        {
            if (
                _nonPortableSettings.Contains(
                    Path.GetFileName(entry),
                    StringComparer.OrdinalIgnoreCase
                )
            )
                continue;

            settings.Add(Path.GetFileName(entry), File.ReadAllText(entry));
        }
        return SettingsJson.SerializeStringDictionary(settings);
    }

    private static bool TryResolveImportedSettingPath(string key, out string resolvedPath)
    {
        resolvedPath = string.Empty;

        if (string.IsNullOrWhiteSpace(key))
            return false;

        if (key is "." or "..")
            return false;

        if (key.IndexOfAny(_rejectedKeyChars) >= 0)
            return false;

        if (!key.Equals(Path.GetFileName(key), StringComparison.Ordinal))
            return false;

        if (key.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return false;

        if (_nonPortableSettings.Contains(key, StringComparer.OrdinalIgnoreCase))
            return false;

        if (!_importableSettingNames.Value.Contains(key))
            return false;

        if (
            OperatingSystem.IsWindows()
            && _reservedDeviceNames.Contains(
                Path.GetFileNameWithoutExtension(key),
                StringComparer.OrdinalIgnoreCase
            )
        )
            return false;

        string configurationDirectory = Path.GetFullPath(
            CoreData.UniGetUIUserConfigurationDirectory
        );
        string candidate = Path.GetFullPath(Path.Join(configurationDirectory, key));

        if (
            !string.Equals(
                Path.GetDirectoryName(candidate),
                configurationDirectory.TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar
                ),
                _pathComparison
            )
        )
            return false;

        if (!key.Equals(Path.GetFileName(candidate), StringComparison.Ordinal))
            return false;

        resolvedPath = candidate;
        return true;
    }

    public static void ImportFromString_JSON(string jsonContent)
    {
        Dictionary<string, string> settings =
            SettingsJson.DeserializeStringDictionary(jsonContent)
            ?? throw new JsonException("The settings document must contain a JSON object.");

        ResetSettings();
        int failedWrites = 0;
        foreach (KeyValuePair<string, string> entry in settings)
        {
            if (!TryResolveImportedSettingPath(entry.Key, out string destination))
            {
                Logger.Warn(
                    $"Discarded imported setting with an unsafe or non-portable key: '{entry.Key}'"
                );
                continue;
            }

            try
            {
                File.WriteAllText(destination, entry.Value);
            }
            catch (Exception ex)
            {
                failedWrites++;
                Logger.Error($"Could not import the setting '{entry.Key}'");
                Logger.Error(ex);
            }
        }

        if (failedWrites > 0)
        {
            throw new IOException(
                $"{failedWrites} setting(s) could not be written. The previous configuration was "
                    + "already cleared, so the imported configuration is incomplete."
            );
        }

        Logger.Info("Settings successfully imported from string content.");
    }

    public static void ResetSettings()
    {
        booleanSettings.Clear();
        valueSettings.Clear();
        listSettings.Clear();
        _dictionarySettings.Clear();

        foreach (
            string entry in Directory.EnumerateFiles(CoreData.UniGetUIUserConfigurationDirectory)
        )
        {
            try
            {
                if (
                    string.Equals(
                        Path.GetFileName(entry),
                        "TelemetryClientToken",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
                    continue;

                File.Delete(entry);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex);
            }
        }
    }
}
