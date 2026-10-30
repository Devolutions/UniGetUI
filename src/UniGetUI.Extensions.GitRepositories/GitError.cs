namespace UniGetUI.Extensions.GitRepositories;

/// <summary>Separates actionable git diagnostics from fetch progress and pruning output.</summary>
public static class GitError
{
    public static string Summarize(string output)
    {
        string[] lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string summary = lines.FirstOrDefault(IsDiagnostic) ?? lines.LastOrDefault() ?? "";
        return summary.Length > 500 ? summary[..497] + "..." : summary;
    }

    private static bool IsDiagnostic(string line)
        => line.StartsWith("error:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("remote: error:", StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("remote: fatal:", StringComparison.OrdinalIgnoreCase);

    internal static bool IsTransientFetchFailure(string output)
    {
        // A ref-name conflict or a broken ref also says "cannot lock ref", but retrying cannot fix it.
        bool refContention = output.Contains("cannot lock ref", StringComparison.OrdinalIgnoreCase)
            && (output.Contains("is at ", StringComparison.OrdinalIgnoreCase)
                && output.Contains("but expected ", StringComparison.OrdinalIgnoreCase)
                || output.Contains(".lock", StringComparison.OrdinalIgnoreCase)
                && output.Contains("File exists", StringComparison.OrdinalIgnoreCase));
        return refContention
            || output.Contains("Connection reset", StringComparison.OrdinalIgnoreCase)
            || output.Contains("remote end hung up unexpectedly", StringComparison.OrdinalIgnoreCase)
            || output.Contains("early EOF", StringComparison.OrdinalIgnoreCase)
            || output.Contains("The requested URL returned error: 502", StringComparison.OrdinalIgnoreCase)
            || output.Contains("The requested URL returned error: 503", StringComparison.OrdinalIgnoreCase)
            || output.Contains("The requested URL returned error: 504", StringComparison.OrdinalIgnoreCase);
    }
}
