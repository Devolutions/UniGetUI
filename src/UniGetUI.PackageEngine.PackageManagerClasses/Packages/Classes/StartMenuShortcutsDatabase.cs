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

    private static readonly string[] ShortcutPatterns = ["*.lnk", "*.url"];

    private static readonly EnumerationOptions ShortcutEnumeration = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
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
        return GetShortcutRoots().Any(root => IsUnder(root, shortcutPath));
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
        string? folder = Settings.GetDictionaryItem<string, string>(
            Settings.K.StartMenuShortcutFolders,
            packageId
        );

        return string.IsNullOrWhiteSpace(folder) ? null : folder;
    }

    public static bool HasRule(IPackage package)
    {
        return GetRule(GetIdForPackage(package)) is not null;
    }

    public static void SetRule(string packageId, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
        {
            RemoveRule(packageId);
            return;
        }

        Settings.SetDictionaryItem(Settings.K.StartMenuShortcutFolders, packageId, folder.Trim());
    }

    public static bool RemoveRule(string packageId)
    {
        if (
            !Settings.DictionaryContainsKey<string, string>(
                Settings.K.StartMenuShortcutFolders,
                packageId
            )
        )
            return false;

        Settings.RemoveDictionaryKey<string, string>(
            Settings.K.StartMenuShortcutFolders,
            packageId
        );
        return true;
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
        string record = BuildRecordKey(packageId, shortcutPath);
        if (Settings.ListContains(PendingShortcutsKey, record))
            return;

        Logger.Info($"Marking the Start Menu shortcut {shortcutPath} to be asked about");
        Settings.AddToList(PendingShortcutsKey, record);
    }

    public static IReadOnlyList<(string PackageId, string ShortcutPath)> GetPendingShortcuts()
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

    public static bool RemoveFromPending(string packageId, string shortcutPath)
    {
        return Settings.RemoveFromList(
            PendingShortcutsKey,
            BuildRecordKey(packageId, shortcutPath)
        );
    }

    public static void ClearPendingShortcuts()
    {
        Settings.ClearList(PendingShortcutsKey);
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
            return IsUnder(root, resolved) ? resolved : null;
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
        int relocated = 0;

        foreach ((string originalPath, string relocatedPath) in GetRelocationsForPackage(packageId))
        {
            if (!File.Exists(originalPath) || AreSamePath(originalPath, relocatedPath))
                continue;

            if (MoveShortcut(originalPath, relocatedPath, true) is not null)
                relocated++;
        }

        return relocated;
    }

    public static int HandleNewShortcuts(IPackage package, IReadOnlyList<string> previousShortcuts)
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

            if (previous.Contains(shortcut))
                continue;

            if (!IsPlausibleMatch(shortcut, identifiers))
            {
                Logger.Info(
                    $"The new Start Menu shortcut {shortcut} will not be handled, since it does not seem to belong to {packageId}"
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

            if (targetDirectory is not null && IsUnder(targetDirectory, shortcut))
                continue;

            if (IsPlausibleMatch(shortcut, identifiers))
                candidates.Add(shortcut);
        }

        return candidates;
    }

    public static int ApplyRule(string packageId, IEnumerable<string> shortcutPaths)
    {
        return ApplyRule(packageId, shortcutPaths.Select(path => (path, (string?)null)));
    }

    /// <param name="shortcuts">
    /// The shortcuts to relocate, each with the name it should take in the target folder.
    /// A null or blank name keeps the name the shortcut already has.
    /// </param>
    public static int ApplyRule(
        string packageId,
        IEnumerable<(string Path, string? NewName)> shortcuts
    )
    {
        string? targetDirectory = ResolveTargetDirectory(GetRule(packageId));
        if (targetDirectory is null)
            return 0;

        int relocated = 0;

        foreach ((string shortcut, string? newName) in shortcuts)
        {
            if (!File.Exists(shortcut))
                continue;

            string fileName = BuildFileName(shortcut, newName);
            bool isRename = !string.Equals(
                fileName,
                Path.GetFileName(shortcut),
                StringComparison.Ordinal
            );

            if (!isRename && IsUnder(targetDirectory, shortcut))
                continue;

            if (!IsUnderUserPrograms(shortcut))
            {
                Logger.Warn(
                    $"The Start Menu shortcut {shortcut} is shared with every user of this machine and will not be relocated"
                );
                continue;
            }

            string destination = Path.Combine(targetDirectory, fileName);
            if (AreSamePath(shortcut, destination))
                continue;

            if (MoveShortcut(shortcut, destination) is not { } finalDestination)
                continue;

            AddRelocationRecord(packageId, shortcut, finalDestination);
            relocated++;
        }

        return relocated;
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
        if (!OperatingSystem.IsWindows())
            return 0;

        int deleted = 0;

        foreach ((string originalPath, string relocatedPath) in GetRelocationsForPackage(packageId))
        {
            if (File.Exists(relocatedPath) && DeleteFromDisk(relocatedPath))
                deleted++;

            RemoveRelocationRecord(packageId, originalPath);
        }

        return deleted;
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

    /// Forgets that UniGetUI had relocated a shortcut, for use once that shortcut is
    /// gone: the record only existed to remember where the file had been put.
    public static int ForgetRelocationsTo(string relocatedPath)
    {
        int forgotten = 0;

        foreach (var record in GetRelocationRecords())
        {
            if (!string.Equals(record.Value, relocatedPath, StringComparison.OrdinalIgnoreCase))
                continue;

            Settings.RemoveDictionaryKey<string, string>(
                Settings.K.RelocatedStartMenuShortcuts,
                record.Key
            );
            forgotten++;
        }

        return forgotten;
    }

    public static void ResetDatabase()
    {
        Settings.ClearDictionary(Settings.K.StartMenuShortcutFolders);
        Settings.ClearDictionary(Settings.K.RelocatedStartMenuShortcuts);
        Settings.ClearDictionary(Settings.K.DeletableStartMenuShortcuts);
        ClearPendingShortcuts();
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

    private static void AddRelocationRecord(
        string packageId,
        string originalPath,
        string relocatedPath
    )
    {
        Settings.SetDictionaryItem(
            Settings.K.RelocatedStartMenuShortcuts,
            BuildRecordKey(packageId, originalPath),
            relocatedPath
        );
    }

    private static void RemoveRelocationRecord(string packageId, string originalPath)
    {
        Settings.RemoveDictionaryKey<string, string>(
            Settings.K.RelocatedStartMenuShortcuts,
            BuildRecordKey(packageId, originalPath)
        );
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

    private static IReadOnlyList<string> GetIdentifiers(IPackage package)
    {
        return NormalizeIdentifiers(
            [
                package.Name,
                package.Id,
                package.Id.Split('.')[^1],
                package.Id.Split('/')[^1],
            ]
        );
    }

    private static IReadOnlyList<string> GetIdentifiers(string packageId)
    {
        int separatorIndex = packageId.IndexOf('\\');
        string id = separatorIndex >= 0 ? packageId[(separatorIndex + 1)..] : packageId;

        return NormalizeIdentifiers([id, id.Split('.')[^1], id.Split('/')[^1]]);
    }

    private static IReadOnlyList<string> NormalizeIdentifiers(IEnumerable<string> values)
    {
        return values
            .Select(Normalize)
            .Where(identifier => identifier.Length >= MinimumMatchLength)
            .Distinct()
            .ToList();
    }

    private static bool IsPlausibleMatch(
        string shortcutPath,
        IReadOnlyList<string> identifiers
    )
    {
        var roots = GetShortcutRoots();
        List<string> candidates = [Path.GetFileNameWithoutExtension(shortcutPath)];

        string? parentDirectory = Path.GetDirectoryName(shortcutPath);
        if (
            !string.IsNullOrEmpty(parentDirectory)
            && !roots.Any(root => AreSamePath(root, parentDirectory))
        )
            candidates.Add(Path.GetFileName(parentDirectory));

        foreach (
            string candidate in candidates
                .Select(Normalize)
                .Where(candidate => candidate.Length >= MinimumMatchLength)
        )
        {
            if (identifiers.Any(identifier => AreRelated(candidate, identifier)))
                return true;
        }

        return false;
    }

    private static bool AreRelated(string candidate, string identifier)
    {
        if (candidate.Equals(identifier, StringComparison.Ordinal))
            return true;

        if (candidate.StartsWith(identifier, StringComparison.Ordinal))
            return true;

        if (identifier.StartsWith(candidate, StringComparison.Ordinal))
            return true;

        if (Math.Min(candidate.Length, identifier.Length) < ContainmentMatchLength)
            return false;

        return candidate.Contains(identifier, StringComparison.Ordinal)
            || identifier.Contains(candidate, StringComparison.Ordinal);
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

            if (!IsUnderUserPrograms(candidate))
                return;

            if (Directory.EnumerateFileSystemEntries(candidate).Any())
                return;

            string? parent = Path.GetDirectoryName(candidate);

            try
            {
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

    private static string Normalize(string value)
    {
        return new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }
}
