using System.Collections.Concurrent;
using System.Text.Json;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;

namespace UniGetUI.Core.SettingsEngine
{
    public static partial class Settings
    {
        private static readonly ConcurrentDictionary<
            K,
            Dictionary<object, object?>
        > _dictionarySettings = new();

        // Returns an empty dictionary if the setting doesn't exist and null if the types are invalid
        private static Dictionary<KeyT, ValueT?>? _getDictionary<KeyT, ValueT>(K key)
            where KeyT : notnull
        {
            string setting = ResolveKey(key);
            try
            {
                try
                {
                    if (
                        _dictionarySettings.TryGetValue(
                            key,
                            out Dictionary<object, object?>? result
                        )
                    )
                    {
                        // If the setting was cached
                        return result.ToDictionary(kvp => (KeyT)kvp.Key, kvp => (ValueT?)kvp.Value);
                    }
                }
                catch (InvalidCastException)
                {
                    Logger.Error(
                        $"Tried to get a dictionary setting with a key of type {typeof(KeyT)} and a value of type {typeof(ValueT)}, which is not the type of the dictionary"
                    );
                    return null;
                }

                // Otherwise, load the setting from disk and cache that setting
                Dictionary<KeyT, ValueT?> value = [];
                if (
                    File.Exists(
                        Path.Join(CoreData.UniGetUIUserConfigurationDirectory, $"{setting}.json")
                    )
                )
                {
                    string result = File.ReadAllText(
                        Path.Join(CoreData.UniGetUIUserConfigurationDirectory, $"{setting}.json")
                    );
                    try
                    {
                        if (result != "")
                        {
                            Dictionary<KeyT, ValueT?>? item =
                                SettingsJson.DeserializeDictionary<KeyT, ValueT?>(result);
                            if (item is not null)
                            {
                                value = item;
                            }
                        }
                    }
                    catch (InvalidCastException)
                    {
                        Logger.Error(
                            $"Tried to get a dictionary setting with a key of type {typeof(KeyT)} and a value of type {typeof(ValueT)}, but the setting on disk ({result}) cannot be deserialized to that"
                        );
                    }
                }

                _dictionarySettings[key] = value.ToDictionary(
                    kvp => (object)kvp.Key,
                    kvp => (object?)kvp.Value
                );
                return value;
            }
            catch (Exception ex)
            {
                Logger.Error($"Could not load dictionary name {setting}");
                Logger.Error(ex);
                return [];
            }
        }

        // Returns an empty dictionary if the setting doesn't exist and null if the types are invalid
        public static IReadOnlyDictionary<KeyT, ValueT?>? GetDictionary<KeyT, ValueT>(K settingsKey)
            where KeyT : notnull
        {
            return _getDictionary<KeyT, ValueT?>(settingsKey);
        }

        public static void SetDictionary<KeyT, ValueT>(
            K settingsKey,
            Dictionary<KeyT, ValueT> value
        )
            where KeyT : notnull
        {
            string setting = ResolveKey(settingsKey);
            _dictionarySettings[settingsKey] = value.ToDictionary(
                kvp => (object)kvp.Key,
                kvp => (object?)kvp.Value
            );

            var file = Path.Join(CoreData.UniGetUIUserConfigurationDirectory, $"{setting}.json");
            try
            {
                if (value.Count != 0)
                    File.WriteAllText(file, SettingsJson.SerializeDictionary(value));
                else if (File.Exists(file))
                    File.Delete(file);
            }
            catch (Exception e)
            {
                Logger.Error(
                    $"CANNOT SET SETTING DICTIONARY FOR setting={setting} [{string.Join(", ", value)}]"
                );
                Logger.Error(e);
            }
        }

        public static ValueT? GetDictionaryItem<KeyT, ValueT>(K settingsKey, KeyT key)
            where KeyT : notnull
        {
            Dictionary<KeyT, ValueT?>? dictionary = _getDictionary<KeyT, ValueT>(settingsKey);
            if (dictionary == null || !dictionary.TryGetValue(key, out ValueT? value))
                return default;

            return value;
        }

        // Also works as `Add`
        public static ValueT? SetDictionaryItem<KeyT, ValueT>(K settingsKey, KeyT key, ValueT value)
            where KeyT : notnull
        {
            Dictionary<KeyT, ValueT?>? dictionary = _getDictionary<KeyT, ValueT>(settingsKey);
            if (dictionary == null)
            {
                dictionary = new() { { key, value } };
                SetDictionary(settingsKey, dictionary);
                return default;
            }

            if (dictionary.TryGetValue(key, out ValueT? oldValue))
            {
                dictionary[key] = value;
                SetDictionary(settingsKey, dictionary);
                return oldValue;
            }

            dictionary.Add(key, value);
            SetDictionary(settingsKey, dictionary);
            return default;
        }

        public static ValueT? RemoveDictionaryKey<KeyT, ValueT>(K settingsKey, KeyT key)
            where KeyT : notnull
        {
            Dictionary<KeyT, ValueT?>? dictionary = _getDictionary<KeyT, ValueT>(settingsKey);
            if (dictionary == null)
                return default;

            bool success = false;
            if (dictionary.TryGetValue(key, out ValueT? value))
            {
                success = dictionary.Remove(key);
                SetDictionary(settingsKey, dictionary);
            }

            if (!success)
                return default;
            return value;
        }

        public static bool DictionaryContainsKey<KeyT, ValueT>(K settingsKey, KeyT key)
            where KeyT : notnull
        {
            Dictionary<KeyT, ValueT?>? dictionary = _getDictionary<KeyT, ValueT>(settingsKey);
            if (dictionary == null)
                return false;

            return dictionary.ContainsKey(key);
        }

        public static bool DictionaryContainsValue<KeyT, ValueT>(K settingsKey, ValueT value)
            where KeyT : notnull
        {
            Dictionary<KeyT, ValueT?>? dictionary = _getDictionary<KeyT, ValueT>(settingsKey);
            if (dictionary == null)
                return false;

            return dictionary.ContainsValue(value);
        }

        public static void ClearDictionary(K settingsKey)
        {
            SetDictionary(settingsKey, new Dictionary<object, object>());
        }
    }
}
