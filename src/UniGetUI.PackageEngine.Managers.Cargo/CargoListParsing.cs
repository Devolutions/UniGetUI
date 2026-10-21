using System.Text.RegularExpressions;

namespace UniGetUI.PackageEngine.Managers.CargoManager;

internal sealed record CargoListEntry(
    string Id,
    string InstalledVersion,
    string? LatestVersion,
    bool NeedsUpdate,
    string? SourceDescription = null
)
{
    internal bool IsCratesIo => SourceDescription is null
        or "registry+https://github.com/rust-lang/crates.io-index"
        or "registry+https://github.com/rust-lang/crates.io-index/"
        or "registry+https://index.crates.io/"
        or "registry+https://index.crates.io"
        or "sparse+https://index.crates.io/"
        or "registry+sparse+https://index.crates.io/";
}

public partial class Cargo
{
    [GeneratedRegex(@"^(?<id>[A-Za-z0-9_][A-Za-z0-9_-]*)\s+v(?<version>[0-9][A-Za-z0-9.+-]*)(?:\s+\((?<source>.*)\))?:$")]
    private static partial Regex InstallListLineRegex();

    internal static List<CargoListEntry> ParseInstallList(IEnumerable<string> lines)
    {
        List<CargoListEntry> entries = [];
        foreach (string rawLine in lines)
        {
            var match = InstallListLineRegex().Match(rawLine.TrimEnd());
            if (match.Success)
                entries.Add(new CargoListEntry(
                    match.Groups["id"].Value,
                    match.Groups["version"].Value,
                    null,
                    false,
                    match.Groups["source"].Success ? match.Groups["source"].Value : null));
            else if (rawLine.Length > 0 && !char.IsWhiteSpace(rawLine[0]))
                throw new InvalidDataException($"Unrecognized cargo install --list output: {rawLine}");
        }
        return entries;
    }
}
