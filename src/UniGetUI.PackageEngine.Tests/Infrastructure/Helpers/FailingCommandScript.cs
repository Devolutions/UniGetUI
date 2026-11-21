namespace UniGetUI.PackageEngine.Tests.Infrastructure.Helpers;

/// <summary>
/// Writes a script that stands in for a package manager executable: it prints the given lines to
/// stdout and stderr and exits with the given code, whatever arguments it receives.
/// </summary>
internal static class FailingCommandScript
{
    public static string Create(string directory, int exitCode, string stderr, params string[] stdout)
    {
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsWindows())
        {
            string path = Path.Combine(directory, $"listing-{Guid.NewGuid():N}.cmd");
            File.WriteAllLines(path,
            [
                "@echo off",
                .. stdout.Select(line => $"echo({EscapeBatch(line)}"),
                $"echo({EscapeBatch(stderr)} 1>&2",
                $"exit /b {exitCode}",
            ]);
            return path;
        }
        else
        {
            string path = Path.Combine(directory, $"listing-{Guid.NewGuid():N}.sh");
            File.WriteAllLines(path,
            [
                "#!/bin/sh",
                .. stdout.Select(line => $"echo '{line}'"),
                $"echo '{stderr}' >&2",
                $"exit {exitCode}",
            ]);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            return path;
        }
    }

    private static string EscapeBatch(string value) =>
        value.Replace("^", "^^").Replace("%", "%%").Replace("&", "^&")
            .Replace("|", "^|").Replace("<", "^<").Replace(">", "^>");
}
