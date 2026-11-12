using System.Text.RegularExpressions;

namespace UniGetUI.Extensions.Xcode;

/// <summary>Reduces what a failed command wrote to a message a person can read at a glance.</summary>
public static partial class XcodesError
{
    private const int MaximumLength = 500;

    /// <summary>
    /// When a command numbers its steps, the reason it stopped is whatever it wrote after the last
    /// step it reached. Without steps, it is the output itself. The hints it prints among the steps, and the menu it
    /// falls back to when a version is not installed, are not the reason.
    /// </summary>
    public static string Summarize(string output, int? exitCode = null)
    {
        string[] lines = XcodesOutputParser.SplitLines(output);
        int firstAfterSteps = Array.FindLastIndex(lines, line => XcodesOutputParser.TryParseProgress(line, out _)) + 1;

        List<string> reason = [];
        foreach (string line in lines[firstAfterSteps..])
        {
            if (line.StartsWith("Available Xcode versions:", StringComparison.Ordinal))
                break;
            if (IsHint(line))
                continue;
            reason.Add(line);
        }

        if (reason.Count == 0)
            return exitCode is { } code ? $"The command exited with code {code}." : "The command failed.";

        string message = string.Join(' ', reason);
        return message.Length <= MaximumLength ? message : message[..(MaximumLength - 3)] + "...";
    }

    /// <summary>Whether an installation error might need account or access recovery, not a diagnosis of its cause.</summary>
    public static bool IsPossibleAccessFailure(string text) => AccessFailure().IsMatch(text);

    [GeneratedRegex(@"\b(?:401|403|unauthori[sz]ed|forbidden)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AccessFailure();

    private static bool IsHint(string line)
        => line.StartsWith("Using regular unxip", StringComparison.Ordinal)
            || line.StartsWith("Using experimental unxip", StringComparison.Ordinal);
}
