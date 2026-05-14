namespace UniGetUI.Core.Data.Tests
{
    public class CoreTests
    {
        public static object[][] Data =>
            [
                [CoreData.UniGetUIDataDirectory],
                [CoreData.UniGetUIInstallationOptionsDirectory],
                [CoreData.UniGetUICacheDirectory_Data],
                [CoreData.UniGetUICacheDirectory_Icons],
                [CoreData.UniGetUICacheDirectory_Lang],
                [CoreData.UniGetUI_DefaultBackupDirectory],
            ];

        [Theory]
        [MemberData(nameof(Data))]
        public void CheckDirectoryAttributes(string directory)
        {
            Assert.True(
                Directory.Exists(directory),
                $"Directory ${directory} does not exist, but it should have been created automatically"
            );
        }

        [Fact]
        public void CheckOtherAttributes()
        {
            Assert.NotEmpty(CoreData.VersionName);
            Assert.NotEqual(0, CoreData.BuildNumber);
            Assert.NotEqual(0, CoreData.UpdatesAvailableNotificationTag);

            Assert.True(
                Directory.Exists(CoreData.UniGetUIExecutableDirectory),
                "Directory where the executable is located does not exist"
            );
            Assert.True(
                File.Exists(CoreData.UniGetUIExecutableFile),
                "The executable file does not exist"
            );
        }

        [Fact]
        public void ResolveInstallationDirectoryReturnsParentForBundledAvaloniaDirectory()
        {
            string installDirectory = Path.GetFullPath(Path.Join("install-root"));
            string avaloniaDirectory = Path.Join(installDirectory, "Avalonia");
            string classicExecutable = Path.Join(installDirectory, "UniGetUI.exe");

            string resolvedDirectory = CoreData.ResolveInstallationDirectory(
                avaloniaDirectory,
                filePath => filePath == classicExecutable,
                static _ => false
            );

            Assert.Equal(installDirectory, resolvedDirectory);
        }

        [Fact]
        public void ResolveInstallationDirectoryKeepsStandaloneAvaloniaDirectory()
        {
            string avaloniaDirectory = Path.GetFullPath(Path.Join("standalone", "Avalonia"));

            string resolvedDirectory = CoreData.ResolveInstallationDirectory(
                avaloniaDirectory,
                static _ => false,
                static _ => false
            );

            Assert.Equal(avaloniaDirectory, resolvedDirectory);
        }

        [Theory]
        [InlineData("3.3.7", "3.3.7")]
        [InlineData("2026.1.2", "v2026.1.2")]
        [InlineData("v2026.1.2", "v2026.1.2")]
        public void CheckGitHubReleaseTag(string versionName, string expectedTag)
        {
            Assert.Equal(expectedTag, CoreData.GetGitHubReleaseTag(versionName));
        }

        [Fact]
        public void CheckGitHubReleaseTagCandidatesForCalendarVersion()
        {
            Assert.Equal(
                ["v2026.1.2", "2026.1.2"],
                CoreData.GetGitHubReleaseTagCandidates("2026.1.2")
            );
        }

        [Fact]
        public void CheckGitHubReleaseUrlsUseEscapedResolvedTag()
        {
            Assert.Equal(
                "https://github.com/Devolutions/UniGetUI/releases/tag/v2026.1.2",
                CoreData.GetGitHubReleasePageUrlFromTag(CoreData.GetGitHubReleaseTag("2026.1.2"))
            );
            Assert.Equal(
                "https://api.github.com/repos/Devolutions/UniGetUI/releases/tags/3.3.7-beta1",
                CoreData.GetGitHubReleaseApiUrlFromTag("3.3.7-beta1")
            );
            Assert.Equal("https://devolutions.net/unigetui/release-notes/", CoreData.ReleaseNotesUrl);
            Assert.Equal(CoreData.ReleaseNotesUrl, CoreData.GetGitHubReleasePageUrl());
        }
    }
}
