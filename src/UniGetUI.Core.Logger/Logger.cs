using Diagnostics = System.Diagnostics;

namespace UniGetUI.Core.Logging
{
    public static class Logger
    {
        // The in-memory log backs the log pages and the IPC log API. It used to keep every entry
        // for the life of the process, so a UniGetUI left running in the tray grew without bound
        // (every update check logs several lines per manager). Keep only the most recent entries;
        // the complete log is still written to the session log file. Trimmed in chunks so that
        // appending stays O(1) amortized.
        public const int MAX_RETAINED_ENTRIES = 10_000;
        public const int TRIM_CHUNK = 1_000;

        private static readonly List<LogEntry> LogContents = [];
        private static readonly Lock LogContentsLock = new();
        private static long _totalLogged;
        private static readonly Lock LogWriteLock = new();
        private static string SessionLogPath => Path.Combine(AppPaths.ScratchDirectory, "session.log");

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
            lock (LogContentsLock)
            {
                LogContents.Add(new LogEntry(content, severity));
                _totalLogged++;
                if (LogContents.Count > MAX_RETAINED_ENTRIES + TRIM_CHUNK)
                    LogContents.RemoveRange(0, LogContents.Count - MAX_RETAINED_ENTRIES);
            }
        }

        // The session log file is shared by every UniGetUI process (the app, each CLI call, the
        // helpers) and was only ever appended to, so it grew for as long as the temp folder
        // lived. Once it passes MAX_SESSION_LOG_BYTES it is moved to "session.log.old", replacing
        // the previous one, so the two files together stay bounded and recent history survives.
        public const long MAX_SESSION_LOG_BYTES = 5 * 1024 * 1024;

        // The size is only looked up every so many writes: a file stat per log line is not free.
        private const int WRITES_BETWEEN_SIZE_CHECKS = 200;
        private static int _writesUntilSizeCheck; // 0: check on this process's first write

        public static string GetRotatedSessionLogPath() => SessionLogPath + ".old";

        /// <summary>
        /// Moves <paramref name="path"/> to <paramref name="path"/>.old, replacing it, when the
        /// file has reached <paramref name="maxBytes"/>. Never throws: another process may hold
        /// or move the file at the same time, and logging must go on either way.
        /// </summary>
        /// <returns>Whether the file was rotated.</returns>
        public static bool RotateLogFileIfTooLarge(string path, long maxBytes) =>
            RotateLogFileIfTooLarge(path, maxBytes, TimeSpan.FromSeconds(1));

        // The lock in AppendToSessionLog only covers this process. Without a lock shared between
        // processes, two of them could both see the file over the limit; the first would rotate
        // it and start a new one, and the second would then move that new, nearly empty file
        // over "session.log.old", losing the history just archived. Under this mutex the second
        // process looks at the file as it is now, and finds nothing to rotate.
        public const string RotationMutexName = "UniGetUI.SessionLogRotation";

        /// <param name="wait">
        /// How long to wait for another process that is rotating. If it takes longer, nothing is
        /// rotated this time; the next size check tries again.
        /// </param>
        public static bool RotateLogFileIfTooLarge(string path, long maxBytes, TimeSpan wait)
        {
            try
            {
                using var mutex = new Mutex(initiallyOwned: false, RotationMutexName);
                bool owned;
                try
                {
                    owned = mutex.WaitOne(wait);
                }
                catch (AbandonedMutexException)
                {
                    // The process that held it exited without releasing it; it is ours now.
                    owned = true;
                }

                if (!owned)
                    return false;

                try
                {
                    var file = new FileInfo(path);
                    if (!file.Exists || file.Length < maxBytes)
                        return false;

                    File.Move(path, path + ".old", overwrite: true);
                    return true;
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
            catch
            {
                return false;
            }
        }

        private static void AppendToSessionLog(string text)
        {
            try
            {
                string sessionLogPath = SessionLogPath;
                Directory.CreateDirectory(Path.GetDirectoryName(sessionLogPath)!);
                lock (LogWriteLock)
                {
                    if (--_writesUntilSizeCheck < 0)
                    {
                        _writesUntilSizeCheck = WRITES_BETWEEN_SIZE_CHECKS;
                        RotateLogFileIfTooLarge(sessionLogPath, MAX_SESSION_LOG_BYTES);
                    }

                    File.AppendAllText(
                        sessionLogPath,
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

        /// <summary>
        /// The most recent log entries still held in memory (oldest first). Older entries are
        /// dropped once the in-memory log is full, so this array's length is not a running
        /// position: use <see cref="TotalLogged"/> and <see cref="GetLogsSince"/> for that.
        /// </summary>
        public static LogEntry[] GetLogs()
        {
            lock (LogContentsLock)
                return LogContents.ToArray();
        }

        /// <summary>
        /// The number of entries logged since the process started, including those no longer
        /// held in memory. Unlike <c>GetLogs().Length</c>, it keeps growing once the log is full.
        /// </summary>
        public static long TotalLogged
        {
            get
            {
                lock (LogContentsLock)
                    return _totalLogged;
            }
        }

        /// <summary>
        /// The entries logged after <paramref name="marker"/>, a value previously read from
        /// <see cref="TotalLogged"/>, as far as they are still held in memory.
        /// </summary>
        public static LogEntry[] GetLogsSince(long marker)
        {
            lock (LogContentsLock)
            {
                long firstRetained = _totalLogged - LogContents.Count;
                int start = (int)Math.Clamp(marker - firstRetained, 0, LogContents.Count);
                return LogContents.GetRange(start, LogContents.Count - start).ToArray();
            }
        }
    }
}
