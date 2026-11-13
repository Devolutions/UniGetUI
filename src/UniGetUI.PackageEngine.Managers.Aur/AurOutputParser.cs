using UniGetUI.PackageEngine.Interfaces;
using UniGetUI.PackageEngine.Managers.PacmanManager;

namespace UniGetUI.PackageEngine.Managers.AurManager;

public enum AurHelperKind
{
    Unknown,
    Paru,
    Yay,
}

internal static class AurOutputParser
{
    private const string LatestCommitVersion = "latest-commit";

    internal readonly record struct ListedPackage(string Id, string Version);

    internal readonly record struct ListedUpdate(string Id, string InstalledVersion, string NewVersion);

    public static AurHelperKind DetectHelper(string versionOutput, string executablePath)
    {
        var name = versionOutput.TrimStart().Split(' ', 2)[0];
        if (name.Equals("paru", StringComparison.OrdinalIgnoreCase))
            return AurHelperKind.Paru;
        if (name.Equals("yay", StringComparison.OrdinalIgnoreCase))
            return AurHelperKind.Yay;

        var fileName = Path.GetFileName(executablePath);
        if (fileName.StartsWith("paru", StringComparison.OrdinalIgnoreCase))
            return AurHelperKind.Paru;
        if (fileName.StartsWith("yay", StringComparison.OrdinalIgnoreCase))
            return AurHelperKind.Yay;
        return AurHelperKind.Unknown;
    }

    public static string ParseHelperVersion(string output)
    {
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return line;
        return $"{parts[0]} {parts[1].TrimStart('v')}";
    }

    public static List<ListedPackage> ParseSearch(string output)
    {
        List<ListedPackage> packages = [];
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
                continue;

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;

            var slashIdx = parts[0].IndexOf('/');
            if (slashIdx <= 0 || slashIdx == parts[0].Length - 1)
                continue;

            packages.Add(new ListedPackage(parts[0][(slashIdx + 1)..], parts[1]));
        }
        return packages;
    }

    public static List<ListedPackage> ParseInstalled(string output)
    {
        List<ListedPackage> packages = [];
        foreach (var rawLine in output.Split('\n'))
        {
            var parts = rawLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
                continue;
            packages.Add(new ListedPackage(parts[0], parts[1]));
        }
        return packages;
    }

    public static List<ListedUpdate> ParseUpdates(string output)
    {
        List<ListedUpdate> packages = [];
        foreach (var rawLine in output.Split('\n'))
        {
            var parts = rawLine.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4 || parts[2] != "->")
                continue;
            if (parts.Length > 4 && parts[4] == "[ignored]")
                continue;
            if (parts[3] == LatestCommitVersion)
                continue;
            packages.Add(new ListedUpdate(parts[0], parts[1], parts[3]));
        }
        return packages;
    }

    public static void ParseDetails(string output, IPackageDetails details)
    {
        string lastKey = "";
        Uri? aurUrl = null;
        foreach (var rawLine in output.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.Length == 0)
            {
                if (lastKey.Length > 0)
                    break;
                continue;
            }

            var colonIdx = line.IndexOf(" : ", StringComparison.Ordinal);
            if (colonIdx < 0)
            {
                if (char.IsWhiteSpace(line[0]))
                    AddDependencies(lastKey, line.Trim(), details);
                continue;
            }

            var key = line[..colonIdx].Trim();
            var value = line[(colonIdx + 3)..].Trim();

            if (key.Length == 0)
            {
                AddDependencies(lastKey, value, details);
                continue;
            }

            lastKey = key;
            if (value.Length == 0 || value == "None")
                continue;

            switch (key)
            {
                case "URL":
                    if (Uri.TryCreate(value, UriKind.Absolute, out var url))
                        details.HomepageUrl = url;
                    break;
                case "AUR URL":
                    if (Uri.TryCreate(value, UriKind.Absolute, out var aur))
                        aurUrl = aur;
                    break;
                case "Description":
                    details.Description = value;
                    break;
                case "Licenses":
                    details.License = value;
                    break;
                case "Maintainer":
                    details.Publisher = value;
                    break;
                case "Submitter":
                    details.Author = value;
                    break;
                case "Version":
                    details.Version = value;
                    break;
                case "Last Modified":
                    details.UpdateDate = value;
                    break;
                case "Keywords":
                    details.Tags = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    break;
                case "Snapshot URL":
                    if (Uri.TryCreate(value, UriKind.Absolute, out var snapshot))
                        details.InstallerUrl = snapshot;
                    break;
                case "Depends On":
                case "Optional Deps":
                    AddDependencies(key, value, details);
                    break;
            }
        }

        details.ManifestUrl = aurUrl ?? new Uri($"https://aur.archlinux.org/packages/{Uri.EscapeDataString(details.Package.Id)}");
    }

    private static void AddDependencies(string key, string value, IPackageDetails details)
    {
        if (value.Length == 0 || value == "None")
            return;

        bool mandatory = key is "Depends On";
        if (!mandatory && key != "Optional Deps")
            return;

        AlpmOutput.AddDependencies(details, value, mandatory);
    }
}
