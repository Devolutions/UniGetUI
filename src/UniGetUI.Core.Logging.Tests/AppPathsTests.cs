using System.Diagnostics;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace UniGetUI.Core.Logging.Tests
{
    public sealed class AppPathsTests : IDisposable
    {
        private readonly string _testRoot;

        public AppPathsTests()
        {
            _testRoot = Path.Combine(
                Path.GetTempPath(),
                $"UniGetUI-AppPathsTests-{Guid.NewGuid():N}"
            );
            Directory.CreateDirectory(_testRoot);
        }

        public void Dispose()
        {
            AppPaths.TEST_PortableDataDirectoryOverride = null;

            if (Directory.Exists(_testRoot))
            {
                Directory.Delete(_testRoot, true);
            }
        }

        [Fact]
        public void ResolvePortableDataDirectoryReturnsNullWithoutTheMarkerFile()
        {
            Assert.Null(AppPaths.ResolvePortableDataDirectory(_testRoot));
            Assert.False(Directory.Exists(Path.Combine(_testRoot, "Settings")));
        }

        [Fact]
        public void ResolvePortableDataDirectoryReturnsSettingsFolderWhenMarked()
        {
            File.WriteAllText(Path.Combine(_testRoot, "ForceUniGetUIPortable"), string.Empty);

            string? resolved = AppPaths.ResolvePortableDataDirectory(_testRoot);

            Assert.Equal(Path.Join(_testRoot, "Settings"), resolved);
            Assert.True(File.Exists(Path.Combine(_testRoot, "Settings", "PermissionTestFile")));
        }

        [Fact]
        public void ResolvePortableDataDirectoryFallsBackWhenTheFolderIsNotWritable()
        {
            File.WriteAllText(Path.Combine(_testRoot, "ForceUniGetUIPortable"), string.Empty);
            File.WriteAllText(Path.Combine(_testRoot, "Settings"), string.Empty);

            Assert.Null(AppPaths.ResolvePortableDataDirectory(_testRoot));
        }

        [Fact]
        public void ResolveInstallationDirectoryKeepsAVolumeRootIntact()
        {
            string root = Path.GetPathRoot(Path.GetFullPath("."))!;

            Assert.Equal(
                root,
                AppPaths.ResolveInstallationDirectory(root, static _ => false, static _ => false));
        }

        [Fact]
        public void ResolveInstallationDirectoryTrimsATrailingSeparator()
        {
            string directory = Path.Join(Path.GetFullPath("."), "UniGetUI");

            Assert.Equal(
                directory,
                AppPaths.ResolveInstallationDirectory(
                    directory + Path.DirectorySeparatorChar,
                    static _ => false,
                    static _ => false));
        }

        [Fact]
        public void ResolvePortableDataDirectoryMarksAFirstRunOnlyWhenItCreatesTheFolder()
        {
            File.WriteAllText(Path.Combine(_testRoot, "ForceUniGetUIPortable"), string.Empty);

            string? first = AppPaths.ResolvePortableDataDirectory(_testRoot);
            Assert.NotNull(first);
            Assert.True(File.Exists(Path.Combine(first!, "FirstRun.pending")));

            File.Delete(Path.Combine(first!, "FirstRun.pending"));
            AppPaths.ResolvePortableDataDirectory(_testRoot);
            Assert.False(
                File.Exists(Path.Combine(first!, "FirstRun.pending")),
                "an established portable folder must not be marked as a first run again");
        }

        [Fact]
        public void ClearFirstPortableRunRemovesTheMarker()
        {
            string portableDirectory = Path.Combine(_testRoot, "Settings");
            Directory.CreateDirectory(portableDirectory);
            File.WriteAllText(Path.Combine(portableDirectory, "FirstRun.pending"), string.Empty);
            AppPaths.TEST_PortableDataDirectoryOverride = portableDirectory;

            Assert.True(AppPaths.IsFirstPortableRun);

            AppPaths.ClearFirstPortableRun();

            Assert.False(AppPaths.IsFirstPortableRun);
        }

        [Fact]
        public void ScratchDirectoryStaysOutsideTheInstallFolderWhenNotPortable()
        {
            AppPaths.TEST_PortableDataDirectoryOverride = null;

            Assert.False(AppPaths.IsPortable);
            Assert.Equal(Path.Join(Path.GetTempPath(), "UniGetUI"), AppPaths.ScratchDirectory);
        }

        [Fact]
        public void ScratchDirectoryMovesIntoThePortableFolderWhenPortable()
        {
            string portableDirectory = Path.Combine(_testRoot, "Settings");
            AppPaths.TEST_PortableDataDirectoryOverride = portableDirectory;

            Assert.True(AppPaths.IsPortable);
            Assert.Equal(Path.Join(portableDirectory, "Temp"), AppPaths.ScratchDirectory);
        }

        [Fact]
        public void ResolveLinkedDirectoriesKeepsTheMissingTailOfAPlainPath()
        {
            Directory.CreateDirectory(Path.Join(_testRoot, "Settings"));
            string root = AppPaths.ResolveLinkedDirectories(_testRoot);

            Assert.Equal(
                Path.Join(root, "Settings", "Temp", "ElevatedWinGetTemp"),
                AppPaths.ResolveLinkedDirectories(Path.Join(_testRoot, "Settings", "Temp", "ElevatedWinGetTemp")));
        }

        [Fact]
        public void ResolveLinkedDirectoriesFollowsScoopJunctions()
        {
            if (!OperatingSystem.IsWindows())
                return;

            string persisted = Path.Join(_testRoot, "persist", "unigetui", "Settings");
            string version = Path.Join(_testRoot, "apps", "unigetui", "2026.3.1");
            Directory.CreateDirectory(Path.Join(persisted, "Temp"));
            Directory.CreateDirectory(version);
            string settingsJunction = Path.Join(version, "Settings");
            string currentJunction = Path.Join(_testRoot, "apps", "unigetui", "current");
            try
            {
                CreateJunction(settingsJunction, persisted);
                CreateJunction(currentJunction, version);

                string resolved = AppPaths.ResolveLinkedDirectories(
                    Path.Join(currentJunction, "Settings", "Temp", "ElevatedWinGetTemp"));

                Assert.Equal(
                    Path.Join(AppPaths.ResolveLinkedDirectories(persisted), "Temp", "ElevatedWinGetTemp"),
                    resolved);
            }
            finally
            {
                foreach (string junction in new[] { currentJunction, settingsJunction })
                {
                    if (Directory.Exists(junction))
                        Directory.Delete(junction);
                }
            }
        }

        [Fact]
        public void ResolveLinkedDirectoriesFollowsSymbolicLinks()
        {
            if (!OperatingSystem.IsWindows())
                return;

            string persisted = Path.Join(_testRoot, "persist", "unigetui", "Settings");
            string version = Path.Join(_testRoot, "apps", "unigetui", "2026.3.1");
            Directory.CreateDirectory(Path.Join(persisted, "Temp"));
            Directory.CreateDirectory(version);
            try
            {
                Directory.CreateSymbolicLink(Path.Join(version, "Settings"), persisted);
                Directory.CreateSymbolicLink(Path.Join(_testRoot, "apps", "unigetui", "current"), version);
            }
            catch (IOException ex) when (ex.HResult == ErrorPrivilegeNotHeld)
            {
                return;
            }

            string resolved = AppPaths.ResolveLinkedDirectories(
                Path.Join(_testRoot, "apps", "unigetui", "current", "Settings", "Temp"));

            Assert.Equal(Path.Join(AppPaths.ResolveLinkedDirectories(persisted), "Temp"), resolved);
        }

        [Fact]
        public void RedirectTempToElevatedDirectorySetsTempAndTmp()
        {
            AppPaths.TEST_PortableDataDirectoryOverride = Path.Combine(_testRoot, "Settings");
            ProcessStartInfo startInfo = new();

            string directory = AppPaths.RedirectTempToElevatedDirectory(startInfo);

            Assert.Equal(AppPaths.ElevatedTempDirectory, directory);
            Assert.Equal(directory, startInfo.Environment["TEMP"]);
            Assert.Equal(directory, startInfo.Environment["TMP"]);
            Assert.EndsWith(Path.Join("Settings", "Temp", "ElevatedWinGetTemp"), directory);
            Assert.True(Directory.Exists(directory));
        }

        private const int ErrorPrivilegeNotHeld = unchecked((int)0x80070522);

        private static void CreateJunction(string link, string target)
        {
            ProcessStartInfo startInfo = new("cmd.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
            };
            foreach (string argument in new[] { "/c", "mklink", "/J", link, target })
                startInfo.ArgumentList.Add(argument);

            using Process process = Process.Start(startInfo)!;
            process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
        }

        [Fact]
        public void SessionLogIsWrittenInsideThePortableFolderWhenPortable()
        {
            string portableDirectory = Path.Combine(_testRoot, "Settings");
            AppPaths.TEST_PortableDataDirectoryOverride = portableDirectory;

            Logger.Info($"Portable session log probe {Guid.NewGuid():N}");

            Assert.True(File.Exists(Path.Join(portableDirectory, "Temp", "session.log")));
        }
    }
}
