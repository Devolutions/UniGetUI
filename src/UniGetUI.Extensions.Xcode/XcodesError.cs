namespace UniGetUI.Extensions.Xcode;

/// <summary>Reduces what a failed xcodes command wrote to a message a person can read at a glance.</summary>
public static class XcodesError
{
    private const int MaximumLength = 500;

    /// <summary>
    /// xcodes numbers the steps of an installation, so the reason it stopped is whatever it wrote after the last
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
            return exitCode is { } code ? $"xcodes exited with code {code}." : "xcodes failed.";

        string message = string.Join(' ', reason);
        return message.Length <= MaximumLength ? message : message[..(MaximumLength - 3)] + "...";
    }

    private static bool IsHint(string line)
        => line.StartsWith("Using regular unxip", StringComparison.Ordinal)
            || line.StartsWith("Using experimental unxip", StringComparison.Ordinal);
}
