using System.Collections.Concurrent;
using System.Text.Json;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;

namespace UniGetUI.Core.SettingsEngine
{
    public static partial class Settings
    {
        private static readonly ConcurrentDictionary<string, List<object>> listSettings = new();

        // Returns an empty list if the setting doesn't exist and null if the type is invalid
        private static List<T>? _getList<T>(string setting)
        {
            try
            {
                try
                {
                    if (listSettings.TryGetValue(setting, out List<object>? result))
                    {
                        // If the setting was cached
                        return result.Cast<T>().ToList();
                    }
                }
                catch (InvalidCastException)
                {
                    Logger.Error(
                        $"Tried to get a list setting as type {typeof(T)}, which is not the type of the list"
                    );
                    return null;
                }

                // Otherwise, load the setting from disk and cache that setting
                List<T> value = [];

                var file = Path.Join(
                    CoreData.UniGetUIUserConfigurationDirectory,
                    $"{setting}.json"
                );
                if (File.Exists(file))
                {
                    string result = File.ReadAllText(file);
                    try
                    {
                        if (result != "")
                        {
                            List<T>? item = SettingsJson.DeserializeList<T>(result);
                            if (item is not null)
                            {
                                value = item;
                            }
                        }
                    }
                    catch (InvalidCastException)
                    {
                        Logger.Error(
                            $"Tried to get a list setting as type {typeof(T)}, but the setting on disk {result} cannot be deserialized to {typeof(T)}"
                        );
                    }
                }

                listSettings[setting] = value.Cast<object>().ToList();
                return value;
            }
            catch (Exception ex)
            {
                Logger.Error($"Could not load list {setting} from settings");
                Logger.Error(ex);
                return [];
            }
        }

        // Returns an empty list if the setting doesn't exist and null if the type is invalid
        public static IReadOnlyList<T>? GetList<T>(string setting)
        {
            return _getList<T>(setting);
        }

        public static void SetList<T>(string setting, List<T> value)
        {
            listSettings[setting] = value.Cast<object>().ToList();
            var file = Path.Join(CoreData.UniGetUIUserConfigurationDirectory, $"{setting}.json");
            try
            {
                if (value.Count != 0)
                    File.WriteAllText(file, SettingsJson.SerializeList(value));
                else if (File.Exists(file))
                    File.Delete(file);
            }
            catch (Exception e)
            {
                Logger.Error(
                    $"CANNOT SET SETTING LIST FOR setting={setting} [{string.Join(", ", value)}]"
                );
                Logger.Error(e);
            }
        }

        public static T? GetListItem<T>(string setting, int index)
        {
            List<T>? list = _getList<T>(setting);
            if (list == null)
                return default;
            if (list.Count <= index)
            {
                Logger.Error($"Index {index} out of range for list setting {setting}");
                return default;
            }

            return list.ElementAt(index);
        }

        public static void AddToList<T>(string setting, T value)
        {
            List<T>? list = _getList<T>(setting);
            if (list == null)
                return;

            list.Add(value);
            SetList(setting, list);
        }

        public static bool RemoveFromList<T>(string setting, T value)
        {
            List<T>? list = _getList<T>(setting);
            if (list == null)
                return false;

            bool result = list.Remove(value);
            SetList(setting, list);
            return result;
        }

        public static bool ListContains<T>(string setting, T value)
        {
            List<T>? list = _getList<T>(setting);
            if (list == null)
                return false;
            return list.Contains(value);
        }

        public static void ClearList(string setting)
        {
            SetList<object>(setting, []);
        }
    }
}
