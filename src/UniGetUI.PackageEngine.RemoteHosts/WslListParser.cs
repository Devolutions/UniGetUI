using System.Text;
using System.Text.RegularExpressions;

namespace UniGetUI.PackageEngine.RemoteHosts;

public sealed record WslDistroInfo(string Name, string State, int Version, bool IsDefault);

public static partial class WslListParser
{
    [GeneratedRegex(
        @"^\s*(?<default>\*)?\s*(?<name>\S+)\s+(?<state>\S+)\s+(?<version>\d+)\s*$",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex VerboseLineRegex();

    public static string DecodeListOutput(ReadOnlySpan<byte> raw)
    {
        if (raw.IsEmpty)
            return "";

        if (raw.Length >= 2 && raw[0] == 0xFF && raw[1] == 0xFE)
            return Encoding.Unicode.GetString(raw[2..]);

        if (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF)
            return Encoding.UTF8.GetString(raw[3..]);

        int inspect = Math.Min(raw.Length, 64);
        int nuls = 0;
        for (int i = 0; i < inspect; i++)
        {
            if (raw[i] == 0)
                nuls++;
        }

        if (nuls >= inspect / 4)
            return Encoding.Unicode.GetString(raw);

        return Encoding.UTF8.GetString(raw);
    }

    public static IReadOnlyList<WslDistroInfo> ParseVerboseList(string text)
    {
        List<WslDistroInfo> distros = [];
        foreach (string rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length == 0)
                continue;
            if (line.Contains("Windows Subsystem for Linux", StringComparison.OrdinalIgnoreCase))
                continue;

            Match match = VerboseLineRegex().Match(rawLine);
            if (!match.Success)
                continue;

            string name = match.Groups["name"].Value;
            if (name.Equals("NAME", StringComparison.OrdinalIgnoreCase))
                continue;

            int version = int.Parse(match.Groups["version"].Value, System.Globalization.CultureInfo.InvariantCulture);
            distros.Add(new WslDistroInfo(
                name,
                match.Groups["state"].Value,
                version,
                match.Groups["default"].Success
            ));
        }

        return distros;
    }

    public static bool IsHelperDistro(string name)
    {
        if (name.Equals("docker-desktop", StringComparison.OrdinalIgnoreCase)
            || name.Equals("docker-desktop-data", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.EndsWith("-data", StringComparison.OrdinalIgnoreCase);
    }
}
