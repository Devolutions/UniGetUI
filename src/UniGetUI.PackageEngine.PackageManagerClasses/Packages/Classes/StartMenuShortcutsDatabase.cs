using System.Text;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.PackageEngine.Interfaces;

namespace UniGetUI.PackageEngine.Classes.Packages.Classes;

public static class StartMenuShortcutsDatabase
{
    public enum Status
    {
        Maintain,
        Unknown,
        Delete,
    }

    private const char RecordSeparator = '|';
    private const int MinimumMatchLength = 3;
    private const int ContainmentMatchLength = 6;
    private const string PendingShortcutsKey = "PendingStartMenuShortcuts";

    private static readonly string[] ShortcutExtensions = [".lnk", ".url"];

    private static readonly string[] ShortcutPatterns =
        ShortcutExtensions.Select(extension => "*" + extension).ToArray();

    private static readonly Lock DatabaseLock = new();

    private static readonly EnumerationOptions ShortcutEnumeration = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip =
            FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
        IgnoreInaccessible = true,
    };

    private static IReadOnlyList<string>? _cachedRoots;
    private static string? _testUserPrograms;
    private static string? _testCommonPrograms;

    public static string? TEST_UserProgramsOverride
    {
        set
        {
            _testUserPrograms = value;
            _cachedRoots = null;
        }
    }

    public static string? TEST_CommonProgramsOverride
    {
        set
        {
            _testCommonPrograms = value;
            _cachedRoots = null;
        }
    }

    private static string UserProgramsDirectory =>
        _testUserPrograms ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs);

    private static string CommonProgramsDirectory =>
        _testCommonPrograms
        ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms);

    public static string GetIdForPackage(IPackage package)
    {
        return IgnoredUpdatesDatabase.GetIgnoredIdForPackage(package);
    }

    public static IReadOnlyList<string> GetShortcutRoots()
    {
        if (_cachedRoots is not null)
            return _cachedRoots;

        List<string> roots = [];

        foreach (string root in new[] { UserProgramsDirectory, CommonProgramsDirectory })
        {
            if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
                continue;

            if (!roots.Any(known => AreSamePath(known, root)))
                roots.Add(root);
        }

        _cachedRoots = roots;
        return roots;
    }

    /// Whether the given path lives under a Start Menu directory UniGetUI manages.
    public static bool IsManagedShortcutPath(string shortcutPath)
    {
        return GetShortcutRoots()
            .Any(root => IsUnder(root, shortcutPath) && !LeavesTheStartMenu(root, shortcutPath));
    }

    private static bool LeavesTheStartMenu(string root, string path)
    {
        try
        {
            string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            string? current = Path.GetFullPath(path);

            while (
                current is not null
                && !string.Equals(
                    Path.TrimEndingDirectorySeparator(current),
                    normalizedRoot,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                if (IsReparsePoint(current))
                    return true;

                current = Path.GetDirectoryName(current);
            }

            return false;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private static bool IsReparsePoint(string directory)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            return info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint);
        }
        catch (Exception)
        {
            return true;
        }
    }

    public static bool IsShortcutFile(string path)
    {
        return ShortcutExtensions.Contains(
            Path.GetExtension(path),
            StringComparer.OrdinalIgnoreCase
        );
    }

    private static bool IsUnderUserPrograms(string path)
    {
        string root = UserProgramsDirectory;
        return !string.IsNullOrEmpty(root) && IsUnder(root, path);
    }

    public static List<string> GetShortcutsOnDisk()
    {
        List<string> shortcuts = [];

        foreach (string root in GetShortcutRoots())
        {
            try
            {
                foreach (string pattern in ShortcutPatterns)
                {
                    shortcuts.AddRange(
                        Directory.EnumerateFiles(root, pattern, ShortcutEnumeration)
                    );
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Failed to load the Start Menu shortcuts under {root}");
                Logger.Error(ex);
            }
        }

        return shortcuts;
    }

    public static IReadOnlyDictionary<string, string> GetRules()
    {
        return (
                Settings.GetDictionary<string, string>(Settings.K.StartMenuShortcutFolders)
                ?? new Dictionary<string, string?>()
            )
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!);
    }

    public static string? GetRule(string packageId)
    {
        foreach (var rule in GetRules())
        {
            if (string.Equals(rule.Key, packageId, StringComparison.OrdinalIgnoreCase))
                return rule.Value;
        }

        return null;
    }

    public static bool HasRule(IPackage package)
    {
        return GetRule(GetIdForPackage(package)) is not null;
    }

    public static void SetRule(string packageId, string folder)
    {
        lock (DatabaseLock)
        {
            if (string.IsNullOrWhiteSpace(folder))
            {
                RemoveRule(packageId);
                return;
            }

            DropRuleKeys(packageId, StringComparison.Ordinal);

            Settings.SetDictionaryItem(
                Settings.K.StartMenuShortcutFolders,
                packageId,
                folder.Trim()
            );
        }
    }

    public static bool RemoveRule(string packageId)
    {
        lock (DatabaseLock)
        {
            return DropRuleKeys(packageId, null) > 0;
        }
    }

    private static int DropRuleKeys(string packageId, StringComparison? keep)
    {
        int dropped = 0;

        foreach (
            string key in GetRules()
                .Keys.Where(key =>
                    string.Equals(key, packageId, StringComparison.OrdinalIgnoreCase)
                    && (keep is null || !string.Equals(key, packageId, keep.Value))
                )
                .ToList()
        )
        {
            Settings.RemoveDictionaryKey<string, string>(
                Settings.K.StartMenuShortcutFolders,
                key
            );
            dropped++;
        }

        return dropped;
    }

    public static IReadOnlyDictionary<string, bool> GetVerdicts()
    {
        return Settings.GetDictionary<string, bool>(Settings.K.DeletableStartMenuShortcuts)
            ?? new Dictionary<string, bool>();
    }

    public static Status GetStatus(string shortcutPath)
    {
        foreach (var verdict in GetVerdicts())
        {
            if (!string.Equals(verdict.Key, shortcutPath, StringComparison.OrdinalIgnoreCase))
                continue;

            return verdict.Value ? Status.Delete : Status.Maintain;
        }

        return Status.Unknown;
    }

    public static void SetStatus(string shortcutPath, Status status)
    {
        lock (DatabaseLock)
        {
            foreach (
                string key in GetVerdicts()
                    .Keys.Where(key =>
                        string.Equals(key, shortcutPath, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(key, shortcutPath, StringComparison.Ordinal)
                    )
                    .ToList()
            )
            {
                Settings.RemoveDictionaryKey<string, bool>(
                    Settings.K.DeletableStartMenuShortcuts,
                    key
                );
            }

            if (status is Status.Unknown)
                Settings.RemoveDictionaryKey<string, bool>(
                    Settings.K.DeletableStartMenuShortcuts,
                    shortcutPath
                );
            else
                Settings.SetDictionaryItem(
                    Settings.K.DeletableStartMenuShortcuts,
                    shortcutPath,
                    status is Status.Delete
                );
        }
    }

    public static List<string> GetAllShortcuts()
    {
        var shortcuts = GetShortcutsOnDisk();

        foreach (var verdict in GetVerdicts())
        {
            if (!shortcuts.Contains(verdict.Key, StringComparer.OrdinalIgnoreCase))
                shortcuts.Add(verdict.Key);
        }

        return shortcuts;
    }

    public static IReadOnlyList<string> GetAllRelocatedShortcuts()
    {
        return GetRelocationRecords().Values.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<string> GetTrackedShortcuts()
    {
        List<string> shortcuts = [];

        foreach (
            string shortcut in GetVerdicts()
                .Keys.Concat(GetAllRelocatedShortcuts())
                .Concat(GetPendingShortcuts().Select(pending => pending.ShortcutPath))
        )
        {
            if (!shortcuts.Contains(shortcut, StringComparer.OrdinalIgnoreCase))
                shortcuts.Add(shortcut);
        }

        return shortcuts;
    }

    public static bool ShouldTrackShortcuts(IPackage package)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        return HasRule(package)
            || Settings.Get(Settings.K.AskAboutNewStartMenuShortcuts)
            || GetVerdicts().Any(verdict => verdict.Value);
    }

    public static void MarkPending(string packageId, string shortcutPath)
    {
        lock (DatabaseLock)
        {
            if (FindPendingRecords(packageId, shortcutPath).Count > 0)
                return;

            Logger.Info($"Marking the Start Menu shortcut {shortcutPath} to be asked about");
            Settings.AddToList(PendingShortcutsKey, BuildRecordKey(packageId, shortcutPath));
        }
    }

    public static IReadOnlyList<(string PackageId, string ShortcutPath)> GetPendingShortcuts()
    {
        lock (DatabaseLock)
        {
            List<(string, string)> pending = [];

            foreach (string record in Settings.GetList<string>(PendingShortcutsKey) ?? [])
            {
                var parsed = ParseRecordKey(record);

                if (parsed is null || !File.Exists(parsed.Value.OriginalPath))
                {
                    Settings.RemoveFromList(PendingShortcutsKey, record);
                    continue;
                }

                pending.Add((parsed.Value.PackageId, parsed.Value.OriginalPath));
            }

            return pending;
        }
    }

    public static bool RemoveFromPending(string packageId, string shortcutPath)
    {
        lock (DatabaseLock)
        {
            bool removed = false;

            foreach (string record in FindPendingRecords(packageId, shortcutPath))
                removed |= Settings.RemoveFromList(PendingShortcutsKey, record);

            return removed;
        }
    }

    private static IReadOnlyList<string> FindPendingRecords(string packageId, string shortcutPath)
    {
        List<string> records = [];

        foreach (string record in Settings.GetList<string>(PendingShortcutsKey) ?? [])
        {
            var parsed = ParseRecordKey(record);
            if (
                parsed is not null
                && string.Equals(
                    parsed.Value.PackageId,
                    packageId,
                    StringComparison.OrdinalIgnoreCase
                )
                && string.Equals(
                    parsed.Value.OriginalPath,
                    shortcutPath,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                records.Add(record);
        }

        return records;
    }

    public static int RemovePendingShortcuts(string shortcutPath)
    {
        lock (DatabaseLock)
        {
            int removed = 0;

            foreach (var pending in GetPendingShortcuts())
            {
                if (
                    string.Equals(
                        pending.ShortcutPath,
                        shortcutPath,
                        StringComparison.OrdinalIgnoreCase
                    )
                    && RemoveFromPending(pending.PackageId, pending.ShortcutPath)
                )
                    removed++;
            }

            return removed;
        }
    }

    public static void ClearPendingShortcuts()
    {
        lock (DatabaseLock)
        {
            Settings.ClearList(PendingShortcutsKey);
        }
    }

    /// The folders that already exist under the user's Start Menu Programs directory,
    /// as the relative names a rule stores.
    public static IReadOnlyList<string> GetUserProgramFolders()
    {
        string root = UserProgramsDirectory;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
            return [];

        try
        {
            return Directory
                .EnumerateDirectories(root, "*", ShortcutEnumeration)
                .Select(directory => Path.GetRelativePath(root, directory))
                .OrderBy(folder => folder, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.Error($"Failed to list the Start Menu folders under {root}");
            Logger.Error(ex);
            return [];
        }
    }

    public static string? ResolveTargetDirectory(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return null;

        string root = UserProgramsDirectory;
        if (string.IsNullOrEmpty(root))
            return null;

        string relative = folder
            .Trim()
            .Replace('/', Path.DirectorySeparatorChar)
            .Trim(Path.DirectorySeparatorChar);

        if (relative.Length is 0 || Path.IsPathRooted(relative))
            return null;

        if (relative.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return null;

        if (relative.Split(Path.DirectorySeparatorChar).Any(segment => segment is "" or "." or ".."))
            return null;

        try
        {
            string resolved = Path.GetFullPath(Path.Combine(root, relative));
            return IsUnder(root, resolved) && !LeavesTheStartMenu(root, resolved)
                ? resolved
                : null;
        }
        catch (Exception ex)
        {
            Logger.Warn($"The Start Menu folder {folder} could not be resolved: {ex.Message}");
            return null;
        }
    }

    public static IReadOnlyList<(
        string OriginalPath,
        string RelocatedPath
    )> GetRelocationsForPackage(string packageId)
    {
        List<(string, string)> relocations = [];

        foreach (var record in GetRelocationRecords())
        {
            var parsed = ParseRecordKey(record.Key);
            if (
                parsed is null
                || !string.Equals(
                    parsed.Value.PackageId,
                    packageId,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                continue;

            relocations.Add((parsed.Value.OriginalPath, record.Value));
        }

        return relocations;
    }

    public static int ReplayRelocations(string packageId)
    {
        lock (DatabaseLock)
        {
            string? targetDirectory = ResolveTargetDirectory(GetRule(packageId));
            int relocated = 0;

            foreach (
                (string originalPath, string relocatedPath) in GetRelocationsForPackage(packageId)
            )
            {
                string destination = targetDirectory is null
                    ? relocatedPath
                    : Path.Combine(targetDirectory, Path.GetFileName(relocatedPath));

                if (!File.Exists(originalPath) || AreSamePath(originalPath, destination))
                    continue;

                if (MoveShortcut(originalPath, destination, true) is not { } finalDestination)
                    continue;

                if (!AreSamePath(finalDestination, relocatedPath))
                    AddRelocationRecord(packageId, originalPath, finalDestination);

                relocated++;
            }

            return relocated;
        }
    }

    public static int RebaseRelocations(string packageId)
    {
        lock (DatabaseLock)
        {
            string? targetDirectory = ResolveTargetDirectory(GetRule(packageId));
            if (targetDirectory is null)
                return 0;

            int relocated = 0;

            foreach (
                (string originalPath, string relocatedPath) in GetRelocationsForPackage(packageId)
            )
            {
                if (!File.Exists(relocatedPath) || IsUnder(targetDirectory, relocatedPath))
                    continue;

                string destination = Path.Combine(
                    targetDirectory,
                    Path.GetFileName(relocatedPath)
                );

                if (MoveShortcut(relocatedPath, destination) is not { } finalDestination)
                    continue;

                AddRelocationRecord(packageId, originalPath, finalDestination);
                relocated++;
            }

            return relocated;
        }
    }

    public static int HandleNewShortcuts(IPackage package, IReadOnlyList<string> previousShortcuts)
    {
        lock (DatabaseLock)
        {
            if (!OperatingSystem.IsWindows())
                return 0;

            string packageId = GetIdForPackage(package);
            string? rule = GetRule(packageId);
            string? targetDirectory = rule is null ? null : ResolveTargetDirectory(rule);

            if (rule is not null && targetDirectory is null)
            {
                Logger.Warn(
                    $"The Start Menu folder {{folder={rule}}} set for {packageId} is not a valid Start Menu subfolder, no shortcut will be relocated"
                );
            }

            bool askAboutNewShortcuts = Settings.Get(Settings.K.AskAboutNewStartMenuShortcuts);
            var identifiers = GetIdentifiers(package);

            // Recorded destinations are only replayed while the package still has a folder:
            // dropping the folder has to stop the relocations, not just the new ones.
            int handled = rule is null ? 0 : ReplayRelocations(packageId);
            HashSet<string> previous = new(previousShortcuts, StringComparer.OrdinalIgnoreCase);

            foreach (string shortcut in GetShortcutsOnDisk())
            {
                Status status = GetStatus(shortcut);

                if (status is Status.Delete)
                {
                    if (DeleteFromDisk(shortcut))
                        handled++;
                    continue;
                }

                if (previous.Contains(shortcut) || IsClaimedByAnotherPackage(packageId, shortcut))
                    continue;

                if (!IsPlausibleMatch(shortcut, identifiers))
                {
                    Logger.Info(
                        $"The new Start Menu shortcut {shortcut} will not be handled, since it does not seem to belong to {packageId}"
                    );
                    continue;
                }

                if (AnotherPackageMatchesBetter(packageId, shortcut, identifiers))
                {
                    Logger.Info(
                        $"The new Start Menu shortcut {shortcut} will not be handled, since another package resembles it more than {packageId}"
                    );
                    continue;
                }

                if (targetDirectory is not null)
                {
                    if (IsUnder(targetDirectory, shortcut))
                        continue;

                    if (!IsUnderUserPrograms(shortcut))
                    {
                        Logger.Warn(
                            $"The Start Menu shortcut {shortcut} is shared with every user of this machine and will not be relocated"
                        );
                        continue;
                    }

                    string destination = Path.Combine(targetDirectory, Path.GetFileName(shortcut));
                    if (MoveShortcut(shortcut, destination) is not { } finalDestination)
                        continue;

                    AddRelocationRecord(packageId, shortcut, finalDestination);
                    handled++;
                    continue;
                }

                if (askAboutNewShortcuts && status is Status.Unknown)
                    MarkPending(packageId, shortcut);
            }

            return handled;
        }
    }

    public static IReadOnlyList<string> FindRelocatableShortcuts(
        string packageId,
        IReadOnlyList<string>? shortcutsOnDisk = null
    )
    {
        if (!OperatingSystem.IsWindows())
            return [];

        var identifiers = GetIdentifiers(packageId);
        if (identifiers.Count is 0)
            return [];

        string? targetDirectory = ResolveTargetDirectory(GetRule(packageId));
        HashSet<string> alreadyRelocated = new(
            GetRelocationsForPackage(packageId).Select(relocation => relocation.RelocatedPath),
            StringComparer.OrdinalIgnoreCase
        );

        List<string> candidates = [];

        foreach (string shortcut in shortcutsOnDisk ?? GetShortcutsOnDisk())
        {
            if (alreadyRelocated.Contains(shortcut) || !IsUnderUserPrograms(shortcut))
                continue;

            if (IsClaimedByAnotherPackage(packageId, shortcut))
                continue;

            if (targetDirectory is not null && IsUnder(targetDirectory, shortcut))
                continue;

            if (
                IsPlausibleMatch(shortcut, identifiers)
                && !AnotherPackageMatchesBetter(packageId, shortcut, identifiers)
            )
                candidates.Add(shortcut);
        }

        return candidates;
    }

    public static int ApplyRule(string packageId, IEnumerable<string> shortcutPaths)
    {
        return ApplyRule(packageId, shortcutPaths, out _);
    }

    public static int ApplyRule(
        string packageId,
        IEnumerable<string> shortcutPaths,
        out IReadOnlyList<string> handledPaths
    )
    {
        return ApplyRule(
            packageId,
            shortcutPaths.Select(path => (path, (string?)null)),
            out handledPaths
        );
    }

    public static int ApplyRule(
        string packageId,
        IEnumerable<(string Path, string? NewName)> shortcuts
    )
    {
        return ApplyRule(packageId, shortcuts, out _);
    }

    /// <param name="shortcuts">
    /// The shortcuts to relocate, each with the name it should take in the target folder.
    /// A null or blank name keeps the name the shortcut already has.
    /// </param>
    /// <param name="handledPaths">
    /// The given shortcuts that need no further attempt, either because they were relocated
    /// or because the rule has nothing left to do with them. A shortcut that could not be
    /// moved is left out, so that the caller can keep it and retry it later.
    /// </param>
    public static int ApplyRule(
        string packageId,
        IEnumerable<(string Path, string? NewName)> shortcuts,
        out IReadOnlyList<string> handledPaths
    )
    {
        List<string> handled = [];
        handledPaths = handled;

        lock (DatabaseLock)
        {
            string? targetDirectory = ResolveTargetDirectory(GetRule(packageId));
            if (targetDirectory is null)
                return 0;

            int relocated = 0;

            foreach ((string shortcut, string? newName) in shortcuts)
            {
                if (!File.Exists(shortcut))
                {
                    handled.Add(shortcut);
                    continue;
                }

                string fileName = BuildFileName(shortcut, newName);
                bool isRename = !string.Equals(
                    fileName,
                    Path.GetFileName(shortcut),
                    StringComparison.Ordinal
                );

                if (!isRename && IsUnder(targetDirectory, shortcut))
                {
                    handled.Add(shortcut);
                    continue;
                }

                if (!IsUnderUserPrograms(shortcut))
                {
                    Logger.Warn(
                        $"The Start Menu shortcut {shortcut} is shared with every user of this machine and will not be relocated"
                    );
                    handled.Add(shortcut);
                    continue;
                }

                string destination = Path.Combine(targetDirectory, fileName);
                if (AreSamePath(shortcut, destination))
                {
                    handled.Add(shortcut);
                    continue;
                }

                if (MoveShortcut(shortcut, destination) is not { } finalDestination)
                    continue;

                AddRelocationRecord(packageId, shortcut, finalDestination);
                handled.Add(shortcut);
                relocated++;
            }

            return relocated;
        }
    }

    /// The name a relocated shortcut takes, keeping its original extension and
    /// refusing anything that is not a plain file name.
    public static string BuildFileName(string originalPath, string? newName)
    {
        string originalName = Path.GetFileName(originalPath);

        if (string.IsNullOrWhiteSpace(newName))
            return originalName;

        var invalid = Path.GetInvalidFileNameChars();
        string cleaned = new string(
            newName.Trim().Where(character => !invalid.Contains(character)).ToArray()
        ).Trim(' ', '.');

        if (cleaned.Length is 0)
            return originalName;

        string extension = Path.GetExtension(originalName);

        return cleaned.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
            ? cleaned
            : cleaned + extension;
    }

    public static int CleanupForPackage(string packageId)
    {
        lock (DatabaseLock)
        {
            if (!OperatingSystem.IsWindows())
                return 0;

            int deleted = 0;

            foreach (
                (string originalPath, string relocatedPath) in GetRelocationsForPackage(packageId)
            )
            {
                if (File.Exists(relocatedPath))
                {
                    if (!DeleteFromDisk(relocatedPath))
                        continue;

                    deleted++;
                }

                RemoveRelocationRecord(packageId, originalPath);
            }

            return deleted;
        }
    }

    public static string? MoveShortcut(
        string originalPath,
        string destinationPath,
        bool overwrite = false
    )
    {
        try
        {
            string? destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrEmpty(destinationDirectory))
                return null;

            Directory.CreateDirectory(destinationDirectory);

            string finalDestination = overwrite
                ? destinationPath
                : GetFreeDestination(destinationPath);

            File.Move(originalPath, finalDestination, overwrite);
            Logger.Info($"Relocated the Start Menu shortcut {originalPath} to {finalDestination}");

            PruneEmptyDirectories(Path.GetDirectoryName(originalPath));
            return finalDestination;
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.Warn(
                $"UniGetUI is not allowed to relocate the Start Menu shortcut {{shortcutPath={originalPath}}}: {ex.Message}"
            );
            return null;
        }
        catch (Exception ex)
        {
            Logger.Error(
                $"Failed to relocate the Start Menu shortcut {{shortcutPath={originalPath}}}"
            );
            Logger.Error(ex);
            return null;
        }
    }

    public static bool DeleteFromDisk(string shortcutPath)
    {
        Logger.Info("Deleting Start Menu shortcut " + shortcutPath);
        try
        {
            File.Delete(shortcutPath);
            PruneEmptyDirectories(Path.GetDirectoryName(shortcutPath));
            return true;
        }
        catch (Exception e)
        {
            Logger.Error(
                $"Failed to delete the Start Menu shortcut {{shortcutPath={shortcutPath}}}: {e.Message}"
            );
            return false;
        }
    }

    public static void ResetShortcutStatuses()
    {
        lock (DatabaseLock)
        {
            Settings.ClearDictionary(Settings.K.DeletableStartMenuShortcuts);
        }
    }

    public static void ResetDatabase()
    {
        lock (DatabaseLock)
        {
            Settings.ClearDictionary(Settings.K.StartMenuShortcutFolders);
            Settings.ClearDictionary(Settings.K.RelocatedStartMenuShortcuts);
            Settings.ClearDictionary(Settings.K.DeletableStartMenuShortcuts);
            ClearPendingShortcuts();
        }
    }

    private static IReadOnlyDictionary<string, string> GetRelocationRecords()
    {
        return (
                Settings.GetDictionary<string, string>(Settings.K.RelocatedStartMenuShortcuts)
                ?? new Dictionary<string, string?>()
            )
            .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value!);
    }

    private static bool IsClaimedByAnotherPackage(string packageId, string shortcutPath)
    {
        foreach (var record in GetRelocationRecords())
        {
            var parsed = ParseRecordKey(record.Key);
            if (
                parsed is null
                || string.Equals(
                    parsed.Value.PackageId,
                    packageId,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                continue;

            if (
                string.Equals(record.Value, shortcutPath, StringComparison.OrdinalIgnoreCase)
                || string.Equals(
                    parsed.Value.OriginalPath,
                    shortcutPath,
                    StringComparison.OrdinalIgnoreCase
                )
            )
                return true;
        }

        return GetPendingShortcuts()
            .Any(pending =>
                !string.Equals(
                    pending.PackageId,
                    packageId,
                    StringComparison.OrdinalIgnoreCase
                )
                && string.Equals(
                    pending.ShortcutPath,
                    shortcutPath,
                    StringComparison.OrdinalIgnoreCase
                )
            );
    }

    private static void AddRelocationRecord(
        string packageId,
        string originalPath,
        string relocatedPath
    )
    {
        string record = BuildRecordKey(packageId, originalPath);
        DropRelocationRecords(packageId, originalPath, StringComparison.Ordinal);

        Settings.SetDictionaryItem(
            Settings.K.RelocatedStartMenuShortcuts,
            record,
            relocatedPath
        );
    }

    private static void RemoveRelocationRecord(string packageId, string originalPath)
    {
        DropRelocationRecords(packageId, originalPath, null);
    }

    private static void DropRelocationRecords(
        string packageId,
        string originalPath,
        StringComparison? keep
    )
    {
        string record = BuildRecordKey(packageId, originalPath);

        foreach (
            string key in GetRelocationRecords()
                .Keys.Where(key =>
                    string.Equals(key, record, StringComparison.OrdinalIgnoreCase)
                    && (keep is null || !string.Equals(key, record, keep.Value))
                )
                .ToList()
        )
        {
            Settings.RemoveDictionaryKey<string, string>(
                Settings.K.RelocatedStartMenuShortcuts,
                key
            );
        }
    }

    private static string GetFreeDestination(string destinationPath)
    {
        if (!File.Exists(destinationPath))
            return destinationPath;

        string? directory = Path.GetDirectoryName(destinationPath);
        string name = Path.GetFileNameWithoutExtension(destinationPath);
        string extension = Path.GetExtension(destinationPath);

        for (int suffix = 2; suffix < 100; suffix++)
        {
            string candidate = Path.Combine(
                directory ?? "",
                $"{name} ({suffix}){extension}"
            );

            if (!File.Exists(candidate))
            {
                Logger.Warn(
                    $"A Start Menu shortcut already occupies {destinationPath}, using {candidate} instead"
                );
                return candidate;
            }
        }

        return destinationPath;
    }

    private static string BuildRecordKey(string packageId, string originalPath)
    {
        return $"{packageId}{RecordSeparator}{originalPath}";
    }

    private static (string PackageId, string OriginalPath)? ParseRecordKey(string recordKey)
    {
        int separatorIndex = recordKey.IndexOf(RecordSeparator);
        if (separatorIndex <= 0 || separatorIndex == recordKey.Length - 1)
            return null;

        return (recordKey[..separatorIndex], recordKey[(separatorIndex + 1)..]);
    }

    private static IReadOnlyList<MatchText> GetIdentifiers(IPackage package)
    {
        return BuildIdentifiers(
            [
                package.Name,
                package.Id,
                package.Id.Split('.')[^1],
                package.Id.Split('/')[^1],
            ]
        );
    }

    private static IReadOnlyList<MatchText> GetIdentifiers(string packageId)
    {
        int separatorIndex = packageId.IndexOf('\\');
        string id = separatorIndex >= 0 ? packageId[(separatorIndex + 1)..] : packageId;

        return BuildIdentifiers([id, id.Split('.')[^1], id.Split('/')[^1]]);
    }

    private static IReadOnlyList<MatchText> BuildIdentifiers(IEnumerable<string> values)
    {
        return values
            .Select(BuildMatchText)
            .Where(identifier => identifier.Value.Length >= MinimumMatchLength)
            .ToList();
    }

    private static bool IsPlausibleMatch(
        string shortcutPath,
        IReadOnlyList<MatchText> identifiers
    )
    {
        return GetMatchDistance(shortcutPath, identifiers) < int.MaxValue;
    }

    private static int GetMatchDistance(
        string shortcutPath,
        IReadOnlyList<MatchText> identifiers
    )
    {
        int distance = int.MaxValue;

        foreach (MatchText candidate in GetMatchCandidates(shortcutPath))
        {
            foreach (MatchText identifier in identifiers)
            {
                if (AreRelated(candidate, identifier))
                    distance = Math.Min(
                        distance,
                        Math.Abs(candidate.Value.Length - identifier.Value.Length)
                    );
            }
        }

        return distance;
    }

    private static IReadOnlyList<MatchText> GetMatchCandidates(string shortcutPath)
    {
        var roots = GetShortcutRoots();
        List<string> candidates = [Path.GetFileNameWithoutExtension(shortcutPath)];

        string? parentDirectory = Path.GetDirectoryName(shortcutPath);
        if (
            !string.IsNullOrEmpty(parentDirectory)
            && !roots.Any(root => AreSamePath(root, parentDirectory))
        )
            candidates.Add(Path.GetFileName(parentDirectory));

        return candidates
            .Select(BuildMatchText)
            .Where(candidate => candidate.Value.Length >= MinimumMatchLength)
            .ToList();
    }

    private static bool AnotherPackageMatchesBetter(
        string packageId,
        string shortcutPath,
        IReadOnlyList<MatchText> identifiers
    )
    {
        int distance = GetMatchDistance(shortcutPath, identifiers);
        if (distance is 0)
            return false;

        foreach (string knownId in GetKnownPackageIds())
        {
            if (string.Equals(knownId, packageId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (GetMatchDistance(shortcutPath, GetIdentifiers(knownId)) < distance)
                return true;
        }

        return false;
    }

    private static IReadOnlyList<string> GetKnownPackageIds()
    {
        List<string> packageIds = [.. GetRules().Keys];

        foreach (
            string packageId in GetRelocationRecords()
                .Keys.Select(ParseRecordKey)
                .Where(parsed => parsed is not null)
                .Select(parsed => parsed!.Value.PackageId)
                .Concat(GetPendingShortcuts().Select(pending => pending.PackageId))
        )
        {
            if (!packageIds.Contains(packageId, StringComparer.OrdinalIgnoreCase))
                packageIds.Add(packageId);
        }

        return packageIds;
    }

    private sealed class MatchText(string value, HashSet<int> boundaries)
    {
        public string Value { get; } = value;

        public HashSet<int> Boundaries { get; } = boundaries;
    }

    private static MatchText BuildMatchText(string value)
    {
        var text = new StringBuilder(value.Length);
        HashSet<int> boundaries = [0];

        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character))
                text.Append(char.ToLowerInvariant(character));
            else
                boundaries.Add(text.Length);
        }

        boundaries.Add(text.Length);
        return new MatchText(text.ToString(), boundaries);
    }

    private static bool AreRelated(MatchText candidate, MatchText identifier)
    {
        if (candidate.Value.Equals(identifier.Value, StringComparison.Ordinal))
            return true;

        if (StartsAtBoundary(candidate, identifier) || StartsAtBoundary(identifier, candidate))
            return true;

        if (Math.Min(candidate.Value.Length, identifier.Value.Length) < ContainmentMatchLength)
            return false;

        return ContainsAtBoundaries(candidate, identifier)
            || ContainsAtBoundaries(identifier, candidate);
    }

    private static bool StartsAtBoundary(MatchText text, MatchText part)
    {
        return text.Value.StartsWith(part.Value, StringComparison.Ordinal)
            && text.Boundaries.Contains(part.Value.Length);
    }

    private static bool ContainsAtBoundaries(MatchText text, MatchText part)
    {
        for (
            int start = text.Value.IndexOf(part.Value, StringComparison.Ordinal);
            start >= 0;
            start = text.Value.IndexOf(part.Value, start + 1, StringComparison.Ordinal)
        )
        {
            if (
                text.Boundaries.Contains(start)
                && text.Boundaries.Contains(start + part.Value.Length)
            )
                return true;
        }

        return false;
    }

    private static void PruneEmptyDirectories(string? directory)
    {
        var roots = GetShortcutRoots();
        string? current = directory;

        while (!string.IsNullOrEmpty(current) && Directory.Exists(current))
        {
            string candidate = current;

            if (roots.Any(root => AreSamePath(root, candidate)))
                return;

            if (!IsUnderUserPrograms(candidate) || IsReparsePoint(candidate))
                return;

            string? parent = Path.GetDirectoryName(candidate);

            try
            {
                if (Directory.EnumerateFileSystemEntries(candidate).Any())
                    return;

                Directory.Delete(candidate);
                Logger.Info($"Deleted the empty Start Menu folder {candidate}");
            }
            catch (Exception ex)
            {
                Logger.Warn(
                    $"Failed to delete the empty Start Menu folder {{folder={candidate}}}: {ex.Message}"
                );
                return;
            }

            current = parent;
        }
    }

    private static bool IsUnder(string root, string path)
    {
        try
        {
            string normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            string normalizedPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

            return normalizedPath.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase
            );
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool AreSamePath(string first, string second)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
                StringComparison.OrdinalIgnoreCase
            );
        }
        catch (Exception)
        {
            return false;
        }
    }
}
