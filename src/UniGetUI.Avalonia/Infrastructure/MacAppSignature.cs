using System.Diagnostics;

namespace UniGetUI.Avalonia.Infrastructure;

/// <summary>
/// Checks that an extracted UniGetUI.app is signed with one of Devolutions' Developer ID certificates,
/// down to every file it seals, and reads the version it declares.
/// </summary>
internal static class MacAppSignature
{
    internal const string CodeSign = "/usr/bin/codesign";
    internal const string PlUtil = "/usr/bin/plutil";

    internal delegate (int ExitCode, string Output) ToolRunner(string tool, IReadOnlyList<string> arguments);

    /// <summary>A Developer ID Application certificate issued to one of the teams, chained to Apple's root.</summary>
    internal static string Requirement(IReadOnlyList<string> teamIds)
    {
        if (teamIds.Count == 0)
        {
            throw new ArgumentException("No Team Identifier was given.", nameof(teamIds));
        }

        foreach (string teamId in teamIds)
        {
            if (teamId.Length != 10 || !teamId.All(char.IsAsciiLetterOrDigit))
            {
                throw new ArgumentException($"'{teamId}' is not a Team Identifier.", nameof(teamIds));
            }
        }

        string teams = string.Join(" or ", teamIds.Select(t => $"certificate leaf[subject.OU] = \"{t}\""));
        return "anchor apple generic and certificate leaf[field.1.2.840.113635.100.6.1.13] exists "
            + $"and ({teams})";
    }

    public static bool Verify(string appBundle, string requirement, out string problem)
        => Verify(appBundle, requirement, RunTool, out problem);

    internal static bool Verify(string appBundle, string requirement, ToolRunner run, out string problem)
    {
        try
        {
            // --deep --strict covers nested code and fails on any file added to, changed in or missing from the seal.
            (int exitCode, string output) = run(
                CodeSign,
                ["--verify", "--deep", "--strict", $"-R={requirement}", appBundle]);
            if (exitCode == 0)
            {
                problem = "";
                return true;
            }

            problem = $"the app is not signed by a trusted team: {output.Trim()}";
            return false;
        }
        catch (Exception ex)
        {
            problem = $"the signature could not be checked: {ex.Message}";
            return false;
        }
    }

    public static string? ReadShortVersion(string appBundle) => ReadShortVersion(appBundle, RunTool);

    internal static string? ReadShortVersion(string appBundle, ToolRunner run)
    {
        try
        {
            (int exitCode, string output) = run(
                PlUtil,
                ["-extract", "CFBundleShortVersionString", "raw", "-o", "-", Path.Join(appBundle, "Contents", "Info.plist")]);
            return exitCode == 0 && !string.IsNullOrWhiteSpace(output) ? output.Trim() : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static (int ExitCode, string Output) RunTool(string tool, IReadOnlyList<string> arguments)
    {
        using Process process = new()
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = tool,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            },
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        // Read both pipes at once so a full one cannot stall the tool.
        Task<string> stdout = process.StandardOutput.ReadToEndAsync();
        Task<string> stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        return (process.ExitCode, stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult());
    }
}
