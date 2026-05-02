using System.Text.RegularExpressions;
using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.PackageEngine.Managers.FlatpakManager;

public partial class Flatpak
{
    [GeneratedRegex(@"[a-zA-Z0-9][-a-zA-Z0-9]*(\.[a-zA-Z0-9][-a-zA-Z0-9]*)+")]
    private static partial Regex _searchAppIdRegex();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex _multiSpaceRegex();

    private static void ParseColumns(IReadOnlyList<string> parts, out string appId, out string version, out string name)
    {
        appId = parts[0];
        version = parts[1];
        if (string.IsNullOrEmpty(version) && parts.Count > 2)
            version = parts[2];
        name = parts[4];
    }

    public static IReadOnlyList<Package> ParseInstalledPackages(
        IEnumerable<string> outputLines,
        IManagerSource source,
        IPackageManager manager)
    {
        var packages = new List<Package>();

        foreach (var line in outputLines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            var parts = trimmed.Split('\t');
            if (parts.Length < 5)
            {
                continue;
            }

            var appId = parts[0];
            var version = parts[1];
            if (string.IsNullOrEmpty(version))
            {
                version = parts[2];
            }

            var name = parts[4];

            packages.Add(new Package(
                CoreTools.FormatAsName(name),
                appId,
                version,
                source,
                manager));
        }

        return packages;
    }

    public static IReadOnlyList<Package> ParseAvailableUpdates(
        IEnumerable<string> outputLines,
        IManagerSource source,
        IPackageManager manager,
        IReadOnlyList<IPackage> installedPackages)
    {
        var installedPackageMap = new Dictionary<string, IPackage>();
        foreach (var installedPackage in installedPackages)
        {
            installedPackageMap.TryAdd(installedPackage.Id, installedPackage);
        }

        var packages = new List<Package>();

        foreach (var line in outputLines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            var parts = trimmed.Split('\t');
            if (parts.Length < 5)
            {
                continue;
            }

            ParseColumns(parts, out var appId, out var newVersion, out var name);

            if (installedPackageMap.TryGetValue(appId, out var installedPackage))
            {
                packages.Add(new Package(
                    CoreTools.FormatAsName(name),
                    appId,
                    installedPackage.VersionString,
                    newVersion,
                    source,
                    manager));
            }
        }

        return packages;
    }

    public static IReadOnlyList<Package> ParseSearchResults(
        IEnumerable<string> outputLines,
        IManagerSource source,
        IPackageManager manager)
    {
        var lines = outputLines.ToArray();
        if (lines.Length == 0)
        {
            return [];
        }

        var packages = new List<Package>();

        // Detect format: tab-separated (no header) vs space-padded (with header)
        int startIndex = lines[0].Contains('\t') ? 0 : 1;

        for (int i = startIndex; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                continue;
            }

            string name;
            string appId;
            string version;

            var tabParts = trimmed.Split('\t');
            if (tabParts.Length >= 4)
            {
                name = tabParts[0];
                appId = tabParts[2];
                version = tabParts[3];
            }
            else
            {
                var appIdMatch = _searchAppIdRegex().Match(trimmed);
                if (!appIdMatch.Success)
                {
                    continue;
                }

                appId = appIdMatch.Value;
                var before = trimmed[..appIdMatch.Index].TrimEnd();
                var after = trimmed[(appIdMatch.Index + appIdMatch.Length)..].Trim();

                name = _multiSpaceRegex().Split(before)[0];
                var parts = after.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                version = parts.Length > 0 ? parts[0] : "";
            }

            packages.Add(new Package(
                CoreTools.FormatAsName(name),
                appId,
                version,
                source,
                manager));
        }

        return packages;
    }
}
