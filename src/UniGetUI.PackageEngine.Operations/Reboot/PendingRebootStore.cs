using System.Text.Json;
using UniGetUI.Core.Data;
using UniGetUI.Core.Logging;
using UniGetUI.PackageEngine.Enums;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.Operations.Reboot;

public static class PendingRebootStore
{
    private const int MaxEntries = 500;
    private static readonly object _lock = new();
    private static List<PendingRebootEntry>? _cache;
    private static (DateTime Time, long Length) _cacheStamp = (default, -2L);

    public static event EventHandler? Changed;

    public static string? TestFilePathOverride { get; set; }

    private static string FilePath
        => TestFilePathOverride ?? Path.Join(CoreData.UniGetUIUserConfigurationDirectory, "PendingReboots.json");

    public static void InvalidateCache()
    {
        lock (_lock)
        {
            _cache = null;
            _cacheStamp = (default, -2L);
        }
    }

    public static IReadOnlyList<PendingRebootEntry> GetPending()
    {
        lock (_lock) return LoadUnlocked().ToArray();
    }

    public static int PendingCount
    {
        get { lock (_lock) return LoadUnlocked().Count; }
    }

    public static bool HasPending => PendingCount > 0;

    public static bool IsPending(string managerName, string packageId)
    {
        if (string.IsNullOrEmpty(packageId)) return false;
        lock (_lock)
        {
            return LoadUnlocked().Any(entry => Matches(entry, managerName, packageId));
        }
    }

    public static IReadOnlySet<string> GetPendingKeys()
    {
        lock (_lock)
        {
            return LoadUnlocked()
                .Select(entry => KeyFor(entry.ManagerName, entry.PackageId))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static string KeyFor(string managerName, string packageId)
        => managerName + "|" + packageId;

    public static void Record(IPackage package, OperationType role)
    {
        var entry = new PendingRebootEntry
        {
            PackageId = package.Id,
            PackageName = package.Name,
            ManagerName = package.Manager.Id,
            SourceName = package.Source.Name,
            Version = role is OperationType.Update ? package.NewVersionString : package.VersionString,
            Kind = KindFor(role),
            RecordedAtUtc = DateTime.UtcNow.ToString("O"),
            BootId = BootSession.GetId(),
            UptimeTicks = BootSession.GetUptimeTicks(),
        };

        lock (_lock)
        {
            var list = LoadUnlocked();
            list.RemoveAll(existing => Matches(existing, entry.ManagerName, entry.PackageId));
            list.Insert(0, entry);
            if (list.Count > MaxEntries)
                list.RemoveRange(MaxEntries, list.Count - MaxEntries);
            SaveUnlocked();
        }

        Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void Clear(string managerName, string packageId)
    {
        bool changed;
        lock (_lock)
        {
            var list = LoadUnlocked();
            changed = list.RemoveAll(entry => Matches(entry, managerName, packageId)) > 0;
            if (changed) SaveUnlocked();
        }

        if (changed) Changed?.Invoke(null, EventArgs.Empty);
    }

    public static void ClearAll()
    {
        bool changed;
        lock (_lock)
        {
            var list = LoadUnlocked();
            changed = list.Count > 0;
            list.Clear();
            if (changed) SaveUnlocked();
        }

        if (changed) Changed?.Invoke(null, EventArgs.Empty);
    }

    private static bool Matches(PendingRebootEntry entry, string managerName, string packageId)
        => entry.PackageId.Equals(packageId, StringComparison.OrdinalIgnoreCase)
           && entry.ManagerName.Equals(managerName, StringComparison.OrdinalIgnoreCase);

    private static string KindFor(OperationType role) => role switch
    {
        OperationType.Install => "install-package",
        OperationType.Update => "update-package",
        OperationType.Uninstall => "uninstall-package",
        _ => "",
    };

    private static List<PendingRebootEntry> LoadUnlocked()
    {
        if (_cache is not null && ReadFileStamp() == _cacheStamp) return _cache;

        var stampBeforeRead = ReadFileStamp();
        var loaded = new List<PendingRebootEntry>();
        try
        {
            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                var typeInfo = PendingRebootJsonContext.Default.ListPendingRebootEntry;
                loaded = JsonSerializer.Deserialize(json, typeInfo) ?? [];
            }
        }
        catch (Exception ex)
        {
            Logger.Warn("Failed to read the pending-reboot store; starting empty");
            Logger.Warn(ex);
            loaded = [];
        }

        int rawCount = loaded.Count;
        _cache = Sanitize(loaded);
        _cacheStamp = stampBeforeRead;

        bool repaired = _cache.Count != rawCount;
        bool dropped = DropEntriesFromPreviousBootsUnlocked();
        if (repaired || dropped)
            SaveUnlocked();

        return _cache;
    }

    private static List<PendingRebootEntry> Sanitize(List<PendingRebootEntry> entries)
    {
        var result = new List<PendingRebootEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (entry is null) continue;

            entry.PackageId = OrEmpty(entry.PackageId);
            entry.PackageName = OrEmpty(entry.PackageName);
            entry.ManagerName = OrEmpty(entry.ManagerName);
            entry.SourceName = OrEmpty(entry.SourceName);
            entry.Version = OrEmpty(entry.Version);
            entry.Kind = OrEmpty(entry.Kind);
            entry.RecordedAtUtc = OrEmpty(entry.RecordedAtUtc);
            entry.BootId = OrEmpty(entry.BootId);

            if (entry.PackageId.Length == 0 || entry.ManagerName.Length == 0) continue;
            result.Add(entry);
        }

        if (result.Count != entries.Count)
            Logger.Warn($"Discarded {entries.Count - result.Count} malformed pending-reboot entries");

        return result;
    }

    private static string OrEmpty(string? value) => value ?? "";

    private static (DateTime Time, long Length) ReadFileStamp()
    {
        try
        {
            var info = new FileInfo(FilePath);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : (default, -1L);
        }
        catch
        {
            return (default, -1L);
        }
    }

    private static bool DropEntriesFromPreviousBootsUnlocked()
    {
        if (_cache is null || _cache.Count == 0) return false;

        string currentBootId = BootSession.GetId();
        long currentUptime = BootSession.GetUptimeTicks();
        int removed = _cache.RemoveAll(
            entry => RecordedBeforeCurrentBoot(entry, currentBootId, currentUptime));
        if (removed > 0)
            Logger.Info($"Discarded {removed} pending-reboot entries recorded before the current boot");
        return removed > 0;
    }

    private static bool RecordedBeforeCurrentBoot(
        PendingRebootEntry entry,
        string currentBootId,
        long currentUptimeTicks)
    {
        if (currentBootId.Length > 0 && entry.BootId.Length > 0)
            return !string.Equals(entry.BootId, currentBootId, StringComparison.OrdinalIgnoreCase);

        if (entry.UptimeTicks <= 0) return true;
        return currentUptimeTicks < entry.UptimeTicks;
    }

    private static void SaveUnlocked()
    {
        try
        {
            var typeInfo = PendingRebootJsonContext.Default.ListPendingRebootEntry;
            string json = JsonSerializer.Serialize(_cache ?? [], typeInfo);

            string temporaryPath = FilePath + ".tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, FilePath, overwrite: true);

            _cacheStamp = ReadFileStamp();
        }
        catch (Exception ex)
        {
            Logger.Warn("Failed to persist the pending-reboot store");
            Logger.Warn(ex);
        }
    }
}
