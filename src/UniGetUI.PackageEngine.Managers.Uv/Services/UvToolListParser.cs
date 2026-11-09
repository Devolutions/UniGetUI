using System.Text.RegularExpressions;
using UniGetUI.PackageEngine.Managers.UvManager.Models;

namespace UniGetUI.PackageEngine.Managers.UvManager.Services;

internal static partial class UvToolListParser
{
    [GeneratedRegex(
        @"^(?<id>[A-Za-z0-9][A-Za-z0-9._-]*)\s+v(?<version>[A-Za-z0-9._+]+)(?<rest>.*)$",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex HeaderPattern();

    [GeneratedRegex(
        @"^-\s+(?<name>[^()\s]+)(?:\s+\((?<path>.+)\))?$",
        RegexOptions.CultureInvariant
    )]
    private static partial Regex ExecutablePattern();

    /// <summary>
    /// Parses the stdout of `uv tool list` into tool entries. Works with every flag combination
    /// (`--show-python`, `--show-version-specifiers`, `--show-paths`, `--outdated`); absent
    /// data simply stays null on the entry. Lines that are neither a tool header nor an
    /// executable line are ignored, so notices on stdout like "No tools installed" cannot
    /// corrupt the listing. An executable line before any header is dropped.
    /// Verified against uv 0.13.7; see docs/UV_ARCHITECTURE.md.
    /// </summary>
    internal static IReadOnlyList<UvTool> Parse(IEnumerable<string> lines)
    {
        List<UvTool> tools = [];
        string? activeId = null;
        string? activeVersion = null;
        string? activeSpecifier = null;
        string? activePython = null;
        string? activeLatest = null;
        string? activePath = null;
        List<string> activeExecutables = [];

        void Flush()
        {
            if (activeId is not null)
            {
                tools.Add(
                    new UvTool
                    {
                        PackageId = activeId,
                        Version = activeVersion!,
                        VersionSpecifier = activeSpecifier,
                        PythonVersion = activePython,
                        LatestVersion = activeLatest,
                        ToolEnvironmentPath = activePath,
                        Executables = [.. activeExecutables],
                    }
                );
            }
            activeId = null;
            activeExecutables = [];
        }

        foreach (string raw in lines)
        {
            string line = raw.TrimEnd();

            Match header = HeaderPattern().Match(line);
            if (header.Success)
            {
                Flush();
                ParseHeaderDetails(
                    header.Groups["rest"].Value,
                    out activeSpecifier,
                    out activePython,
                    out activeLatest,
                    out activePath
                );
                activeId = header.Groups["id"].Value;
                activeVersion = header.Groups["version"].Value;
                continue;
            }

            Match executable = ExecutablePattern().Match(line);
            if (executable.Success && activeId is not null)
                activeExecutables.Add(executable.Groups["name"].Value);
        }

        Flush();
        return tools;
    }

    /// <summary>
    /// Walks the annotations after "&lt;id&gt; v&lt;version&gt;": bracket tokens like
    /// "[required: ==0.5.0]", "[CPython 3.13.2]" and "[latest: 0.17.4]", plus the tool
    /// environment path between parentheses. Unknown brackets are dropped; a bracket without a
    /// closing token absorbs only to the end of the line.
    /// </summary>
    private static void ParseHeaderDetails(
        string rest,
        out string? specifier,
        out string? python,
        out string? latest,
        out string? path
    )
    {
        specifier = null;
        python = null;
        latest = null;
        path = null;
        int index = 0;

        while (index < rest.Length)
        {
            char start = rest[index];
            if (start is ' ' or '\t')
            {
                index++;
                continue;
            }

            char closer = start is '[' ? ']' : ')';
            int end = rest.IndexOf(closer, index);
            if (end < 0)
                break;

            string content = rest[(index + 1)..end].Trim();
            if (start is '(')
            {
                path = content;
            }
            else if (content.StartsWith("required:", StringComparison.Ordinal))
            {
                specifier = content["required:".Length..].Trim();
            }
            else if (content.StartsWith("latest:", StringComparison.Ordinal))
            {
                latest = content["latest:".Length..].Trim();
            }
            else if (!content.Contains(':'))
            {
                python = content;
            }

            index = end + 1;
        }
    }
}
