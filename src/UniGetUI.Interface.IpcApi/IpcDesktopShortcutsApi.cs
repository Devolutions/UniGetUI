using UniGetUI.PackageEngine.Classes.Packages.Classes;

namespace UniGetUI.Interface;

public sealed class IpcDesktopShortcutInfo
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
    public string Status { get; set; } = "";
    public bool ExistsOnDisk { get; set; }
    public bool IsTracked { get; set; }
    public bool IsPendingReview { get; set; }
}

public sealed class IpcDesktopShortcutRequest
{
    public string Path { get; set; } = "";
    public string? Status { get; set; }
}

public sealed class IpcDesktopShortcutOperationResult
{
    public string Status { get; set; } = "success";
    public string Command { get; set; } = "";
    public string? Message { get; set; }
    public IpcDesktopShortcutInfo? Shortcut { get; set; }
}

public static class IpcDesktopShortcutsApi
{
    public static IReadOnlyList<IpcDesktopShortcutInfo> ListShortcuts()
    {
        var trackedShortcuts = DesktopShortcutsDatabase.GetDatabase();
        HashSet<string> allShortcuts =
        [
            .. DesktopShortcutsDatabase.GetAllShortcuts(),
            .. DesktopShortcutsDatabase.GetUnknownShortcuts(),
        ];

        return allShortcuts
            .OrderBy(path => System.IO.Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => ToShortcutInfo(path, trackedShortcuts))
            .ToArray();
    }

    public static IpcDesktopShortcutOperationResult SetShortcut(
        IpcDesktopShortcutRequest request
    )
    {
        string shortcutPath = NormalizeShortcutPath(request.Path);
        string shortcutStatus = request.Status?.Trim().ToLowerInvariant() ?? "";

        DesktopShortcutsDatabase.Status status = shortcutStatus switch
        {
            "delete" => DesktopShortcutsDatabase.Status.Delete,
            "keep" => DesktopShortcutsDatabase.Status.Maintain,
            _ => throw new InvalidOperationException(
                "The status parameter must be either keep or delete."
            ),
        };

        DesktopShortcutsDatabase.AddToDatabase(shortcutPath, status);
        DesktopShortcutsDatabase.RemoveFromUnknownShortcuts(shortcutPath);

        if (status is DesktopShortcutsDatabase.Status.Delete && File.Exists(shortcutPath))
        {
            DesktopShortcutsDatabase.DeleteFromDisk(shortcutPath);
        }

        return new IpcDesktopShortcutOperationResult
        {
            Command = "set-desktop-shortcut",
            Shortcut = ToShortcutInfo(shortcutPath),
        };
    }

    public static IpcDesktopShortcutOperationResult ResetShortcut(
        IpcDesktopShortcutRequest request
    )
    {
        string shortcutPath = NormalizeShortcutPath(request.Path);
        DesktopShortcutsDatabase.AddToDatabase(shortcutPath, DesktopShortcutsDatabase.Status.Unknown);

        return new IpcDesktopShortcutOperationResult
        {
            Command = "reset-desktop-shortcut",
            Shortcut = ToShortcutInfo(shortcutPath),
        };
    }

    public static IpcCommandResult ResetAllShortcuts()
    {
        DesktopShortcutsDatabase.ResetDatabase();
        return IpcCommandResult.Success("reset-desktop-shortcuts");
    }

    private static IpcDesktopShortcutInfo ToShortcutInfo(
        string shortcutPath,
        IReadOnlyDictionary<string, bool>? trackedShortcuts = null
    )
    {
        trackedShortcuts ??= DesktopShortcutsDatabase.GetDatabase();
        string fileName = System.IO.Path.GetFileName(shortcutPath);

        return new IpcDesktopShortcutInfo
        {
            Path = shortcutPath,
            Name = string.IsNullOrWhiteSpace(fileName)
                ? shortcutPath
                : System.IO.Path.GetFileNameWithoutExtension(fileName),
            Status = DesktopShortcutsDatabase.GetStatus(shortcutPath) switch
            {
                DesktopShortcutsDatabase.Status.Delete => "delete",
                DesktopShortcutsDatabase.Status.Maintain => "keep",
                _ => "unknown",
            },
            ExistsOnDisk = File.Exists(shortcutPath),
            IsTracked = trackedShortcuts.ContainsKey(shortcutPath),
            IsPendingReview = DesktopShortcutsDatabase.GetUnknownShortcuts().Contains(shortcutPath),
        };
    }

    private static string NormalizeShortcutPath(string shortcutPath)
    {
        string normalizedPath = shortcutPath.Trim().Trim('"').Trim('\'');
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            throw new InvalidOperationException("The path parameter is required.");
        }

        return normalizedPath;
    }
}
