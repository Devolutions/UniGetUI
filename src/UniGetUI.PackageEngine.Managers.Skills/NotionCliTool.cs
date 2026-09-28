using System.Text.RegularExpressions;
using UniGetUI.Core.Data;
using UniGetUI.Core.SettingsEngine;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Classes.Manager.Classes;
using UniGetUI.PackageEngine.Classes.Packages.Classes;

namespace UniGetUI.PackageEngine.Managers.SkillsManager;

/// <summary>
/// The Notion CLI (ntn), which Notion skill sources go through: it signs in and keeps the token in
/// the OS credential store, so UniGetUI never handles Notion credentials. This finds it, and
/// installs it through the missing-dependency dialog: with WinGet on Windows, npm elsewhere. A
/// version the user pins is the one installed, and UniGetUI then stops offering its updates.
/// </summary>
public static partial class NotionCliTool
{
    public const string Name = "Notion CLI";

    internal const string WinGetId = "Notion.ntn";

    private const string NpmPackage = "ntn";

    /// <summary>Its entry in the ignored updates, as the WinGet package.</summary>
    internal const string IgnoredUpdatesId = "winget\\" + WinGetId;

    // Only digits, dots and a short pre-release tag, since the version goes into an install command
    [GeneratedRegex(@"^\d{1,4}(?:\.\d{1,6}){1,3}(?:-[0-9A-Za-z][0-9A-Za-z.-]{0,31})?$")]
    private static partial Regex VersionPattern();

    public static bool IsValidVersion(string version) => VersionPattern().IsMatch(version);

    /// <summary>The version the user pinned, which is the one installed; null for the latest.</summary>
    public static string? PinnedVersion =>
        Settings.GetValue(Settings.K.SkillsNotionCliVersion).Trim() is { Length: > 0 } version && IsValidVersion(version)
            ? version
            : null;

    /// <summary>
    /// Pins the version to install, or clears the pin for an empty value. While a version is pinned,
    /// UniGetUI does not offer updates of the WinGet package. Returns false for an invalid version.
    /// </summary>
    public static bool TrySetPinnedVersion(string version)
    {
        version = version.Trim();
        if (version.Length > 0 && !IsValidVersion(version))
            return false;

        Settings.SetValue(Settings.K.SkillsNotionCliVersion, version);
        if (version.Length > 0)
            IgnoredUpdatesDatabase.Add(IgnoredUpdatesId);
        else
            IgnoredUpdatesDatabase.Remove(IgnoredUpdatesId);
        return true;
    }

    // Every library call asks, so an answer is kept: a found ntn while it is still there, and its
    // absence for a little while, or until Forget is called after an install
    private static readonly TimeSpan RecheckMissingAfter = TimeSpan.FromSeconds(30);
    private static readonly Lock CacheLock = new();
    private static string? _cachedPath;
    private static DateTime _checkedAt = DateTime.MinValue;

    /// <summary>
    /// Where ntn is: on the PATH, or where WinGet or the install script put it, which a PATH read
    /// before the install does not reach yet. Null when it is not installed.
    /// </summary>
    public static string? FindExecutable()
    {
        lock (CacheLock)
        {
            bool fresh = _cachedPath is not null
                ? File.Exists(_cachedPath)
                : DateTime.UtcNow - _checkedAt < RecheckMissingAfter;
            if (!fresh)
            {
                _cachedPath = Locate();
                _checkedAt = DateTime.UtcNow;
            }

            return _cachedPath;
        }
    }

    /// <summary>Looks for ntn again on the next call, as after installing it.</summary>
    public static void Forget()
    {
        lock (CacheLock)
        {
            _cachedPath = null;
            _checkedAt = DateTime.MinValue;
        }
    }

    private static string? Locate()
    {
        if (OperatingSystem.IsWindows())
        {
            var (found, path) = CoreTools.Which("ntn.exe");
            if (found)
                return path;

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            foreach (string links in new[] { Path.Combine(localAppData, "Microsoft", "WinGet", "Links"), Path.Combine(programFiles, "WinGet", "Links") })
            {
                string link = Path.Combine(links, "ntn.exe");
                if (File.Exists(link))
                    return link;
            }

            string packages = Path.Combine(localAppData, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(packages))
            {
                foreach (string folder in Directory.EnumerateDirectories(packages, $"{WinGetId}_*"))
                {
                    if (Directory.EnumerateFiles(folder, "ntn.exe", SearchOption.AllDirectories).FirstOrDefault() is { } exe)
                        return exe;
                }
            }

            return null;
        }

        var (onPath, unixPath) = CoreTools.Which("ntn");
        if (onPath)
            return unixPath;

        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[] { Path.Combine(home, ".local", "bin", "ntn"), Path.Combine(home, ".ntn", "bin", "ntn"), "/opt/homebrew/bin/ntn", "/usr/local/bin/ntn" }
            .FirstOrDefault(File.Exists);
    }

    /// <summary>The install command for the missing-dependency dialog, at the pinned version if any.</summary>
    public static ManagerDependency CreateDependency()
    {
        string? version = PinnedVersion;
        Func<Task<bool>> isInstalled = () =>
            Task.Run(() =>
            {
                Forget();
                return FindExecutable() is not null;
            });

        if (OperatingSystem.IsWindows())
        {
            string pin = version is null ? "" : $" --version {version}";
            return new ManagerDependency(
                Name,
                CoreData.PowerShell5,
                "-ExecutionPolicy Bypass -NoLogo -NoProfile -Command \"& {winget install --id "
                    + WinGetId
                    + " --exact --source winget --accept-source-agreements --accept-package-agreements --force"
                    + pin
                    + "}\"",
                $"winget install --id {WinGetId} --exact --source winget{pin}",
                isInstalled
            );
        }

        // Notion's install script is the other supported way, but UniGetUI does not pipe a download
        // into a shell for the user; it is offered as the command to run by hand
        string package = version is null ? NpmPackage : $"{NpmPackage}@{version}";
        bool hasNpm = CoreTools.Which("npm").Item1;
        return new ManagerDependency(
            Name,
            hasNpm ? "npm" : "",
            hasNpm ? $"install --global {package}" : "",
            hasNpm ? $"npm install --global {package}" : "curl -fsSL https://ntn.dev | bash",
            isInstalled
        );
    }
}
