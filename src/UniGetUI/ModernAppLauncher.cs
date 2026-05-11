using System.Diagnostics;
using UniGetUI.Core.Logging;
using UniGetUI.Core.SettingsEngine;

namespace UniGetUI;

internal static class ModernAppLauncher
{
    internal const string ModernAppDirectoryName = "Avalonia";
    internal const string ModernAppExecutableName = "UniGetUI.Avalonia.exe";

    public static bool IsClassicModeEnabled() =>
        !Settings.Get(Settings.K.DisableClassicMode) && !Settings.Get(Settings.K.EnableUniGetUIBeta);

    public static void Launch(string[] args)
    {
        string executablePath = ResolveModernExecutablePath(AppContext.BaseDirectory);
        using Process process =
            Process.Start(CreateStartInfo(executablePath, args))
            ?? throw new InvalidOperationException(
                $"Could not launch modern UniGetUI from '{executablePath}'"
            );

        Logger.Info($"Launched modern UniGetUI from {executablePath} with PID {process.Id}");
    }

    internal static string ResolveModernExecutablePath(string baseDirectory)
    {
        foreach (string candidate in GetModernExecutableCandidates(baseDirectory))
        {
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException(
            $"Modern UniGetUI executable '{ModernAppExecutableName}' was not found.",
            Path.Combine(baseDirectory, ModernAppDirectoryName, ModernAppExecutableName)
        );
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath, IEnumerable<string> args)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory =
                Path.GetDirectoryName(executablePath)
                ?? throw new DirectoryNotFoundException(
                    $"Could not resolve directory for '{executablePath}'"
                ),
            UseShellExecute = false,
        };

        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);

        return startInfo;
    }

    private static IEnumerable<string> GetModernExecutableCandidates(string baseDirectory)
    {
        yield return Path.Combine(baseDirectory, ModernAppExecutableName);
        yield return Path.Combine(
            baseDirectory,
            ModernAppDirectoryName,
            ModernAppExecutableName
        );

        foreach (string candidate in GetDevelopmentBuildCandidates(baseDirectory))
            yield return candidate;
    }

    private static IEnumerable<string> GetDevelopmentBuildCandidates(string baseDirectory)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(baseDirectory));
        while (directory is not null)
        {
            string avaloniaBinDirectory = Path.Combine(
                directory.FullName,
                "UniGetUI.Avalonia",
                "bin"
            );
            if (Directory.Exists(avaloniaBinDirectory))
            {
                foreach (
                    string candidate in Directory
                        .EnumerateFiles(
                            avaloniaBinDirectory,
                            ModernAppExecutableName,
                            SearchOption.AllDirectories
                        )
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                )
                {
                    yield return candidate;
                }
            }

            directory = directory.Parent;
        }
    }
}
