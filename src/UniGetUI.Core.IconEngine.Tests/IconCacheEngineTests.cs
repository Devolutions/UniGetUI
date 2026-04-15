using UniGetUI.Core.Data;

namespace UniGetUI.Core.IconEngine.Tests
{
    public static class IconCacheEngineTests
    {
        private const string UniGetUiIconUrl =
            "https://raw.githubusercontent.com/Devolutions/UniGetUI/main/src/UniGetUI.Core.IconEngine.Tests/TestData/unigetui.png";

        private const string ElevenClockIconUrl =
            "https://raw.githubusercontent.com/Devolutions/UniGetUI/main/src/UniGetUI.Core.IconEngine.Tests/TestData/elevenclock.png";

        [Fact]
        public static void TestCacheEngineForSha256()
        {
            Uri ICON_1 = new Uri(UniGetUiIconUrl);
            byte[] HASH_1 =
            [
                0xB7,
                0x41,
                0xC3,
                0x18,
                0xBF,
                0x2B,
                0x07,
                0xAA,
                0x92,
                0xB2,
                0x7A,
                0x1B,
                0x4D,
                0xC5,
                0xEE,
                0xC4,
                0xD1,
                0x9B,
                0x22,
                0xD4,
                0x0A,
                0x13,
                0x26,
                0xA7,
                0x45,
                0xA4,
                0xA7,
                0xF5,
                0x81,
                0x8E,
                0xAF,
                0xFF,
            ];
            Uri ICON_2 = new Uri(ElevenClockIconUrl);
            byte[] HASH_2 =
            [
                0x9E,
                0xB8,
                0x7A,
                0x5A,
                0x64,
                0xCA,
                0x6D,
                0x8D,
                0x0A,
                0x7B,
                0x98,
                0xC5,
                0x4F,
                0x6A,
                0x58,
                0x72,
                0xFD,
                0x94,
                0xC9,
                0xA6,
                0x82,
                0xB3,
                0x2B,
                0x90,
                0x70,
                0x66,
                0x66,
                0x1C,
                0xBF,
                0x81,
                0x97,
                0x97,
            ];

            string managerName = "TestManager";
            string packageId = "Package55";

            string extension = ICON_1.ToString().Split(".")[^1];
            string expectedFile = Path.Join(
                CoreData.UniGetUICacheDirectory_Icons,
                managerName,
                packageId,
                $"icon.{extension}"
            );
            if (File.Exists(expectedFile))
            {
                File.Delete(expectedFile);
            }

            // Download a hashed icon
            CacheableIcon icon = new(ICON_1, HASH_1);
            string? path = IconCacheEngine.GetCacheOrDownloadIcon(icon, managerName, packageId, 0);
            Assert.NotNull(path);
            Assert.Equal(expectedFile, path);
            Assert.True(File.Exists(path));

            DateTime oldModificationDate = File.GetLastWriteTime(path);

            // Test the same icon, modification date shouldn't change
            icon = new CacheableIcon(ICON_1, HASH_1);
            path = IconCacheEngine.GetCacheOrDownloadIcon(icon, managerName, packageId, 0);
            Assert.NotNull(path);
            DateTime newModificationDate = File.GetLastWriteTime(path);

            Assert.Equal(oldModificationDate, newModificationDate);
            Assert.Equal(expectedFile, path);
            Assert.True(File.Exists(path));

            // Attempt to retrieve a different icon. The modification date SHOULD have changed
            icon = new CacheableIcon(ICON_2, HASH_2);
            path = IconCacheEngine.GetCacheOrDownloadIcon(icon, managerName, packageId, 0);
            Assert.NotNull(path);
            DateTime newIconModificationDate = File.GetLastWriteTime(path);

            Assert.NotEqual(oldModificationDate, newIconModificationDate);
            Assert.Equal(expectedFile, path);
            Assert.True(File.Exists(path));

            // Give an invalid hash: The icon should not be cached not returned
            icon = new CacheableIcon(ICON_2, HASH_1);
            path = IconCacheEngine.GetCacheOrDownloadIcon(icon, managerName, packageId, 0);
            Assert.Null(path);
            Assert.False(File.Exists(path));
        }

        [Fact]
        public static void TestCacheEngineForPackageVersion()
        {
            Uri URI = new Uri(UniGetUiIconUrl);
            string VERSION = "v3.01";
            string MANAGER_NAME = "TestManager";
            string PACKAGE_ID = "Package2";

            string extension = URI.ToString().Split(".")[^1];
            string expectedFile = Path.Join(
                CoreData.UniGetUICacheDirectory_Icons,
                MANAGER_NAME,
                PACKAGE_ID,
                $"icon.{extension}"
            );
            if (File.Exists(expectedFile))
            {
                File.Delete(expectedFile);
            }

            // Download an icon through version verification
            CacheableIcon icon = new(URI, VERSION);
            string? path = IconCacheEngine.GetCacheOrDownloadIcon(
                icon,
                MANAGER_NAME,
                PACKAGE_ID,
                0
            );
            Assert.NotNull(path);
            Assert.Equal(expectedFile, path);
            Assert.True(File.Exists(path));

            DateTime oldModificationDate = File.GetLastWriteTime(path);

            // Test the same version, the icon should not get touched
            icon = new CacheableIcon(URI, VERSION);
            path = IconCacheEngine.GetCacheOrDownloadIcon(icon, MANAGER_NAME, PACKAGE_ID, 0);
            Assert.NotNull(path);
            DateTime newModificationDate = File.GetLastWriteTime(path);

            Assert.Equal(oldModificationDate, newModificationDate);
            Assert.Equal(expectedFile, path);
            Assert.True(File.Exists(path));

            // Test a new version, the icon should be downloaded again
            icon = new CacheableIcon(URI, VERSION + "-beta0");
            path = IconCacheEngine.GetCacheOrDownloadIcon(icon, MANAGER_NAME, PACKAGE_ID, 0);
            Assert.NotNull(path);
            DateTime newNewModificationDate = File.GetLastWriteTime(path);

            Assert.Equal(oldModificationDate, newNewModificationDate);
            Assert.Equal(expectedFile, path);
            Assert.True(File.Exists(path));
        }

        [Fact]
        public static void TestCacheEngineForIconUri()
        {
            Uri URI_1 = new Uri(UniGetUiIconUrl);
            Uri URI_2 = new Uri(ElevenClockIconUrl);
            string managerName = "TestManager";
            string packageId = "Package12";

            string extension = URI_1.ToString().Split(".")[^1];
            string expectedFile = Path.Join(
                CoreData.UniGetUICacheDirectory_Icons,
                managerName,
                packageId,
                $"icon.{extension}"
            );
            if (File.Exists(expectedFile))
            {
                File.Delete(expectedFile);
            }

            // Download an icon through URI verification
            CacheableIcon icon = new(URI_1);
            string? path = IconCacheEngine.GetCacheOrDownloadIcon(icon, managerName, packageId, 0);
            Assert.NotNull(path);
            Assert.Equal(expectedFile, path);
            Assert.True(File.Exists(path));

            DateTime oldModificationDate = File.GetLastWriteTime(path);

            // Test the same URI, the icon should not get touched
            icon = new CacheableIcon(URI_1);
            path = IconCacheEngine.GetCacheOrDownloadIcon(icon, managerName, packageId, 0);
            Assert.NotNull(path);
            DateTime newModificationDate = File.GetLastWriteTime(path);

            Assert.Equal(oldModificationDate, newModificationDate);
            Assert.Equal(expectedFile, path);
            Assert.True(File.Exists(path));

            // Test a new URI, the icon should be downloaded again
            icon = new CacheableIcon(URI_2);
            path = IconCacheEngine.GetCacheOrDownloadIcon(icon, managerName, packageId, 0);
            Assert.NotNull(path);
            DateTime newNewModificationDate = File.GetLastWriteTime(path);

            Assert.NotEqual(oldModificationDate, newNewModificationDate);
            Assert.Equal(expectedFile, path);
            Assert.True(File.Exists(path));
        }

        [Fact]
        public static void TestCacheEngineForPackageSize()
        {
            Uri ICON_1 = new Uri(UniGetUiIconUrl);
            int ICON_1_SIZE = 19788;
            Uri ICON_2 = new Uri(ElevenClockIconUrl);
            int ICON_2_SIZE = 19747;
            string managerName = "TestManager";
            string packageId = "Package3";

            // Clear any cache for reproducible data
            string extension = ICON_1.ToString().Split(".")[^1];
            string expectedFile = Path.Join(
                CoreData.UniGetUICacheDirectory_Icons,
                managerName,
                packageId,
                $"icon.{extension}"
            );
            if (File.Exists(expectedFile))
            {
                File.Delete(expectedFile);
            }

            // Cache an icon
            CacheableIcon icon = new(ICON_1, ICON_1_SIZE);
            string? path = IconCacheEngine.GetCacheOrDownloadIcon(icon, managerName, packageId, 0);
            Assert.NotNull(path);
            Assert.Equal(expectedFile, path);
            Assert.True(File.Exists(path));

            DateTime oldModificationDate = File.GetLastWriteTime(path);

            // Attempt to retrieve the same icon again.
            icon = new CacheableIcon(ICON_1, ICON_1_SIZE);
            path = IconCacheEngine.GetCacheOrDownloadIcon(icon, managerName, packageId, 0);
            Assert.NotNull(path);
            DateTime newModificationDate = File.GetLastWriteTime(path);

            // The modification date shouldn't have changed
            Assert.Equal(oldModificationDate, newModificationDate);
            Assert.Equal(expectedFile, path);
            Assert.True(File.Exists(path));

            // Attempt to retrieve a different icon. The modification date SHOULD have changed
            icon = new CacheableIcon(ICON_2, ICON_2_SIZE);
            path = IconCacheEngine.GetCacheOrDownloadIcon(icon, managerName, packageId, 0);
            Assert.NotNull(path);
            DateTime newIconModificationDate = File.GetLastWriteTime(path);

            Assert.NotEqual(oldModificationDate, newIconModificationDate);
            Assert.Equal(expectedFile, path);
            Assert.True(File.Exists(path));

            // Give an invalid size: The icon should not be cached not returned
            icon = new CacheableIcon(ICON_1, ICON_1_SIZE + 1);
            path = IconCacheEngine.GetCacheOrDownloadIcon(icon, managerName, packageId, 0);
            Assert.Null(path);
            Assert.False(File.Exists(path));
        }
    }
}
