namespace UniGetUI.Core.Data.Tests
{
    public class UpdateInProgressGuardTests : IDisposable
    {
        // {root}/app stands in for {app}; {root} is its always-empty parent.
        private readonly string _root;
        private readonly string _appDir;

        private static readonly Func<int, bool> ProcessAlive = _ => true;
        private static readonly Func<int, bool> ProcessDead = _ => false;

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

        private static string WriteMarker(string directory, string content = "1234")
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, UpdateInProgressGuard.MarkerFileName);
            File.WriteAllText(path, content);
            return path;
        }

        [Fact]
        public void NoMarker_ReturnsFalse()
        {
            Assert.False(UpdateInProgressGuard.IsUpdateInProgress(_appDir, ProcessAlive));
        }

        [Fact]
        public void MarkerWithRunningInstaller_ReturnsTrue()
        {
            WriteMarker(_appDir);
            Assert.True(UpdateInProgressGuard.IsUpdateInProgress(_appDir, ProcessAlive));
        }

        [Fact]
        public void MarkerInParentWithRunningInstaller_ReturnsTrue()
        {
            // Avalonia runs from {app}\Avalonia; marker is in {app}.
            WriteMarker(_appDir);
            string child = Path.Combine(_appDir, "Avalonia");
            Directory.CreateDirectory(child);

            Assert.True(UpdateInProgressGuard.IsUpdateInProgress(child, ProcessAlive));
        }

        [Fact]
        public void MarkerWithDeadInstaller_ReturnsFalseAndIsDeleted()
        {
            string marker = WriteMarker(_appDir);

            Assert.False(UpdateInProgressGuard.IsUpdateInProgress(_appDir, ProcessDead));
            Assert.False(File.Exists(marker)); // stale marker is cleaned up
        }

        [Fact]
        public void MarkerWithUnreadableContent_ReturnsFalseAndIsKept()
        {
            string marker = WriteMarker(_appDir, "not-a-pid");

            Assert.False(UpdateInProgressGuard.IsUpdateInProgress(_appDir, ProcessAlive));
            Assert.True(File.Exists(marker)); // not deleted: could be a partial write
        }

        [Fact]
        public void RealProcessCheck_TreatsCurrentProcessAsRunning()
        {
            // Exercises the real IsProcessRunning via the parameterless overload.
            WriteMarker(_appDir, Environment.ProcessId.ToString());
            Assert.True(UpdateInProgressGuard.IsUpdateInProgress(_appDir));
        }

        [Fact]
        public void MarkerFileName_MatchesInstallerContract()
        {
            Assert.Equal(".unigetui-update-in-progress", UpdateInProgressGuard.MarkerFileName);
        }
    }
}
