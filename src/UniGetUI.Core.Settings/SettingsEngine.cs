using System.Collections.Concurrent;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;

namespace UniGetUI.Core.SettingsEngine
{
    public static partial class Settings
    {
        private static readonly ConcurrentDictionary<K, bool> booleanSettings = new();
        private static readonly ConcurrentDictionary<K, string> valueSettings = new();

        public static bool Get(K key, bool invert = false)
        {
            string setting = ResolveKey(key);
            if (booleanSettings.TryGetValue(key, out bool result))
            { // If the setting was cached
                return result ^ invert;
            }

            // Otherwise, load the value from disk and cache that setting
            result = File.Exists(Path.Join(CoreData.UniGetUIUserConfigurationDirectory, setting));
            booleanSettings[key] = result;
            return result ^ invert;
        }

        public static void Set(K key, bool value)
        {
            string setting = ResolveKey(key);
            try
            {
                // Cache that setting's new value
                booleanSettings[key] = value;

                // Update changes on disk if applicable
                if (
                    value
                    && !File.Exists(Path.Join(CoreData.UniGetUIUserConfigurationDirectory, setting))
                )
                {
                    File.WriteAllText(
                        Path.Join(CoreData.UniGetUIUserConfigurationDirectory, setting),
                        ""
                    );
                }
                else if (!value)
                {
                    valueSettings[key] = "";

                    if (
                        File.Exists(Path.Join(CoreData.UniGetUIUserConfigurationDirectory, setting))
                    )
                    {
                        File.Delete(
                            Path.Join(CoreData.UniGetUIUserConfigurationDirectory, setting)
                        );
                    }
                }
            }
            catch (Exception e)
            {
                Logger.Error($"CANNOT SET SETTING FOR setting={setting} enabled={value}");
                Logger.Error(e);
            }
        }

        public static string GetValue(K key)
        {
            string setting = ResolveKey(key);
            if (valueSettings.TryGetValue(key, out string? value))
            { // If the setting was cached
                return value;
            }

            // Otherwise, load the setting from disk and cache that setting
            value = "";
            if (File.Exists(Path.Join(CoreData.UniGetUIUserConfigurationDirectory, setting)))
            {
                value = File.ReadAllText(
                    Path.Join(CoreData.UniGetUIUserConfigurationDirectory, setting)
                );
            }

            valueSettings[key] = value;
            return value;
        }

        public static void SetValue(K key, string value)
        {
            string setting = ResolveKey(key);
            try
            {
                if (value == String.Empty)
                {
                    Set(key, false);
                    booleanSettings[key] = false;
                    valueSettings[key] = "";
                }
                else
                {
                    File.WriteAllText(
                        Path.Join(CoreData.UniGetUIUserConfigurationDirectory, setting),
                        value
                    );
                    booleanSettings[key] = true;
                    valueSettings[key] = value;
                }
            }
            catch (Exception e)
            {
                Logger.Error($"CANNOT SET SETTING VALUE FOR setting={setting} enabled={value}");
                Logger.Error(e);
            }
        }

        /// <summary>Persists a value and reports failure without changing the cached setting.</summary>
        public static bool TrySetValue(K key, string value)
        {
            string setting = ResolveKey(key);
            try
            {
                string path = Path.Join(CoreData.UniGetUIUserConfigurationDirectory, setting);
                if (value == String.Empty)
                    File.Delete(path);
                else
                    File.WriteAllText(path, value);

                booleanSettings[key] = value != String.Empty;
                valueSettings[key] = value;
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Logger.Error($"CANNOT SET SETTING VALUE FOR setting={setting}");
                Logger.Error(e);
                return false;
            }
        }
    }
}
