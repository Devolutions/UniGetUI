using System.Runtime.Versioning;
using Microsoft.Win32;
using UniGetUI.Core.Logging;

namespace UniGetUI.Core.Tools;

public static class PersistedEnvironment
{
    internal readonly record struct PersistedValue(string Raw, bool Expandable);

    internal sealed class Snapshot
    {
        internal Snapshot(
            IReadOnlyDictionary<string, PersistedValue> machine,
            IReadOnlyDictionary<string, PersistedValue> user
        )
        {
            Machine = machine;
            User = user;
        }

        internal IReadOnlyDictionary<string, PersistedValue> Machine { get; }
        internal IReadOnlyDictionary<string, PersistedValue> User { get; }
    }

    private const string PathVariable = "Path";
    private const string MachineEnvironmentKey =
        @"System\CurrentControlSet\Control\Session Manager\Environment";
    private const string UserEnvironmentKey = "Environment";
    private static readonly object BaselineLock = new();
    private static Snapshot? _baseline;

    public static void Initialize()
    {
        lock (BaselineLock)
            _baseline ??= Capture();
    }

    public static IReadOnlyList<string> Refresh() => Refresh(Capture);

    internal static IReadOnlyList<string> Refresh(Func<Snapshot?> capture)
    {
        lock (BaselineLock)
        {
            Snapshot? current = capture();
            if (current is null)
                return [];

            if (_baseline is null)
            {
                _baseline = current;
                return [];
            }

            var changes = ComputeChanges(
                _baseline,
                current,
                name => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process),
                Environment.ExpandEnvironmentVariables
            );
            foreach (var (name, value) in changes)
                Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.Process);

            _baseline = current;
            return [.. changes.Keys];
        }
    }

    public static void TEST_ResetBaseline() => TEST_ResetBaseline(Capture());

    internal static void TEST_ResetBaseline(Snapshot? baseline)
    {
        lock (BaselineLock)
            _baseline = baseline;
    }

    private static Snapshot? Capture()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var machine = Read(Registry.LocalMachine, MachineEnvironmentKey, required: true);
        var user = Read(Registry.CurrentUser, UserEnvironmentKey, required: false);
        return machine is null || user is null ? null : new Snapshot(machine, user);
    }

    internal static Snapshot CreateSnapshot(
        IReadOnlyDictionary<string, PersistedValue> machine,
        IReadOnlyDictionary<string, PersistedValue> user
    ) =>
        new(
            new Dictionary<string, PersistedValue>(machine, StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, PersistedValue>(user, StringComparer.OrdinalIgnoreCase)
        );

    internal static IReadOnlyDictionary<string, string?> ComputeChanges(
        Snapshot before,
        Snapshot after,
        Func<string, string?> currentProcessValue,
        Func<string, string> expand
    )
    {
        var changes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        names.UnionWith(before.Machine.Keys);
        names.UnionWith(before.User.Keys);
        names.UnionWith(after.Machine.Keys);
        names.UnionWith(after.User.Keys);

        foreach (string name in names)
        {
            if (name.Equals(PathVariable, StringComparison.OrdinalIgnoreCase))
            {
                string? path = ComputePath(before, after, currentProcessValue(name), expand);
                if (path is not null)
                    changes[name] = path;
                continue;
            }

            PersistedValue? oldValue = Effective(before, name);
            PersistedValue? newValue = Effective(after, name);
            if (oldValue == newValue)
                continue;

            if (SameValue(currentProcessValue(name), Resolve(oldValue, expand)))
                changes[name] = Resolve(newValue, expand);
        }

        return changes;
    }

    private static string? ComputePath(
        Snapshot before,
        Snapshot after,
        string? current,
        Func<string, string> expand
    )
    {
        List<string> oldEntries = [.. PathEntries(before, expand)];
        List<string> newEntries = [.. PathEntries(after, expand)];
        var removed = oldEntries
            .Except(newEntries, PathEntryComparer.Instance)
            .ToHashSet(PathEntryComparer.Instance);
        var added = newEntries.Except(oldEntries, PathEntryComparer.Instance).ToHashSet(
            PathEntryComparer.Instance
        );
        if (removed.Count == 0 && added.Count == 0)
            return null;

        List<string> result = [.. Split(current).Where(entry => !removed.Contains(entry))];
        for (int i = 0; i < newEntries.Count; i++)
        {
            string entry = newEntries[i];
            if (!added.Contains(entry) || result.Contains(entry, PathEntryComparer.Instance))
                continue;

            int insertAt = result.Count;
            for (int j = i + 1; j < newEntries.Count; j++)
            {
                int position = result.FindIndex(existing =>
                    PathEntryComparer.Instance.Equals(existing, newEntries[j])
                );
                if (position >= 0)
                {
                    insertAt = position;
                    break;
                }
            }

            result.Insert(insertAt, entry);
        }

        return string.Join(';', result);
    }

    private static IEnumerable<string> PathEntries(Snapshot snapshot, Func<string, string> expand) =>
        Split(Resolve(Lookup(snapshot.Machine, PathVariable), expand))
            .Concat(Split(Resolve(Lookup(snapshot.User, PathVariable), expand)));

    private static PersistedValue? Lookup(
        IReadOnlyDictionary<string, PersistedValue> values,
        string name
    ) => values.TryGetValue(name, out PersistedValue value) ? value : null;

    private static IEnumerable<string> Split(string? value) =>
        (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? Resolve(PersistedValue? value, Func<string, string> expand) =>
        value is not { } persisted ? null
        : persisted.Expandable ? expand(persisted.Raw)
        : persisted.Raw;

    private static bool SameValue(string? first, string? second) =>
        string.Equals(
            string.IsNullOrEmpty(first) ? null : first,
            string.IsNullOrEmpty(second) ? null : second,
            StringComparison.Ordinal
        );

    private static PersistedValue? Effective(Snapshot snapshot, string name) =>
        Lookup(snapshot.User, name) ?? Lookup(snapshot.Machine, name);

    [SupportedOSPlatform("windows")]
    private static Dictionary<string, PersistedValue>? Read(
        RegistryKey root,
        string subKey,
        bool required
    )
    {
        try
        {
            using RegistryKey? key = root.OpenSubKey(subKey, writable: false);
            var result = new Dictionary<string, PersistedValue>(StringComparer.OrdinalIgnoreCase);
            if (key is null)
                return required ? null : result;

            foreach (string name in key.GetValueNames())
            {
                if (name.Length == 0 || name.Contains('='))
                    continue;

                RegistryValueKind kind = key.GetValueKind(name);
                if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
                    continue;

                if (
                    key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)
                    is string raw
                )
                    result[name] = new PersistedValue(raw, kind is RegistryValueKind.ExpandString);
            }

            return result;
        }
        catch (Exception ex)
        {
            Logger.Warn($@"Could not read the environment from {root.Name}\{subKey}: {ex.Message}");
            return null;
        }
    }

    private sealed class PathEntryComparer : IEqualityComparer<string>
    {
        public static readonly PathEntryComparer Instance = new();

        public bool Equals(string? x, string? y) =>
            string.Equals(Normalize(x), Normalize(y), StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(string obj) =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(Normalize(obj));

        private static string Normalize(string? entry)
        {
            string value = (entry ?? "").Trim().Trim('"').Trim();
            return value.Length > 3 ? value.TrimEnd('\\', '/') : value;
        }
    }
}
