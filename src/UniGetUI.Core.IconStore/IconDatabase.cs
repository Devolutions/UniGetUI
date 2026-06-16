using System.Text.Json;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;

namespace UniGetUI.Core.IconEngine
{
    /// <summary>
    /// This class represents the structure of the icon and screenshot database. It is used to deserialize the JSON data.
    /// </summary>
    public class IconDatabase
    {
        private const string ICON_DATABASE_FILE_NAME = "Icon Database.json";
        private static readonly TimeSpan ICON_DATABASE_REFRESH_INTERVAL = TimeSpan.FromDays(1);

        public struct IconCount
        {
            public int PackagesWithIconCount = 0;
            public int TotalScreenshotCount = 0;
            public int PackagesWithScreenshotCount = 0;

            public IconCount() { }
        }

        private static IconDatabase? __instance;
        public static IconDatabase Instance
        {
            get => __instance ??= new();
        }

        /// <summary>
        /// The icon and screenshot database
        /// </summary>
        private Dictionary<
            string,
            IconScreenshotDatabase_v2.PackageIconAndScreenshots
        > IconDatabaseData = [];
        private IconCount __icon_count = new();

        /// <summary>
        /// Download the icon and screenshots database to a local file, and load it into memory
        /// </summary>
        public async Task LoadIconAndScreenshotsDatabaseAsync()
        {
            string IconsAndScreenshotsFile = GetIconsAndScreenshotsFile();
            bool hasCustomDownloadUrl = Settings.Get(Settings.K.IconDataBaseURL);
            if (
                !hasCustomDownloadUrl
                && IsCachedDatabaseFresh(IconsAndScreenshotsFile, DateTime.UtcNow)
            )
            {
                Logger.Debug("Using cached icons and screenshots database; refresh is not due yet");
                await LoadFromCacheAsync();
                if (__icon_count.PackagesWithIconCount > 0)
                {
                    return;
                }

                Logger.Warn(
                    "Cached icons and screenshots database could not be loaded; refreshing it"
                );
            }

            try
            {
                Uri DownloadUrl = new(
                    "https://github.com/Devolutions/UniGetUI/raw/refs/heads/main/WebBasedData/screenshot-database-v2.json"
                );
                if (hasCustomDownloadUrl)
                {
                    DownloadUrl = new Uri(Settings.GetValue(Settings.K.IconDataBaseURL));
                }

                using (HttpClient client = new(CoreTools.GenericHttpClientParameters))
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
                    string fileContents = await client.GetStringAsync(DownloadUrl);
                    await WriteCacheFileAsync(IconsAndScreenshotsFile, fileContents);
                }

                Logger.ImportantInfo("Downloaded new icons and screenshots successfully!");

                if (!File.Exists(IconsAndScreenshotsFile))
                {
                    Logger.Error("Icon Database file not found");
                    return;
                }
            }
            catch (Exception e)
            {
                Logger.Warn("Failed to download icons and screenshots");
                Logger.Warn(e);
            }

            // Update data with new cached file
            await LoadFromCacheAsync();
        }

        internal static bool IsCachedDatabaseFresh(string path, DateTime utcNow)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            DateTime lastWriteTimeUtc = File.GetLastWriteTimeUtc(path);
            return utcNow - lastWriteTimeUtc < ICON_DATABASE_REFRESH_INTERVAL;
        }

        private static string GetIconsAndScreenshotsFile()
        {
            return Path.Join(CoreData.UniGetUICacheDirectory_Data, ICON_DATABASE_FILE_NAME);
        }

        private static async Task WriteCacheFileAsync(string path, string contents)
        {
            string temporaryPath = path + ".tmp";
            await File.WriteAllTextAsync(temporaryPath, contents);
            File.Move(temporaryPath, path, overwrite: true);
        }

        public async Task LoadFromCacheAsync()
        {
            try
            {
                string IconsAndScreenshotsFile = GetIconsAndScreenshotsFile();
                IconScreenshotDatabase_v2 JsonData =
                    IconStoreJson.DeserializeIconDatabase(
                        await File.ReadAllTextAsync(IconsAndScreenshotsFile)
                    );
                if (JsonData.icons_and_screenshots is not null)
                {
                    IconDatabaseData = JsonData.icons_and_screenshots;
                }

                __icon_count = new IconCount
                {
                    PackagesWithIconCount = JsonData.package_count.packages_with_icon,
                    PackagesWithScreenshotCount = JsonData.package_count.packages_with_screenshot,
                    TotalScreenshotCount = JsonData.package_count.total_screenshots,
                };
            }
            catch (Exception ex)
            {
                Logger.Error("Failed to load icon and screenshot database");
                Logger.Error(ex);
            }
        }

        public string? GetIconUrlForId(string id)
        {
            if (IconDatabaseData.TryGetValue(id, out var value) && value.icon.Length != 0)
            {
                return value.icon;
            }

            return null;
        }

        public string[] GetScreenshotsUrlForId(string id)
        {
            return IconDatabaseData.TryGetValue(id, out var value) ? value.images.ToArray() : [];
        }

        public IconCount GetIconCount()
        {
            return __icon_count;
        }
    }
}
