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
            try
            {
                string IconsAndScreenshotsFile = Path.Join(
                    CoreData.UniGetUICacheDirectory_Data,
                    "Icon Database.json"
                );
                Uri DownloadUrl = new(
                    "https://github.com/Devolutions/UniGetUI/raw/refs/heads/main/WebBasedData/screenshot-database-v2.json"
                );
                if (Settings.Get(Settings.K.IconDataBaseURL))
                {
                    DownloadUrl = new Uri(Settings.GetValue(Settings.K.IconDataBaseURL));
                }

                using (HttpClient client = new(CoreTools.GenericHttpClientParameters))
                {
                    client.DefaultRequestHeaders.UserAgent.ParseAdd(CoreData.UserAgentString);
                    string fileContents = await client.GetStringAsync(DownloadUrl);
                    await File.WriteAllTextAsync(IconsAndScreenshotsFile, fileContents);
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

        public async Task LoadFromCacheAsync()
        {
            try
            {
                string IconsAndScreenshotsFile = Path.Join(
                    CoreData.UniGetUICacheDirectory_Data,
                    "Icon Database.json"
                );
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
