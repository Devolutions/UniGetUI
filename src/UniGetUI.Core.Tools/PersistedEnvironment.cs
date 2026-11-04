using System.Runtime.Versioning;
using System.Text;
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
    private const int MaxExpansionPasses = 8;
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
                name => Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process)
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
        Func<string, string?> currentProcessValue
    )
    {
        var accepted = new Dictionary<string, PersistedValue?>(StringComparer.OrdinalIgnoreCase);
        var dependents = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        names.UnionWith(before.Machine.Keys);
        names.UnionWith(before.User.Keys);
        names.UnionWith(after.Machine.Keys);
        names.UnionWith(after.User.Keys);
        names.RemoveWhere(name => name.Equals(PathVariable, StringComparison.OrdinalIgnoreCase));

        foreach (string name in names)
        {
            PersistedValue? oldValue = Effective(before, name);
            PersistedValue? newValue = Effective(after, name);
            if (!SameValue(currentProcessValue(name), Resolve(oldValue, currentProcessValue)))
                continue;

            if (oldValue != newValue)
                accepted[name] = newValue;
            else if (newValue is { Expandable: true } expandable)
                dependents[name] = expandable.Raw;
        }

        var changes = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in accepted)
            changes[name] = value is { Expandable: false } text ? text.Raw : null;

        string? Staged(string name) =>
            changes.TryGetValue(name, out string? value) ? value : currentProcessValue(name);

        for (int pass = 0; pass < MaxExpansionPasses; pass++)
        {
            bool stable = true;
            foreach (var (name, value) in accepted)
            {
                if (value is not { Expandable: true } expandable)
                    continue;

                string resolved = Expand(expandable.Raw, Staged);
                if (!string.Equals(changes[name], resolved, StringComparison.Ordinal))
                {
                    changes[name] = resolved;
                    stable = false;
                }
            }

            foreach (var (name, raw) in dependents)
            {
                string resolved = Expand(raw, Staged);
                if (!SameValue(Staged(name), resolved))
                {
                    changes[name] = resolved;
                    stable = false;
                }
            }

            if (stable)
                break;
        }

        foreach (string name in dependents.Keys)
        {
            if (changes.TryGetValue(name, out string? value) && SameValue(value, currentProcessValue(name)))
                changes.Remove(name);
        }

        string? path = ComputePath(before, after, currentProcessValue, Staged);
        if (path is not null)
            changes[PathVariable] = path;

        return changes;
    }

    private static string? ComputePath(
        Snapshot before,
        Snapshot after,
        Func<string, string?> originalLookup,
        Func<string, string?> stagedLookup
    )
    {
        List<string> oldEntries = PathEntries(before, originalLookup);
        List<string> newEntries = PathEntries(after, stagedLookup);
        if (oldEntries.SequenceEqual(newEntries, PathEntryComparer.Instance))
            return null;

        var registryOwned = oldEntries.ToHashSet(PathEntryComparer.Instance);
        List<string> processEntries = [.. Split(originalLookup(PathVariable))];
        var runtimeEntries = processEntries
            .Where(entry => !registryOwned.Contains(entry))
            .ToHashSet(PathEntryComparer.Instance);
        var emitted = new HashSet<string>(PathEntryComparer.Instance);
        var result = new List<string>();
        int next = 0;

        foreach (string entry in processEntries)
        {
            if (runtimeEntries.Contains(entry))
            {
                if (emitted.Add(entry))
                    result.Add(entry);
                continue;
            }

            while (
                next < newEntries.Count
                && (emitted.Contains(newEntries[next]) || runtimeEntries.Contains(newEntries[next]))
            )
                next++;
            if (next == newEntries.Count)
                continue;

            string slot = newEntries[next++];
            emitted.Add(slot);
            result.Add(PathEntryComparer.Instance.Equals(slot, entry) ? entry : slot);
        }

        foreach (string entry in newEntries)
        {
            if (emitted.Add(entry))
                result.Add(entry);
        }

        return result.SequenceEqual(processEntries, StringComparer.Ordinal) ? null : string.Join(';', result);
    }

    private static List<string> PathEntries(Snapshot snapshot, Func<string, string?> lookup) =>
        [
            .. Split(Resolve(Lookup(snapshot.Machine, PathVariable), lookup))
                .Concat(Split(Resolve(Lookup(snapshot.User, PathVariable), lookup)))
                .Distinct(PathEntryComparer.Instance),
        ];

    private static PersistedValue? Lookup(
        IReadOnlyDictionary<string, PersistedValue> values,
        string name
    ) => values.TryGetValue(name, out PersistedValue value) ? value : null;

    private static IEnumerable<string> Split(string? value) =>
        (value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string? Resolve(PersistedValue? value, Func<string, string?> lookup) =>
        value is not { } persisted ? null
        : persisted.Expandable ? Expand(persisted.Raw, lookup)
        : persisted.Raw;

    internal static string Expand(string raw, Func<string, string?> lookup)
    {
        var result = new StringBuilder(raw.Length);
        int position = 0;
        while (position < raw.Length)
        {
            int open = raw.IndexOf('%', position);
            int close = open < 0 ? -1 : raw.IndexOf('%', open + 1);
            if (close < 0)
            {
                result.Append(raw, position, raw.Length - position);
                break;
            }

            result.Append(raw, position, open - position);
            string? value = close > open + 1 ? lookup(raw[(open + 1)..close]) : null;
            if (value is null)
            {
                result.Append(raw, open, close - open);
                position = close;
            }
            else
            {
                result.Append(value);
                position = close + 1;
            }
        }

        return result.ToString();
    }

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
