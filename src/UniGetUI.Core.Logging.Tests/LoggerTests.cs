#pragma warning disable CA2201
namespace UniGetUI.Core.Logging.Tests
{
    public class LoggerTests
    {
        [Fact]
        public void TestLogger()
        {
            int baseIndex = Logger.GetLogs().Length;

            DateTime startTime = DateTime.Now;
            Logger.Info("Hello World");
            Logger.Debug("Hello World 2");
            Logger.Error("Hello World 3");
            Logger.Warn(new Exception("Test exception"));

            DateTime endTime = DateTime.Now;

            LogEntry[] logs = Logger.GetLogs()[baseIndex..];

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
    }
}
