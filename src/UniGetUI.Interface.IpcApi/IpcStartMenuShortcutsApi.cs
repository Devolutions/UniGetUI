using UniGetUI.PackageEngine.Classes.Packages.Classes;

namespace UniGetUI.Interface;

public sealed class IpcStartMenuShortcutInfo
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Location { get; set; } = "";
    public string Status { get; set; } = "";
    public bool ExistsOnDisk { get; set; }
    public bool IsTracked { get; set; }
    public bool IsPendingReview { get; set; }
    public string? PendingForPackage { get; set; }
}

public sealed class IpcStartMenuShortcutRequest
{
    public string Path { get; set; } = "";
    public string? Status { get; set; }
}

public sealed class IpcStartMenuShortcutOperationResult
{
    public string Status { get; set; } = "success";
    public string Command { get; set; } = "";
    public string? Message { get; set; }
    public IpcStartMenuShortcutInfo? Shortcut { get; set; }
}

public sealed class IpcStartMenuFolderInfo
{
    public string PackageId { get; set; } = "";
    public string Folder { get; set; } = "";
    public string? ResolvedPath { get; set; }
    public int RelocatedShortcuts { get; set; }
    public int PendingShortcuts { get; set; }
    public IReadOnlyList<string> MatchingShortcuts { get; set; } = [];
}

public sealed class IpcStartMenuFolderRequest
{
    public string PackageId { get; set; } = "";
    public string? Folder { get; set; }
    public bool RelocateExisting { get; set; }
}

public sealed class IpcStartMenuFolderOperationResult
{
    public string Status { get; set; } = "success";
    public string Command { get; set; } = "";
    public string? Message { get; set; }
    public int RelocatedShortcuts { get; set; }
    public IpcStartMenuFolderInfo? Folder { get; set; }
}

public static class IpcStartMenuShortcutsApi
{
    public static IReadOnlyList<IpcStartMenuShortcutInfo> ListShortcuts()
    {
        var verdicts = StartMenuShortcutsDatabase.GetVerdicts();
        var pending = StartMenuShortcutsDatabase.GetPendingShortcuts();

        HashSet<string> allShortcuts =
        [
            .. StartMenuShortcutsDatabase.GetAllShortcuts(),
            .. pending.Select(entry => entry.ShortcutPath),
        ];

        return allShortcuts
            .OrderBy(path => System.IO.Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => ToShortcutInfo(path, verdicts, pending))
            .ToArray();
    }

    public static IpcStartMenuShortcutOperationResult SetShortcut(
        IpcStartMenuShortcutRequest request
    )
    {
        string shortcutPath = NormalizeShortcutPath(request.Path);
        string shortcutStatus = request.Status?.Trim().ToLowerInvariant() ?? "";

        StartMenuShortcutsDatabase.Status status = shortcutStatus switch
        {
            "delete" => StartMenuShortcutsDatabase.Status.Delete,
            "keep" => StartMenuShortcutsDatabase.Status.Maintain,
            _ => throw new InvalidOperationException(
                "The status parameter must be either keep or delete."
            ),
        };

        StartMenuShortcutsDatabase.SetStatus(shortcutPath, status);
        StartMenuShortcutsDatabase.RemovePendingShortcuts(shortcutPath);

        if (status is StartMenuShortcutsDatabase.Status.Delete && File.Exists(shortcutPath))
        {
            StartMenuShortcutsDatabase.DeleteFromDisk(shortcutPath);
        }

        return new IpcStartMenuShortcutOperationResult
        {
            Command = "set-start-menu-shortcut",
            Shortcut = ToShortcutInfo(shortcutPath),
        };
    }

    public static IpcStartMenuShortcutOperationResult ResetShortcut(
        IpcStartMenuShortcutRequest request
    )
    {
        string shortcutPath = NormalizeShortcutPath(request.Path);
        StartMenuShortcutsDatabase.SetStatus(
            shortcutPath,
            StartMenuShortcutsDatabase.Status.Unknown
        );

        return new IpcStartMenuShortcutOperationResult
        {
            Command = "reset-start-menu-shortcut",
            Shortcut = ToShortcutInfo(shortcutPath),
        };
    }

    public static IReadOnlyList<IpcStartMenuFolderInfo> ListFolders()
    {
        var rules = StartMenuShortcutsDatabase.GetRules();
        var pending = StartMenuShortcutsDatabase.GetPendingShortcuts();
        var shortcutsOnDisk = StartMenuShortcutsDatabase.GetShortcutsOnDisk();

        return rules
            .Keys.Union(
                pending.Select(entry => entry.PackageId),
                StringComparer.OrdinalIgnoreCase
            )
            .OrderBy(packageId => packageId, StringComparer.OrdinalIgnoreCase)
            .Select(packageId =>
                ToFolderInfo(
                    packageId,
                    StartMenuShortcutsDatabase.GetRule(packageId) ?? "",
                    pending,
                    shortcutsOnDisk
                )
            )
            .ToArray();
    }

    public static IpcStartMenuFolderOperationResult SetFolder(IpcStartMenuFolderRequest request)
    {
        string packageId = NormalizePackageId(request.PackageId);
        string folder = request.Folder?.Trim().Trim('"').Trim('\'') ?? "";

        if (string.IsNullOrWhiteSpace(folder))
        {
            throw new InvalidOperationException("The folder parameter is required.");
        }

        if (StartMenuShortcutsDatabase.ResolveTargetDirectory(folder) is null)
        {
            throw new InvalidOperationException(
                "The folder parameter must be a subfolder of the user's Start Menu Programs directory."
            );
        }

        StartMenuShortcutsDatabase.SetRule(packageId, folder);
        int rebased = StartMenuShortcutsDatabase.RebaseRelocations(packageId);

        var pendingShortcuts = StartMenuShortcutsDatabase
            .GetPendingShortcuts()
            .Where(entry =>
                string.Equals(entry.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
            )
            .Select(entry => entry.ShortcutPath)
            .ToList();

        List<string> toRelocate = [.. pendingShortcuts];

        if (request.RelocateExisting)
        {
            toRelocate.AddRange(
                StartMenuShortcutsDatabase
                    .FindRelocatableShortcuts(packageId)
                    .Where(shortcut =>
                        !pendingShortcuts.Contains(shortcut, StringComparer.OrdinalIgnoreCase)
                    )
            );
        }

        int relocated = StartMenuShortcutsDatabase.ApplyRule(
            packageId,
            toRelocate,
            out var handledShortcuts
        );

        foreach (string shortcut in pendingShortcuts)
        {
            if (handledShortcuts.Contains(shortcut, StringComparer.OrdinalIgnoreCase))
                StartMenuShortcutsDatabase.RemoveFromPending(packageId, shortcut);
        }

        return new IpcStartMenuFolderOperationResult
        {
            Command = "set-start-menu-folder",
            RelocatedShortcuts = relocated + rebased,
            Folder = ToFolderInfo(packageId, folder),
        };
    }

    public static IpcStartMenuFolderOperationResult RemoveFolder(IpcStartMenuFolderRequest request)
    {
        string packageId = NormalizePackageId(request.PackageId);
        bool removed = StartMenuShortcutsDatabase.RemoveRule(packageId);

        return new IpcStartMenuFolderOperationResult
        {
            Command = "remove-start-menu-folder",
            Message = removed
                ? null
                : $"No Start Menu folder was set for the package {packageId}.",
            Folder = ToFolderInfo(packageId, ""),
        };
    }

    public static IpcCommandResult ResetAll()
    {
        StartMenuShortcutsDatabase.ResetShortcutStatuses();
        return IpcCommandResult.Success("reset-start-menu-shortcuts");
    }

    private static IpcStartMenuShortcutInfo ToShortcutInfo(
        string shortcutPath,
        IReadOnlyDictionary<string, bool>? verdicts = null,
        IReadOnlyList<(string PackageId, string ShortcutPath)>? pending = null
    )
    {
        verdicts ??= StartMenuShortcutsDatabase.GetVerdicts();
        pending ??= StartMenuShortcutsDatabase.GetPendingShortcuts();

        string fileName = System.IO.Path.GetFileName(shortcutPath);
        var pendingEntry = pending.FirstOrDefault(entry =>
            string.Equals(entry.ShortcutPath, shortcutPath, StringComparison.OrdinalIgnoreCase)
        );

        return new IpcStartMenuShortcutInfo
        {
            Path = shortcutPath,
            Name = string.IsNullOrWhiteSpace(fileName)
                ? shortcutPath
                : System.IO.Path.GetFileNameWithoutExtension(fileName),
            Location = System.IO.Path.GetDirectoryName(shortcutPath) ?? "",
            Status = StartMenuShortcutsDatabase.GetStatus(shortcutPath) switch
            {
                StartMenuShortcutsDatabase.Status.Delete => "delete",
                StartMenuShortcutsDatabase.Status.Maintain => "keep",
                _ => "unknown",
            },
            ExistsOnDisk = File.Exists(shortcutPath),
            IsTracked = verdicts.Keys.Contains(shortcutPath, StringComparer.OrdinalIgnoreCase),
            IsPendingReview = pendingEntry.ShortcutPath is not null,
            PendingForPackage = pendingEntry.PackageId,
        };
    }

    private static IpcStartMenuFolderInfo ToFolderInfo(
        string packageId,
        string folder,
        IReadOnlyList<(string PackageId, string ShortcutPath)>? pending = null,
        IReadOnlyList<string>? shortcutsOnDisk = null
    )
    {
        pending ??= StartMenuShortcutsDatabase.GetPendingShortcuts();

        return new IpcStartMenuFolderInfo
        {
            PackageId = packageId,
            Folder = folder,
            ResolvedPath = StartMenuShortcutsDatabase.ResolveTargetDirectory(folder),
            RelocatedShortcuts = StartMenuShortcutsDatabase
                .GetRelocationsForPackage(packageId)
                .Count,
            PendingShortcuts = pending.Count(entry =>
                string.Equals(entry.PackageId, packageId, StringComparison.OrdinalIgnoreCase)
            ),
            MatchingShortcuts = StartMenuShortcutsDatabase.FindRelocatableShortcuts(
                packageId,
                shortcutsOnDisk
            ),
        };
    }

    private static string NormalizeShortcutPath(string shortcutPath)
    {
        string normalizedPath = shortcutPath.Trim().Trim('"').Trim('\'');
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            throw new InvalidOperationException("The path parameter is required.");
        }

        if (!StartMenuShortcutsDatabase.IsManagedShortcutPath(normalizedPath))
        {
            throw new InvalidOperationException(
                "The path parameter must point inside a Start Menu Programs directory."
            );
        }

        if (!StartMenuShortcutsDatabase.IsShortcutFile(normalizedPath))
        {
            throw new InvalidOperationException(
                "The path parameter must point to a .lnk or .url shortcut."
            );
        }

        return System.IO.Path.GetFullPath(normalizedPath);
    }

    private static string NormalizePackageId(string packageId)
    {
        string normalizedId = packageId.Trim().Trim('"').Trim('\'');
        if (string.IsNullOrWhiteSpace(normalizedId))
        {
            throw new InvalidOperationException("The package parameter is required.");
        }

        int separatorIndex = normalizedId.IndexOf('\\');
        if (separatorIndex > 0)
        {
            normalizedId =
                normalizedId[..separatorIndex].ToLowerInvariant()
                + normalizedId[separatorIndex..];
        }

        return normalizedId;
    }
}
