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

    public static event EventHandler? Changed;

    public static string? TestFilePathOverride { get; set; }

    private static string FilePath
        => TestFilePathOverride ?? Path.Join(CoreData.UniGetUIUserConfigurationDirectory, "PendingReboots.json");

    public static void InvalidateCache()
    {
        lock (_lock) _cache = null;
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
        if (_cache is not null) return _cache;

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

        _cache = loaded;
        if (DropEntriesFromPreviousBootsUnlocked())
            SaveUnlocked();

        return _cache;
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
            File.WriteAllText(FilePath, json);
        }
        catch (Exception ex)
        {
            Logger.Warn("Failed to persist the pending-reboot store");
            Logger.Warn(ex);
        }
    }
}
