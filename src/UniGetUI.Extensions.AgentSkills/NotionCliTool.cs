using System.Text.RegularExpressions;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;

namespace UniGetUI.Extensions.AgentSkills;

public static partial class NotionCliTool
{
    public const string Name = "Notion CLI";
    [GeneratedRegex(@"^\d{1,4}(?:\.\d{1,6}){1,3}(?:-[0-9A-Za-z][0-9A-Za-z.-]{0,31})?$")]
    private static partial Regex VersionPattern();
    public static bool IsValidVersion(string version) => VersionPattern().IsMatch(version);
    public static string? PinnedVersion =>
        Settings.GetValue(Settings.K.SkillsNotionCliVersion).Trim() is { Length: > 0 } version && IsValidVersion(version)
            ? version : null;
    public static bool TrySetPinnedVersion(string version)
    {
        version = version.Trim();
        if (version.Length > 0 && !IsValidVersion(version)) return false;
        Settings.SetValue(Settings.K.SkillsNotionCliVersion, version);
        return true;
    }

    private static readonly Lock CacheLock = new();
    private static string? _cachedPath;
    private static DateTime _checkedAt = DateTime.MinValue;
    public static void Forget()
    {
        lock (CacheLock) { _cachedPath = null; _checkedAt = DateTime.MinValue; }
    }
    public static string? FindExecutable()
    {
        lock (CacheLock)
        {
            if (_cachedPath is not null ? File.Exists(_cachedPath) : DateTime.UtcNow - _checkedAt < TimeSpan.FromSeconds(30))
                return _cachedPath;
            _cachedPath = Locate();
            _checkedAt = DateTime.UtcNow;
            return _cachedPath;
        }
    }
    private static string? Locate()
    {
        var (found, path) = CoreTools.Which(OperatingSystem.IsWindows() ? "ntn.exe" : "ntn");
        if (found) return path;
        if (OperatingSystem.IsWindows())
        {
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (string links in new[]
            {
                Path.Combine(local, "Microsoft", "WinGet", "Links"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WinGet", "Links"),
            })
                if (File.Exists(Path.Combine(links, "ntn.exe"))) return Path.Combine(links, "ntn.exe");
            string packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(packages))
                foreach (string folder in Directory.EnumerateDirectories(packages, "Notion.ntn_*"))
                    if (Directory.EnumerateFiles(folder, "ntn.exe", SearchOption.AllDirectories).FirstOrDefault() is { } exe)
                        return exe;
            return null;
        }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[] { Path.Combine(home, ".local", "bin", "ntn"), Path.Combine(home, ".ntn", "bin", "ntn"), "/opt/homebrew/bin/ntn", "/usr/local/bin/ntn" }
            .FirstOrDefault(File.Exists);
    }
}
