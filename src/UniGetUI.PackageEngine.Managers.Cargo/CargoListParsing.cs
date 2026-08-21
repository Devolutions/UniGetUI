using System.Text.RegularExpressions;

namespace UniGetUI.PackageEngine.Managers.CargoManager;

internal sealed record CargoListEntry(
    string Id,
    string InstalledVersion,
    string? LatestVersion,
    bool NeedsUpdate
);

public partial class Cargo
{
    [GeneratedRegex(@"^[A-Za-z0-9_][A-Za-z0-9_-]*$")]
    private static partial Regex CrateNameRegex();

    [GeneratedRegex(@"[ \t]{2,}|\t")]
    private static partial Regex ColumnSeparatorRegex();

    [GeneratedRegex(@"^v(?<version>[0-9][A-Za-z0-9.+-]*)(?:\s+\(v[^)]*\))?$")]
    private static partial Regex VersionCellRegex();

    [GeneratedRegex(@"^(?<id>[A-Za-z0-9_][A-Za-z0-9_-]*)\s+v(?<version>[0-9][A-Za-z0-9.+-]*)(?:\s+\(.*\))?:$")]
    private static partial Regex InstallListLineRegex();

    internal static List<CargoListEntry> ParseInstallUpdateList(
        IEnumerable<string> lines,
        List<string>? skippedRows = null
    )
    {
        List<CargoListEntry> entries = [];
        bool insideTable = false;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();

            if (line.Length is 0)
            {
                insideTable = false;
                continue;
            }

            if (IsTableHeader(line))
            {
                insideTable = true;
                continue;
            }

            if (!insideTable)
                continue;

            var entry = ParseInstallUpdateRow(line);
            if (entry is null)
                skippedRows?.Add(line);
            else
                entries.Add(entry);
        }

        return entries;
    }

    internal static List<CargoListEntry> ParseInstallList(IEnumerable<string> lines)
    {
        List<CargoListEntry> entries = [];

        foreach (var rawLine in lines)
        {
            var match = InstallListLineRegex().Match(rawLine.TrimEnd());
            if (match.Success)
                entries.Add(
                    new CargoListEntry(
                        match.Groups["id"].Value,
                        match.Groups["version"].Value,
                        null,
                        false
                    )
                );
        }

        return entries;
    }

    private static bool IsTableHeader(string line) =>
        line.StartsWith("Package", StringComparison.Ordinal)
        && line.Contains("Installed", StringComparison.Ordinal)
        && line.Contains("Latest", StringComparison.Ordinal)
        && line.Contains("Needs update", StringComparison.Ordinal);

    private static CargoListEntry? ParseInstallUpdateRow(string line)
    {
        var cells = ColumnSeparatorRegex().Split(line);
        if (cells.Length < 4)
            return null;

        var id = cells[0].Trim();
        if (!CrateNameRegex().IsMatch(id))
            return null;

        var installedVersion = ParseVersionCell(cells[1]);
        if (installedVersion is null)
            return null;

        return new CargoListEntry(
            id,
            installedVersion,
            ParseVersionCell(cells[2]),
            cells[3].Trim().Equals("Yes", StringComparison.OrdinalIgnoreCase)
        );
    }

    private static string? ParseVersionCell(string cell)
    {
        var match = VersionCellRegex().Match(cell.Trim());
        return match.Success ? match.Groups["version"].Value : null;
    }
}
