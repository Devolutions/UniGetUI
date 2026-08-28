using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.Classes.Packages.Classes;

public static class AutoUpdatesDatabase
{
    public const string AllVersions = "*";

    public static IReadOnlyDictionary<string, string> GetDatabase()
    {
        return Settings
                .GetDictionary<string, string>(Settings.K.AutoUpdatedPackages)
                ?.Where(kvp => kvp.Value is not null)
                .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!)
            ?? new Dictionary<string, string>();
    }

    public static string GetIdForPackage(IPackage package)
    {
        return IgnoredUpdatesDatabase.GetIgnoredIdForPackage(package);
    }

    public static void Add(string autoUpdateId)
    {
        Settings.SetDictionaryItem(Settings.K.AutoUpdatedPackages, autoUpdateId, AllVersions);
    }

    public static bool Remove(string autoUpdateId)
    {
        if (
            Settings.DictionaryContainsKey<string, string>(
                Settings.K.AutoUpdatedPackages,
                autoUpdateId
            )
        )
        {
            return Settings.RemoveDictionaryKey<string, string>(
                    Settings.K.AutoUpdatedPackages,
                    autoUpdateId
                ) != null;
        }

        Logger.Warn(
            $"Attempted to remove from automatic updates a package {{autoUpdateId={autoUpdateId}}} that was not found there"
        );
        return false;
    }

    public static bool IsAutoUpdated(string autoUpdateId)
    {
        return Settings.GetDictionaryItem<string, string>(
            Settings.K.AutoUpdatedPackages,
            autoUpdateId
        ) is not null;
    }

    public static bool IsAutoUpdated(IPackage package) => IsAutoUpdated(GetIdForPackage(package));

    public static void AddRange(IEnumerable<string> autoUpdateIds)
    {
        var updated = GetDatabase().ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        foreach (string id in autoUpdateIds)
            updated[id] = AllVersions;

        Settings.SetDictionary(Settings.K.AutoUpdatedPackages, updated);
    }

    public static void RemoveRange(IEnumerable<string> autoUpdateIds)
    {
        var updated = GetDatabase().ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        foreach (string id in autoUpdateIds)
            updated.Remove(id);

        Settings.SetDictionary(Settings.K.AutoUpdatedPackages, updated);
    }

    public static int Count => GetDatabase().Count;

    public static void Clear() => Settings.ClearDictionary(Settings.K.AutoUpdatedPackages);
}
