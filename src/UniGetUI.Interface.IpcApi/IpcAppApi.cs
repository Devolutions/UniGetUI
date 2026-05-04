namespace UniGetUI.Interface;

public sealed class IpcAppInfo
{
    public bool Headless { get; set; }
    public bool WindowAvailable { get; set; }
    public bool WindowVisible { get; set; }
    public bool CanShowWindow { get; set; }
    public bool CanNavigate { get; set; }
    public bool CanQuit { get; set; }
    public string CurrentPage { get; set; } = "";
    public IReadOnlyList<string> SupportedPages { get; set; } = IpcAppPages.SupportedPages;
}

public sealed class IpcAppNavigateRequest
{
    public string Page { get; set; } = "";
    public string? ManagerName { get; set; }
    public string? HelpAttachment { get; set; }
}

public static class IpcAppPages
{
    public static readonly IReadOnlyList<string> SupportedPages =
    [
        "discover",
        "updates",
        "installed",
        "bundles",
        "settings",
        "managers",
        "own-log",
        "manager-log",
        "operation-history",
        "help",
        "release-notes",
        "about",
    ];

    public static string NormalizePageName(string page)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(page);

        string normalized = page.Trim().ToLowerInvariant();
        return normalized switch
        {
            "discover" => normalized,
            "updates" => normalized,
            "installed" => normalized,
            "bundles" => normalized,
            "settings" => normalized,
            "managers" => normalized,
            "own-log" => normalized,
            "manager-log" => normalized,
            "operation-history" => normalized,
            "help" => normalized,
            "release-notes" => normalized,
            "about" => normalized,
            _ => throw new InvalidOperationException(
                $"Unsupported page \"{page}\". Supported pages: {string.Join(", ", SupportedPages)}."
            ),
        };
    }

    public static string ToPageName(string? pageTypeName)
    {
        if (string.IsNullOrWhiteSpace(pageTypeName))
        {
            return "";
        }

        return pageTypeName switch
        {
            "Discover" => "discover",
            "Updates" => "updates",
            "Installed" => "installed",
            "Bundles" => "bundles",
            "Settings" => "settings",
            "Managers" => "managers",
            "OwnLog" => "own-log",
            "ManagerLog" => "manager-log",
            "OperationHistory" => "operation-history",
            "Help" => "help",
            "ReleaseNotes" => "release-notes",
            "About" => "about",
            _ => pageTypeName.Trim(),
        };
    }
}
