using System.Text.Json;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;

namespace UniGetUI.Core.SettingsEngine;

public partial class Settings
{
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
                new[]
                {
                    "OperationHistory",
                    "WinGetAlreadyUpgradedPackages.json",
                    "TelemetryClientToken",
                    "CurrentSessionToken",
                }.Contains(Path.GetFileName(entry))
            )
                continue;

            settings.Add(Path.GetFileName(entry), File.ReadAllText(entry));
        }
        return JsonSerializer.Serialize(settings, SerializationOptions);
    }

    public static void ImportFromString_JSON(string jsonContent)
    {
        ResetSettings();
        Dictionary<string, string> settings =
            JsonSerializer.Deserialize<Dictionary<string, string>>(
                jsonContent,
                SerializationOptions
            ) ?? [];
        foreach (KeyValuePair<string, string> entry in settings)
        {
            if (
                new[]
                {
                    "OperationHistory",
                    "WinGetAlreadyUpgradedPackages.json",
                    "TelemetryClientToken",
                    "CurrentSessionToken",
                }.Contains(entry.Key)
            )
                continue;

            File.WriteAllText(
                Path.Join(CoreData.UniGetUIUserConfigurationDirectory, entry.Key),
                entry.Value
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
                if (new[] { "TelemetryClientToken" }.Contains(entry.Split("\\")[^1]))
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
