using System.Globalization;
using System.Text.RegularExpressions;

namespace UniGetUI.Extensions.Xcode;

/// <summary>Reads progress lines and the Xcode list text layout of the xcodes command, which tests use to describe lists.</summary>
public static partial class XcodesOutputParser
{
    /// <summary>Takes the escape sequences out of terminal output, and the control characters it echoes.</summary>
    public static string StripTerminalControl(string text)
    {
        string plain = AnsiSequence().Replace(text, "");
        return ControlCharacters().Replace(plain, "");
    }

    /// <summary>Splits output into trimmed, non-empty lines.</summary>
    public static string[] SplitLines(string output)
        => StripTerminalControl(output)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The version <c>xcodes version</c> prints, such as <c>2.1.0</c>.</summary>
    public static string? ParseVersion(string output) => SplitLines(output).FirstOrDefault();

    /// <summary>Reads <c>xcodes list</c> or <c>xcodes update</c>: one release per line, oldest first.</summary>
    public static IReadOnlyList<XcodeRelease> ParseReleases(string output) => ParseReleases(output, out _);

    internal static IReadOnlyList<XcodeRelease> ParseReleases(string output, out bool complete)
    {
        List<XcodeRelease> releases = [];
        complete = true;
        foreach (string line in SplitLines(output))
        {
            if (line.StartsWith("Showing Xcodes for this Mac by default:", StringComparison.Ordinal))
                continue;
            if (!TryParseDescription(line, out XcodeDescription description))
            {
                complete = false;
                continue;
            }
            releases.Add(new XcodeRelease(description.Version, description.Build, description.Architecture,
                description.IsInstalled, description.IsSelected));
        }
        complete &= releases.Count > 0;
        return releases;
    }

    /// <summary>Reads <c>xcodes installed</c>: a description, a tab, then where it is.</summary>
    public static IReadOnlyList<InstalledXcode> ParseInstalled(string output) => ParseInstalled(output, out _);

    internal static IReadOnlyList<InstalledXcode> ParseInstalled(string output, out bool complete)
    {
        List<InstalledXcode> installed = [];
        complete = true;
        foreach (string line in SplitLines(output))
        {
            int separator = line.IndexOf('\t');
            if (separator < 0)
                separator = line.IndexOf(" /", StringComparison.Ordinal);
            if (separator < 0)
            {
                complete = false;
                continue;
            }

            string path = line[(separator + 1)..].Trim();
            if (!path.StartsWith('/') || !TryParseDescription(line[..separator], out XcodeDescription description))
            {
                complete = false;
                continue;
            }
            installed.Add(new InstalledXcode(description.Version, description.Build, description.Architecture,
                description.IsSelected, path));
        }
        return installed;
    }

    /// <summary>Reads a numbered step of an installation, and the percentage a download reports.</summary>
    public static bool TryParseProgress(string line, out XcodeProgress progress)
    {
        progress = null!;
        Match step = StepLine().Match(StripTerminalControl(line).Trim());
        if (!step.Success
            || !int.TryParse(step.Groups["step"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int number)
            || !int.TryParse(step.Groups["count"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int count))
            return false;

        string text = step.Groups["text"].Value.Trim();
        double? fraction = null;
        if (Percentage().Match(text) is { Success: true } percentage
            && int.TryParse(percentage.Groups["percent"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int percent))
            fraction = Math.Clamp(percent, 0, 100) / 100d;

        progress = new XcodeProgress(number, count, text, fraction);
        return true;
    }

    /// <summary>Reads the percentage in an xcodebuild simulator download progress line.</summary>
    public static bool TryParseRuntimeProgress(string line, out double percent)
    {
        percent = 0;
        Match match = RuntimePercentage().Match(StripTerminalControl(line));
        return match.Success
               && double.TryParse(match.Groups["percent"].Value.Replace(',', '.'),
                   NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out percent)
               && percent is >= 0 and <= 100;
    }

    internal readonly record struct XcodeDescription(
        string Version, string? Build, XcodeArchitecture Architecture, bool IsInstalled, bool IsSelected);

    /// <summary>Reads <c>26.4 (17E192) [Apple Silicon] (Installed, Selected)</c>, in which all but the version are optional.</summary>
    internal static bool TryParseDescription(string text, out XcodeDescription description)
    {
        description = default;
        string line = text.Trim();
        int cut = line.IndexOfAny(['(', '[']);
        string version = string.Join(' ', (cut < 0 ? line : line[..cut]).Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!XcodeVersion.TryParse(version, out _))
            return false;

        string? build = null;
        XcodeArchitecture architecture = XcodeArchitecture.Unspecified;
        bool installed = false, selected = false;
        if (cut >= 0)
        {
            string suffix = line[cut..];
            int consumed = 0;
            foreach (Match group in Groups().Matches(suffix))
            {
                if (!string.IsNullOrWhiteSpace(suffix[consumed..group.Index]))
                    return false;
                consumed = group.Index + group.Length;
                if (group.Groups["architecture"].Success)
                {
                    architecture = ParseArchitecture(group.Groups["architecture"].Value);
                    continue;
                }

                string[] words = group.Groups["parenthesized"].Value
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (words.Length > 0 && words.All(IsFlag))
                {
                    installed |= words.Contains("Installed", StringComparer.OrdinalIgnoreCase);
                    selected |= words.Contains("Selected", StringComparer.OrdinalIgnoreCase);
                }
                else if (build is null && words.Length == 1)
                {
                    build = group.Groups["parenthesized"].Value.Trim();
                }
                else
                {
                    return false;
                }
            }
            if (!string.IsNullOrWhiteSpace(suffix[consumed..]))
                return false;
        }

        description = new XcodeDescription(version, build, architecture, installed, selected);
        return true;
    }

    private static bool IsFlag(string word)
        => word.Equals("Installed", StringComparison.OrdinalIgnoreCase)
            || word.Equals("Selected", StringComparison.OrdinalIgnoreCase);

    private static XcodeArchitecture ParseArchitecture(string name) => name.Trim().ToLowerInvariant() switch
    {
        "apple silicon" => XcodeArchitecture.AppleSilicon,
        "universal" => XcodeArchitecture.Universal,
        "intel" => XcodeArchitecture.Intel,
        _ => XcodeArchitecture.Unspecified,
    };

    [GeneratedRegex(@"\x1B(?:\[[0-?]*[ -/]*[@-~]|\][^\x07\x1B]*(?:\x07|\x1B\\)|[@-Z\\-_])")]
    private static partial Regex AnsiSequence();

    // Tab, line feed and carriage return are layout; backspaces and the like are terminal noise.
    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F\x7F]")]
    private static partial Regex ControlCharacters();

    [GeneratedRegex(@"\((?<parenthesized>[^()]*)\)|\[(?<architecture>[^\[\]]*)\]")]
    private static partial Regex Groups();

    [GeneratedRegex(@"^\((?<step>\d+)/(?<count>\d+)\)\s+(?<text>.+)$")]
    private static partial Regex StepLine();

    // Foundation writes 42%, and 42 % where the locale puts a space before the sign.
    [GeneratedRegex(@"(?<![\d.,])(?<percent>\d{1,3})(?:[.,]\d+)?\s*%")]
    private static partial Regex Percentage();

    [GeneratedRegex(@"(?<![\d.,-])(?<percent>\d{1,3}(?:[.,]\d+)?)\s*%")]
    private static partial Regex RuntimePercentage();
}
