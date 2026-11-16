#pragma warning disable CA2201
namespace UniGetUI.Core.Logging.Tests
{
    public class LoggerTests
    {
        [Fact]
        public void TestLogger()
        {
            long marker = Logger.TotalLogged;

            DateTime startTime = DateTime.Now;
            Logger.Info("Hello World");
            Logger.Debug("Hello World 2");
            Logger.Error("Hello World 3");
            Logger.Warn(new Exception("Test exception"));

            DateTime endTime = DateTime.Now;

            LogEntry[] logs = Logger.GetLogsSince(marker);

            Assert.Equal("Hello World", logs[0].Content);
            Assert.Equal("Hello World 2", logs[1].Content);
            Assert.Equal("Hello World 3", logs[2].Content);
            Assert.Equal("System.Exception: Test exception", logs[3].Content);

            Assert.Equal(LogEntry.SeverityLevel.Info, logs[0].Severity);
            Assert.Equal(LogEntry.SeverityLevel.Debug, logs[1].Severity);
            Assert.Equal(LogEntry.SeverityLevel.Error, logs[2].Severity);
            Assert.Equal(LogEntry.SeverityLevel.Warning, logs[3].Severity);

            Assert.True(logs[0].Time > startTime && logs[0].Time < endTime);
            Assert.True(logs[1].Time > startTime && logs[1].Time < endTime);
            Assert.True(logs[2].Time > startTime && logs[2].Time < endTime);
            Assert.True(logs[3].Time > startTime && logs[3].Time < endTime);
        }

        [Fact]
        public void InMemoryLogIsBoundedButKeepsCountingAndRecentEntries()
        {
            long marker = Logger.TotalLogged;
            int written = Logger.MAX_RETAINED_ENTRIES + (Logger.TRIM_CHUNK * 2) + 7;

            for (int i = 0; i < written; i++)
                Logger.Debug($"bounded-log-test {i}");

            Assert.True(Logger.GetLogs().Length <= Logger.MAX_RETAINED_ENTRIES + Logger.TRIM_CHUNK);
            Assert.True(Logger.TotalLogged - marker >= written);

            LogEntry[] recent = Logger.GetLogsSince(marker);
            Assert.InRange(recent.Length, Logger.MAX_RETAINED_ENTRIES, Logger.MAX_RETAINED_ENTRIES + Logger.TRIM_CHUNK);
            Assert.Equal($"bounded-log-test {written - 1}", recent[^1].Content);

            // A marker taken after the log is full still returns exactly what came after it.
            long lateMarker = Logger.TotalLogged;
            Logger.Info("after the marker");
            Assert.Equal("after the marker", Assert.Single(Logger.GetLogsSince(lateMarker)).Content);
        }

        [Fact]
        public void LogFileIsRotatedOnlyOnceItReachesTheLimit()
        {
            string directory = Directory.CreateTempSubdirectory("unigetui-log-rotation-").FullName;
            try
            {
                string path = Path.Join(directory, "session.log");
                string rotated = path + ".old";

                Assert.False(Logger.RotateLogFileIfTooLarge(path, 100));

                File.WriteAllText(path, new string('a', 99));
                Assert.False(Logger.RotateLogFileIfTooLarge(path, 100));
                Assert.False(File.Exists(rotated));

                File.WriteAllText(path, new string('b', 100));
                Assert.True(Logger.RotateLogFileIfTooLarge(path, 100));
                Assert.False(File.Exists(path));
                Assert.Equal(new string('b', 100), File.ReadAllText(rotated));

                // A second rotation replaces the previous rotated file, so only two files ever exist.
                File.WriteAllText(path, new string('c', 150));
                Assert.True(Logger.RotateLogFileIfTooLarge(path, 100));
                Assert.Equal(new string('c', 150), File.ReadAllText(rotated));
                Assert.Equal(["session.log.old"], Directory.GetFiles(directory).Select(Path.GetFileName));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        // Rotation is serialized between processes: while another one is rotating, this one must
        // not move the file, and afterwards it must decide from the file as it is then.
        [Fact]
        public void RotationWaitsItsTurnBehindAnotherRotation()
        {
            string directory = Directory.CreateTempSubdirectory("unigetui-log-rotation-").FullName;
            try
            {
                string path = Path.Join(directory, "session.log");
                string rotated = path + ".old";
                File.WriteAllText(rotated, "archived history");
                File.WriteAllText(path, new string('a', 100));

                using var held = new ManualResetEventSlim();
                using var release = new ManualResetEventSlim();
                var other = new Thread(() =>
                {
                    using var mutex = new Mutex(initiallyOwned: false, Logger.RotationMutexName);
                    mutex.WaitOne();
                    held.Set();
                    release.Wait();
                    mutex.ReleaseMutex();
                });
                other.Start();
                held.Wait();
                try
                {
                    Assert.False(Logger.RotateLogFileIfTooLarge(path, 100, TimeSpan.FromMilliseconds(50)));
                    Assert.Equal("archived history", File.ReadAllText(rotated));
                    Assert.True(File.Exists(path));
                }
                finally
                {
                    release.Set();
                    other.Join();
                }

                // The other rotation has finished and left a new, small file: nothing to rotate.
                File.WriteAllText(path, "new");
                Assert.False(Logger.RotateLogFileIfTooLarge(path, 100));
                Assert.Equal("archived history", File.ReadAllText(rotated));
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public void RotatingALogFileThatCannotBeMovedDoesNotThrow()
        {
            string missingDirectory = Path.Join(Path.GetTempPath(), $"unigetui-missing-{Guid.NewGuid():N}");
            Assert.False(Logger.RotateLogFileIfTooLarge(Path.Join(missingDirectory, "session.log"), 1));
        }

        [Fact]
        public async Task ConcurrentLoggingDoesNotLoseOrCorruptEntries()
        {
            long marker = Logger.TotalLogged;
            await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
            {
                for (int i = 0; i < 500; i++)
                    Logger.Debug($"concurrent-log-test {t}-{i}");
            })));

            Assert.Equal(4000, Logger.GetLogsSince(marker).Count(e => e.Content.StartsWith("concurrent-log-test ", StringComparison.Ordinal)));
        }
    }
}
