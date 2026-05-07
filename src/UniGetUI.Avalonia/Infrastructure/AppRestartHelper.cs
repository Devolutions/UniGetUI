using Avalonia.Controls.ApplicationLifetimes;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class AppRestartHelper
{
    private const string LauncherExecutableName = "UniGetUI.exe";

    public static void Restart()
    {
        string executablePath = ResolveRestartExecutablePath(AppContext.BaseDirectory);
        CoreTools.ScheduleRelaunchAfterExit(executablePath);

        if (MainWindow.Instance is { } mainWindow)
        {
            mainWindow.QuitApplication();
            return;
        }

        (global::Avalonia.Application.Current?.ApplicationLifetime
            as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    internal static string ResolveRestartExecutablePath(string baseDirectory)
    {
        if (!OperatingSystem.IsWindows())
            return Environment.ProcessPath
                ?? throw new InvalidOperationException("Could not resolve the current executable path.");

        foreach (string candidate in GetWindowsLauncherCandidates(baseDirectory))
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return Environment.ProcessPath
            ?? throw new FileNotFoundException(
                $"Could not find the UniGetUI launcher '{LauncherExecutableName}'."
            );
    }

    private static IEnumerable<string> GetWindowsLauncherCandidates(string baseDirectory)
    {
        yield return Path.Combine(baseDirectory, "..", LauncherExecutableName);
        yield return Path.Combine(baseDirectory, LauncherExecutableName);

        var directory = new DirectoryInfo(Path.GetFullPath(baseDirectory));
        while (directory is not null)
        {
            string launcherBinDirectory = Path.Combine(directory.FullName, "UniGetUI", "bin");
            if (Directory.Exists(launcherBinDirectory))
            {
                foreach (
                    string candidate in Directory
                        .EnumerateFiles(
                            launcherBinDirectory,
                            LauncherExecutableName,
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
