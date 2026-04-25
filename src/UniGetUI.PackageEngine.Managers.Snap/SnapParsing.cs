using UniGetUI.Core.Tools;
using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.PackageClasses;

namespace UniGetUI.PackageEngine.Managers.SnapManager;

public partial class Snap
{
    public static IReadOnlyList<Package> ParseInstalledPackages(
        IEnumerable<string> outputLines,
        IManagerSource source,
        IPackageManager manager)
    {
        var packages = new List<Package>();
        bool headerSkipped = false;

        foreach (var line in outputLines)
        {
            if (!headerSkipped)
            {
                headerSkipped = true;
                continue;
            }

            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var match = InstalledLineRegex().Match(trimmed);
            if (!match.Success) continue;

            var id = match.Groups[1].Value;
            var version = match.Groups[2].Value;
            packages.Add(new Package(
                CoreTools.FormatAsName(id),
                id,
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
        IReadOnlyList<IPackage> installedPackages
    )
    {
        var installedPackageMap = new Dictionary<string, IPackage>();
        foreach (var installedPackage in installedPackages)
        {
            installedPackageMap.TryAdd(installedPackage.Id, installedPackage);
        }

        var packages = new List<Package>();
        bool headerSkipped = false;

        foreach (var line in outputLines)
        {
            if (!headerSkipped)
            {
                headerSkipped = true;
                continue;
            }

            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var match = UpdateLineRegex().Match(trimmed);
            if (!match.Success) continue;

            var id = match.Groups[1].Value;
            var newVersion = match.Groups[2].Value;

            if (installedPackageMap.TryGetValue(id, out var installedPackage))
            {
                packages.Add(new Package(
                    CoreTools.FormatAsName(id),
                    id,
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
        var packages = new List<Package>();
        bool headerSkipped = false;

        foreach (var line in outputLines)
        {
            if (!headerSkipped)
            {
                headerSkipped = true;
                continue;
            }

            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed)) continue;

            var match = FindLineRegex().Match(trimmed);
            if (!match.Success) continue;

            var id = match.Groups[1].Value;
            var version = match.Groups[2].Value;
            packages.Add(new Package(
                CoreTools.FormatAsName(id),
                id,
                version,
                source,
                manager));
        }

        return packages;
    }
}
