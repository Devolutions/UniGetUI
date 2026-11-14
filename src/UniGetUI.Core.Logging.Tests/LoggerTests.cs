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
