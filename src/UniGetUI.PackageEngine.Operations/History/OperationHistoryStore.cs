using System.Text.Json;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;

namespace UniGetUI.PackageEngine.Operations.History;

/// <summary>
/// Thread-safe, file-backed store of finished operations. Single source of truth for the
/// history UI and the IPC/agent layer. Persisted as JSON in the user configuration directory.
/// </summary>
public static class OperationHistoryStore
{
    private const int MaxEntries = 1000;
    private static readonly object _lock = new();
    private static List<OperationHistoryRecord>? _cache;

    /// <summary>Raised (off the lock) whenever the history changes, so the UI can refresh.</summary>
    public static event EventHandler? Changed;

    /// <summary>Test-only override for the backing file path; production leaves this null.</summary>
    public static string? TestFilePathOverride { get; set; }

    private static string FilePath
        => TestFilePathOverride ?? Path.Join(CoreData.UniGetUIUserConfigurationDirectory, "OperationHistory.json");

    private static List<OperationHistoryRecord> LoadUnlocked()
    {
        if (_cache is not null) return _cache;
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                var typeInfo = OperationHistoryJsonContext.Default.ListOperationHistoryRecord;
                _cache = JsonSerializer.Deserialize(json, typeInfo) ?? [];
            }
            else
            {
                _cache = [];
                // First run on the new store: pull in the old plain-text history, if any.
                if (TestFilePathOverride is null)
                    ImportLegacyTextHistoryUnlocked(_cache);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Failed to read the operation history store; starting empty");
            Logger.Warn(ex);
            _cache = [];
        }
        return _cache;
    }

    private static bool SaveUnlocked()
    {
        try
        {
            var typeInfo = OperationHistoryJsonContext.Default.ListOperationHistoryRecord;
            string json = JsonSerializer.Serialize(_cache ?? [], typeInfo);
            File.WriteAllText(FilePath, json);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Warn("Failed to persist the operation history store");
            Logger.Warn(ex);
            return false;
        }
    }

    public static IReadOnlyList<OperationHistoryRecord> GetAll()
    {
        lock (_lock) return LoadUnlocked().ToArray();
    }

    public static OperationHistoryRecord? Get(string id)
    {
        lock (_lock) return LoadUnlocked().FirstOrDefault(r => r.Id == id);
    }

    /// <summary>Prepend a record (newest first), capping the store at <see cref="MaxEntries"/>.</summary>
    public static void Add(OperationHistoryRecord record)
    {
        lock (_lock)
        {
            var list = LoadUnlocked();
            list.Insert(0, record);
            if (list.Count > MaxEntries)
                list.RemoveRange(MaxEntries, list.Count - MaxEntries);
            SaveUnlocked();
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Remove(string id)
    {
        bool removed;
        lock (_lock)
        {
            removed = LoadUnlocked().RemoveAll(r => r.Id == id) > 0;
            if (removed) SaveUnlocked();
        }
        if (removed) Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Clear()
    {
        lock (_lock)
        {
            LoadUnlocked().Clear();
            SaveUnlocked();
        }
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Drops the in-memory cache so the next read re-loads from disk. Test/diagnostic use.</summary>
    public static void InvalidateCache()
    {
        lock (_lock) _cache = null;
    }

    // One-time migration: earlier versions stored history as a single newline-joined text blob in the
    // "OperationHistory" settings file. It has no per-operation metadata, so it can only be surfaced in
    // the raw Log tab as one legacy entry. Once imported, the old file is removed so this never repeats.
    private static void ImportLegacyTextHistoryUnlocked(List<OperationHistoryRecord> cache)
    {
        try
        {
            string legacyPath = Path.Join(CoreData.UniGetUIUserConfigurationDirectory, "OperationHistory");
            if (!File.Exists(legacyPath)) return;

            string content = File.ReadAllText(legacyPath);
            if (string.IsNullOrWhiteSpace(content))
            {
                // Nothing to migrate; drop the empty legacy file so the check doesn't repeat.
                File.Delete(legacyPath);
                return;
            }

            var record = new OperationHistoryRecord
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = OperationHistoryRecord.KindLegacyLog,
                Status = "",
                TimestampUtc = DateTime.UtcNow.ToString("O"),
                Output = content
                    .Split('\n')
                    .Select(line => new OperationHistoryOutputLine
                    {
                        Text = line.Replace("\r", ""),
                        Type = "Information",
                    })
                    .ToList(),
            };
            cache.Add(record);

            // Only delete the user's only history copy after a verifiably successful save. If the save
            // fails (full disk, permissions), keep the legacy file and retry the import next launch.
            if (SaveUnlocked())
            {
                Logger.Info($"Imported {record.Output.Count} lines of legacy operation history");
                File.Delete(legacyPath);
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Failed to import legacy operation history");
            Logger.Warn(ex);
        }
    }
}
