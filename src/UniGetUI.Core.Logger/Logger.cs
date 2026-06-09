using Diagnostics = System.Diagnostics;

namespace UniGetUI.Core.Logging
{
    public static class Logger
    {
        private static readonly List<LogEntry> LogContents = [];
        private static readonly Lock LogWriteLock = new();
        private static readonly string SessionLogPath = Path.Combine(
            Path.GetTempPath(),
            "UniGetUI",
            "session.log"
        );

        private static readonly string UserName = Environment.UserName;

        // When enabled, the current user's name is replaced by **** in every logged line (privacy).
        public static bool RedactUsername { get; set; }

        public static string GetSessionLogPath() => SessionLogPath;

        // Replaces the current user's name with **** when redaction is enabled (privacy).
        // Public so other diagnostic surfaces (operation output, crash reports, update logs)
        // can apply the same redaction before persisting or displaying their own content.
        public static string Redact(string text)
        {
            if (!RedactUsername || UserName.Length == 0)
                return text;
            return text.Replace(UserName, "****", StringComparison.OrdinalIgnoreCase);
        }

        private static void Add(
            string content,
            LogEntry.SeverityLevel severity,
            string caller
        )
        {
            content = Redact(content);
            Diagnostics.Debug.WriteLine($"[{caller}] " + content);
            AppendToSessionLog(content);
            LogContents.Add(new LogEntry(content, severity));
        }

        private static void AppendToSessionLog(string text)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SessionLogPath)!);
                lock (LogWriteLock)
                {
                    File.AppendAllText(
                        SessionLogPath,
                        $"[{DateTime.Now:yyyy-MM-dd h:mm:ss tt}] {text}{Environment.NewLine}"
                    );
                }
            }
            catch { }
        }

        // String parameter log functions
        public static void ImportantInfo(
            string s,
            [System.Runtime.CompilerServices.CallerMemberName] string caller = ""
        ) => Add(s, LogEntry.SeverityLevel.Success, caller);

        public static void Debug(
            string s,
            [System.Runtime.CompilerServices.CallerMemberName] string caller = ""
        ) => Add(s, LogEntry.SeverityLevel.Debug, caller);

        public static void Info(
            string s,
            [System.Runtime.CompilerServices.CallerMemberName] string caller = ""
        ) => Add(s, LogEntry.SeverityLevel.Info, caller);

        public static void Warn(
            string s,
            [System.Runtime.CompilerServices.CallerMemberName] string caller = ""
        ) => Add(s, LogEntry.SeverityLevel.Warning, caller);

        public static void Error(
            string s,
            [System.Runtime.CompilerServices.CallerMemberName] string caller = ""
        ) => Add(s, LogEntry.SeverityLevel.Error, caller);

        // Exception parameter log functions
        public static void ImportantInfo(
            Exception e,
            [System.Runtime.CompilerServices.CallerMemberName] string caller = ""
        ) => Add(e.ToString(), LogEntry.SeverityLevel.Success, caller);

        public static void Debug(
            Exception e,
            [System.Runtime.CompilerServices.CallerMemberName] string caller = ""
        ) => Add(e.ToString(), LogEntry.SeverityLevel.Debug, caller);

        public static void Info(
            Exception e,
            [System.Runtime.CompilerServices.CallerMemberName] string caller = ""
        ) => Add(e.ToString(), LogEntry.SeverityLevel.Info, caller);

        public static void Warn(
            Exception e,
            [System.Runtime.CompilerServices.CallerMemberName] string caller = ""
        ) => Add(e.ToString(), LogEntry.SeverityLevel.Warning, caller);

        public static void Error(
            Exception e,
            [System.Runtime.CompilerServices.CallerMemberName] string caller = ""
        ) => Add(e.ToString(), LogEntry.SeverityLevel.Error, caller);

        public static LogEntry[] GetLogs()
        {
            return LogContents.ToArray();
        }
    }
}
