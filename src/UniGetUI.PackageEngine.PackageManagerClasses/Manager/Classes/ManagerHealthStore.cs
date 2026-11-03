using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.ManagerClasses.Classes
{
    public static partial class ManagerHealthStore
    {
        private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(3);
        private static readonly Lock Gate = new();
        private static Dictionary<string, ManagerHealthRecord>? _records;
        private static string? _recordsPath;
        private static Timer? _saveTimer;

        public static string? TestPathOverride { get; set; }

        private static readonly AsyncLocal<Func<DateTime>?> ClockOverride = new();

        public static DateTime Now => ClockOverride.Value?.Invoke() ?? DateTime.UtcNow;

        public static IDisposable UseClock(Func<DateTime> clock)
        {
            ClockOverride.Value = clock;
            return new ClockScope();
        }

        private sealed class ClockScope : IDisposable
        {
            public void Dispose() => ClockOverride.Value = null;
        }

        static ManagerHealthStore()
        {
            AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
        }

        public static event EventHandler<string>? HealthChanged;

        private static string FilePath =>
            TestPathOverride ?? Path.Combine(CoreData.UniGetUIDataDirectory, "ManagerHealth.json");

        public static ManagerHealthRecord Get(string managerName)
        {
            lock (Gate)
            {
                return Records.TryGetValue(managerName, out var record)
                    ? record.Clone()
                    : new ManagerHealthRecord();
            }
        }

        public static void RecordListing(
            string managerName,
            ManagerListingKind kind,
            TimeSpan duration,
            int packageCount,
            Exception? failure,
            Exception? fastRepairCause
        )
        {
            lock (Gate)
            {
                if (!Records.TryGetValue(managerName, out var record))
                    Records[managerName] = record = new ManagerHealthRecord();

                ManagerListingHealth listing = record[kind];
                DateTime now = Now;

                listing.RecentDurationsMs.Add((long)duration.TotalMilliseconds);
                if (listing.RecentDurationsMs.Count > ManagerListingHealth.MaxRecentDurations)
                    listing.RecentDurationsMs.RemoveRange(
                        0,
                        listing.RecentDurationsMs.Count - ManagerListingHealth.MaxRecentDurations
                    );

                if (fastRepairCause is not null)
                {
                    listing.LastFastRepairUtc = now;
                    listing.LastFastRepairReason = Describe(fastRepairCause);
                }

                if (failure is null)
                {
                    listing.LastSuccessUtc = now;
                    listing.ConsecutiveFailures = 0;
                    listing.LastPackageCount = packageCount;
                }
                else
                {
                    listing.LastFailureUtc = now;
                    listing.LastFailureReason = Describe(failure);
                    listing.LastFailureWasTimeout = failure is TimeoutException;
                    listing.ConsecutiveFailures++;
                }

                ScheduleSave();
            }

            HealthChanged?.Invoke(null, managerName);
        }

        public static void NotifyChanged(string managerName) =>
            HealthChanged?.Invoke(null, managerName);

        public static void Flush()
        {
            lock (Gate)
            {
                _saveTimer?.Dispose();
                _saveTimer = null;
                Save();
            }
        }

        public static void ResetForTests()
        {
            lock (Gate)
            {
                _saveTimer?.Dispose();
                _saveTimer = null;
                _records = null;
                _recordsPath = null;
            }
        }

        private static string Describe(Exception failure) =>
            UrlCredentials()
                .Replace(Logger.Redact($"{failure.GetType().Name}: {failure.Message}"), "****@");

        [GeneratedRegex(@"(?<=://)[^/?#\s@]+@")]
        private static partial Regex UrlCredentials();

        private static Dictionary<string, ManagerHealthRecord> Records
        {
            get
            {
                string path = FilePath;
                if (_records is null || _recordsPath != path)
                {
                    if (_records is not null)
                        Save();
                    _records = Load(path);
                    _recordsPath = path;
                }
                return _records;
            }
        }

        private static Dictionary<string, ManagerHealthRecord> Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    var loaded = JsonSerializer.Deserialize(
                        File.ReadAllBytes(path),
                        ManagerHealthJsonContext.Default.DictionaryStringManagerHealthRecord
                    );
                    if (loaded is not null)
                        return new Dictionary<string, ManagerHealthRecord>(
                            loaded,
                            StringComparer.OrdinalIgnoreCase
                        );
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Logger.Warn($"Could not read the package manager health record: {ex.Message}");
            }

            return new Dictionary<string, ManagerHealthRecord>(StringComparer.OrdinalIgnoreCase);
        }

        private static void ScheduleSave()
        {
            _saveTimer ??= new Timer(_ => Flush(), null, SaveDelay, Timeout.InfiniteTimeSpan);
        }

        private static void Save()
        {
            if (_records is null || _recordsPath is null)
                return;

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_recordsPath)!);
                File.WriteAllBytes(
                    _recordsPath,
                    JsonSerializer.SerializeToUtf8Bytes(
                        _records,
                        ManagerHealthJsonContext.Default.DictionaryStringManagerHealthRecord
                    )
                );
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn($"Could not save the package manager health record: {ex.Message}");
            }
        }
    }

    [JsonSourceGenerationOptions(WriteIndented = true)]
    [JsonSerializable(typeof(Dictionary<string, ManagerHealthRecord>))]
    internal sealed partial class ManagerHealthJsonContext : JsonSerializerContext;
}
