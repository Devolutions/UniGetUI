using Avalonia.Controls.ApplicationLifetimes;
using UniGetUI.Avalonia.Views;
using UniGetUI.Core.Logging;
using UniGetUI.Core.Tools;

namespace UniGetUI.Avalonia.Infrastructure;

internal static class AppRestartHelper
{
    private const string LauncherExecutableName = "UniGetUI.exe";

    private sealed class RelaunchNotScheduledException : Exception;

    public static void Restart() => _ = RestartAsync();

    private static async Task RestartAsync()
    {
        string executablePath = ResolveRestartExecutablePath(AppContext.BaseDirectory);

        if (MainWindow.Instance is { } mainWindow)
        {
            // A throw from the callback makes the coordinator cancel the shutdown, so the window stays open.
            try
            {
                await mainWindow.RequestQuitApplicationAsync(() =>
                {
                    if (!CoreTools.TryScheduleRelaunchAfterExit(executablePath))
                        throw new RelaunchNotScheduledException();
                });
            }
            catch (RelaunchNotScheduledException)
            {
                Logger.Warn("Restart cancelled: the relaunch helper could not be started, UniGetUI stays open");
            }

            return;
        }

        if (!CoreTools.TryScheduleRelaunchAfterExit(executablePath))
        {
            Logger.Warn("Restart cancelled: the relaunch helper could not be started, UniGetUI stays open");
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
