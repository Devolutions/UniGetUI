namespace UniGetUI.Core.Data.Tests
{
    public class UpdateInProgressGuardTests : IDisposable
    {
        // {root}/app stands in for {app}; {root} is its always-empty parent.
        private readonly string _root;
        private readonly string _appDir;

        public UpdateInProgressGuardTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ugui-guard-" + Guid.NewGuid().ToString("N"));
            _appDir = Path.Combine(_root, "app");
            Directory.CreateDirectory(_appDir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { }
        }

        private static string WriteMarker(string directory)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, UpdateInProgressGuard.MarkerFileName);
            File.WriteAllText(path, "update-in-progress");
            return path;
        }

        [Fact]
        public void NoMarker_ReturnsFalse()
        {
            Assert.False(UpdateInProgressGuard.IsUpdateInProgress(_appDir));
        }

        [Fact]
        public void FreshMarkerInDirectory_ReturnsTrue()
        {
            WriteMarker(_appDir);
            Assert.True(UpdateInProgressGuard.IsUpdateInProgress(_appDir));
        }

        [Fact]
        public void FreshMarkerInParentDirectory_ReturnsTrue()
        {
            // Avalonia runs from {app}\Avalonia; marker is in {app}.
            WriteMarker(_appDir);
            string child = Path.Combine(_appDir, "Avalonia");
            Directory.CreateDirectory(child);

            Assert.True(UpdateInProgressGuard.IsUpdateInProgress(child));
        }

        [Fact]
        public void StaleMarker_ReturnsFalseAndIsDeleted()
        {
            string marker = WriteMarker(_appDir);
            File.SetLastWriteTimeUtc(marker, DateTime.UtcNow.AddMinutes(-15));

            Assert.False(UpdateInProgressGuard.IsUpdateInProgress(_appDir));
            Assert.False(File.Exists(marker)); // stale marker is cleaned up

        }

        [Fact]
        public void MarkerFileName_MatchesInstallerContract()
        {
            // Must stay in sync with the name written by UniGetUI.iss.
            Assert.Equal(".unigetui-update-in-progress", UpdateInProgressGuard.MarkerFileName);
        }
    }
}
